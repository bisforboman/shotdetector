using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FrameReader;

/// <summary>
/// The picture's measures of frames the caller decodes, one pass over each frame's planes, with nothing of FFmpeg (8 to
/// 16 bits, 4:2:0, 4:2:2 or 4:4:4; <see cref="PictureFormat"/>). What it keeps per frame is what FFmpeg 8.1's filters
/// count at the frame's depth, so that <see cref="PictureMeasures"/> gives their figures exactly: <c>signalstats</c>'
/// YMIN, YAVG, YMAX and BRNG, <c>blackdetect</c>'s black pixels, <c>cropdetect</c>'s first and last lines over its
/// limit, and whether the frame is colour bars (by their definition: FFmpeg has no filter for them); with them the
/// decoder's picture type. Integer counts over the same planes, so two decoders that give the same planes give the same
/// figures. Not thread-safe: one caller feeds it; ranges of a video measured by meters of their own join
/// (<see cref="PictureMeasures.Join"/>).
/// </summary>
/// <example>
/// <code>
/// using var reader = new VideoFrameReader("delivery.mov", new() { Decoder = new() { Autorotate = false } });
/// PictureMeter? meter = null;
/// while (reader.TryTake(out var frame))
///     using (frame)
///         (meter ??= new PictureMeter(PictureFormat.Of(frame, reader.TimeBase)!)).Add(frame);
/// var black = meter!.Measures.BlackSpans();
/// </code>
/// </example>
public sealed class PictureMeter
{
    // cropdetect's limit (24/255, a float option), FFmpeg 8.1's default.
    const float CropLimit = (float)(24.0 / 255);

    readonly int _width, _height, _hsub, _vsub, _cw, _ch, _bytes, _factor;
    readonly bool _wide;
    readonly uint _black;
    readonly int _cropLimit;

    // The clockwise quarter turn that makes the decoded frame upright (0, 90, 180, 270) and the upright size.
    readonly int _turn, _uprightWidth, _uprightHeight;
    readonly int _first, _end;
    readonly BarColours _bars;
    int _index;

    /// <summary>A meter of a video's frames, from its first.</summary>
    /// <param name="format">The frames' size, range, time base, rotation, depth and chroma.</param>
    /// <exception cref="ArgumentException">The size or time base isn't positive, the depth isn't 8 to 16, or the rotation isn't a quarter turn.</exception>
    public PictureMeter(PictureFormat format)
        : this(format, 0, int.MaxValue)
    {
    }

    /// <summary>
    /// A meter of frames <paramref name="firstFrame"/> up to <paramref name="endFrame"/> of a video (a range of a run
    /// split for parallel reading): the first frame given is <paramref name="firstFrame"/>; frames given from
    /// <paramref name="endFrame"/> on are left out.
    /// </summary>
    /// <param name="format">The frames' size, range, time base, rotation, depth and chroma.</param>
    /// <param name="firstFrame">The first frame's index in the video (display order).</param>
    /// <param name="endFrame">The index after the last frame measured.</param>
    /// <exception cref="ArgumentException">The size or time base isn't positive, the depth isn't 8 to 16, or the rotation isn't a quarter turn.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstFrame"/> is negative or <paramref name="endFrame"/> under it.</exception>
    public PictureMeter(PictureFormat format, int firstFrame, int endFrame)
        : this(format, firstFrame, endFrame, PictureMeasures.DefaultBlackPixelThreshold)
    {
    }

    /// <summary>
    /// <see cref="PictureMeter(PictureFormat, int, int)"/> counting black pixels as blackdetect does with
    /// <c>pix_th=<paramref name="blackPixelThreshold"/></c> (pixel_black_th; 0.10, its default, in the other constructors).
    /// </summary>
    /// <param name="format">The frames' size, range, time base, rotation, depth and chroma.</param>
    /// <param name="firstFrame">The first frame's index in the video (display order).</param>
    /// <param name="endFrame">The index after the last frame measured.</param>
    /// <param name="blackPixelThreshold">The luma at or under which a pixel is black, as a share of the range above black (0 to 1).</param>
    /// <exception cref="ArgumentException">The size or time base isn't positive, the depth isn't 8 to 16, or the rotation isn't a quarter turn.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstFrame"/> is negative or <paramref name="endFrame"/> under it, or <paramref name="blackPixelThreshold"/> isn't 0 to 1.</exception>
    public PictureMeter(PictureFormat format, int firstFrame, int endFrame, double blackPixelThreshold)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (!(blackPixelThreshold is >= 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(blackPixelThreshold), blackPixelThreshold, "blackdetect's pix_th is 0 to 1.");
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("The frame's size must be positive.", nameof(format));
        if (format.TimeBase.Num <= 0 || format.TimeBase.Den <= 0)
            throw new ArgumentException("The time base must be positive.", nameof(format));
        if (format.BitDepth is < 8 or > 16)
            throw new ArgumentException("The bit depth must be 8 to 16.", nameof(format));
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentOutOfRangeException.ThrowIfLessThan(endFrame, firstFrame);
        _turn = ((-format.Rotation % 360) + 360) % 360;
        if (_turn % 90 != 0)
            throw new ArgumentException("The rotation must be a multiple of 90 degrees.", nameof(format));
        (_width, _height, _cw, _ch) = (format.Width, format.Height, format.ChromaWidth, format.ChromaHeight);
        (_hsub, _vsub) = format.Chroma switch
        {
            ChromaSubsampling.Yuv420 => (1, 1),
            ChromaSubsampling.Yuv422 => (1, 0),
            _ => (0, 0),
        };
        (_uprightWidth, _uprightHeight) = _turn % 180 == 90 ? (_height, _width) : (_width, _height);
        (_bytes, _wide) = (format.BytesPerSample, format.BitDepth > 8);
        int max = (1 << format.BitDepth) - 1;
        _factor = 1 << (format.BitDepth - 8);

        // blackdetect's pixel_black_th_i, an unsigned int (37 and 25 at 8 bits, 151 and 102 at 10, at its default 0.1).
        _black = BlackPixels.Threshold(format.BitDepth, format.FullRange, blackPixelThreshold);

        // cropdetect's limit_upscaled: the float limit times max, in single precision, then lrint (24 at 8 bits, 96 at 10).
        _cropLimit = (int)Math.Round((double)(CropLimit * max), MidpointRounding.ToEven);
        _bars = BarColours.For(format.FullRange, _factor);
        _first = firstFrame;
        _end = endFrame;
        Measures = new PictureMeasures(_uprightWidth, _uprightHeight, format.FullRange, format.BitDepth, format.TimeBase, firstFrame, blackPixelThreshold);
    }

    /// <summary>The frames measured so far.</summary>
    public PictureMeasures Measures { get; }

    /// <summary>Measures the next frame given, if it is one of the meter's.</summary>
    /// <param name="y">The luma plane: <see cref="PictureFormat.Height"/> rows of <paramref name="yStride"/> bytes (the last row may end at its width).</param>
    /// <param name="yStride">The bytes from one luma row to the next.</param>
    /// <param name="u">The Cb plane, <see cref="PictureFormat.ChromaWidth"/> × <see cref="PictureFormat.ChromaHeight"/>.</param>
    /// <param name="uStride">The bytes from one Cb row to the next.</param>
    /// <param name="v">The Cr plane, as the Cb one.</param>
    /// <param name="vStride">The bytes from one Cr row to the next.</param>
    /// <param name="pts">The frame's presentation time, in <see cref="PictureFormat.TimeBase"/> units.</param>
    /// <param name="type">The decoder's picture type, kept with the measures.</param>
    /// <exception cref="ArgumentException">A plane is smaller than the format says.</exception>
    public void Add(ReadOnlySpan<byte> y,
        int yStride,
        ReadOnlySpan<byte> u,
        int uStride,
        ReadOnlySpan<byte> v,
        int vStride,
        long pts,
        PictureType type = PictureType.None)
    {
        Check(y, yStride, _width, _height, nameof(y));
        Check(u, uStride, _cw, _ch, nameof(u));
        Check(v, vStride, _cw, _ch, nameof(v));
        int index = _first + _index++;
        if (index >= _end)
            return;
        var p = new Planes(y, yStride, u, uStride, v, vStride, _wide);
        var luma = _wide ? Luma16(p) : Luma8(p);
        int brng = luma.OutOfRange + ChromaOutOfRange(p);
        var (top, bottom, left, right) = _turn == 0 ? ContentLines(p) : TurnedContentLines(p);
        bool bars = InRow(p, _uprightHeight / 4) && InRow(p, _uprightHeight / 2);
        Measures.Add(pts, luma.Min, luma.Max, luma.Sum, luma.Black, brng, top, bottom, left, right, bars, type);
    }

    /// <summary>Measures a decoded frame (its planes, pts and picture type), if it is one of the meter's.</summary>
    /// <param name="frame">A frame of the meter's format (<see cref="PictureFormat.Of"/>), taken as coded.</param>
    /// <exception cref="ArgumentException">The frame's size, depth or chroma isn't the format's.</exception>
    public void Add(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (depth, chroma) = frame.YuvLayout();
        if (frame.Width != _width || frame.Height != _height || depth != Measures.BitDepth || _cw != (chroma == ChromaSubsampling.Yuv444 ? _width : (_width + 1) / 2)
            || _ch != (chroma == ChromaSubsampling.Yuv420 ? (_height + 1) / 2 : _height))
        {
            throw new ArgumentException($"The frame ({frame.Width}x{frame.Height} {frame.PixelFormat}) isn't the meter's format.", nameof(frame));
        }

        var y = frame.GetPlane(0, out int ys);
        var u = frame.GetPlane(1, out int us);
        var v = frame.GetPlane(2, out int vs);
        Add(y, ys, u, us, v, vs, frame.Pts, frame.PictureType);
    }

    void Check(ReadOnlySpan<byte> plane, int stride, int width, int height, string name)
    {
        int rowBytes = width * _bytes;
        if (stride < rowBytes || plane.Length < ((long)stride * (height - 1)) + rowBytes)
            throw new ArgumentException($"The plane is smaller than {width}x{height} samples of {_bytes} bytes with a stride of {stride}.", name);
    }

    /// <summary>The planes of one frame; samples of 16 bits when wide.</summary>
    readonly ref struct Planes(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, int uStride, ReadOnlySpan<byte> v, int vStride, bool wide)
    {
        public readonly ReadOnlySpan<byte> Y = y, U = u, V = v;
        public readonly int YStride = yStride, UStride = uStride, VStride = vStride;
        public readonly bool Wide = wide;

        public int Luma(int x, int row) => Wide ? MemoryMarshal.Read<ushort>(Y[((row * YStride) + (2 * x))..]) : Y[(row * YStride) + x];

        public int Cb(int x, int row) => Wide ? MemoryMarshal.Read<ushort>(U[((row * UStride) + (2 * x))..]) : U[(row * UStride) + x];

        public int Cr(int x, int row) => Wide ? MemoryMarshal.Read<ushort>(V[((row * VStride) + (2 * x))..]) : V[(row * VStride) + x];

        public ReadOnlySpan<ushort> LumaRow16(int row, int width) => MemoryMarshal.Cast<byte, ushort>(Y.Slice(row * YStride, 2 * width));
    }

    /// <summary>8 bits: the luma's sum, minimum and maximum, the pixels at or under blackdetect's threshold and those outside 16 to 235.</summary>
    (long Sum, int Min, int Max, int Black, int OutOfRange) Luma8(in Planes p)
    {
        var low = Vector128.Create((byte)255);
        var high = Vector128<byte>.Zero;
        var black = Vector128.Create((byte)_black);
        var under16 = Vector128.Create((byte)15);
        var over235 = Vector128.Create((byte)235);
        long sum = 0, blacks = 0, outside = 0;
        int min = 255, max = 0;
        for (int r = 0; r < _height; r++)
        {
            var row = p.Y.Slice(r * p.YStride, _width);
            var rowSum = Vector128<uint>.Zero;
            var rowBlack = Vector128<byte>.Zero;
            var rowOutside = Vector128<byte>.Zero;
            int x = 0, n = 0;
            for (; x + 16 <= _width; x += 16)
            {
                var v = Vector128.Create(row.Slice(x, 16));
                low = Vector128.Min(low, v);
                high = Vector128.Max(high, v);
                var (a, b) = Vector128.Widen(v);
                var (c, d) = Vector128.Widen(a + b);
                rowSum += c + d;

                // A compare is all ones where true: subtracting it counts, each byte lane up to 255 times.
                rowBlack -= Vector128.LessThanOrEqual(v, black);
                rowOutside -= Vector128.LessThanOrEqual(v, under16) | Vector128.GreaterThan(v, over235);
                if (++n == 255)
                {
                    blacks += Sum(rowBlack);
                    outside += Sum(rowOutside);
                    rowBlack = rowOutside = Vector128<byte>.Zero;
                    n = 0;
                }
            }

            blacks += Sum(rowBlack);
            outside += Sum(rowOutside);
            sum += Vector128.Sum(rowSum);
            for (; x < _width; x++)
            {
                int q = row[x];
                sum += q;
                min = Math.Min(min, q);
                max = Math.Max(max, q);
                if (q <= _black)
                    blacks++;
                if (q < 16 || q > 235)
                    outside++;
            }
        }

        if (_width >= 16)
        {
            for (int i = 0; i < 16; i++)
            {
                min = Math.Min(min, low.GetElement(i));
                max = Math.Max(max, high.GetElement(i));
            }
        }

        return (sum, min, max, (int)blacks, (int)outside);
    }

    /// <summary>Over 8 bits: as <see cref="Luma8"/>, the broadcast range scaled by 2^(depth − 8) as signalstats does.</summary>
    (long Sum, int Min, int Max, int Black, int OutOfRange) Luma16(in Planes p)
    {
        long sum = 0;
        int min = int.MaxValue, max = 0, blacks = 0, outside = 0;
        int under = 16 * _factor, over = 235 * _factor;
        for (int r = 0; r < _height; r++)
        {
            foreach (int q in p.LumaRow16(r, _width))
            {
                sum += q;
                min = Math.Min(min, q);
                max = Math.Max(max, q);
                if ((uint)q <= _black)
                    blacks++;
                if (q < under || q > over)
                    outside++;
            }
        }

        return (sum, min, max, blacks, outside);
    }

    static long Sum(Vector128<byte> counts)
    {
        var (a, b) = Vector128.Widen(counts);
        return Vector128.Sum(a + b);
    }

    /// <summary>
    /// signalstats' BRNG counts a luma pixel when it or its chroma sample is out of range (16 to 240, scaled to the
    /// depth): the luma pixels in range under each chroma sample out of range, which the luma pass didn't count.
    /// </summary>
    int ChromaOutOfRange(in Planes p)
    {
        int count = 0;
        if (!p.Wide)
        {
            var under16 = Vector128.Create((byte)15);
            var over240 = Vector128.Create((byte)240);
            for (int r = 0; r < _ch; r++)
            {
                var ur = p.U.Slice(r * p.UStride, _cw);
                var vr = p.V.Slice(r * p.VStride, _cw);
                int x = 0;
                for (; x + 16 <= _cw; x += 16)
                {
                    var a = Vector128.Create(ur.Slice(x, 16));
                    var b = Vector128.Create(vr.Slice(x, 16));
                    var outside = Vector128.LessThanOrEqual(a, under16) | Vector128.GreaterThan(a, over240)
                        | Vector128.LessThanOrEqual(b, under16) | Vector128.GreaterThan(b, over240);
                    if (outside == Vector128<byte>.Zero)
                        continue;
                    for (int i = 0; i < 16; i++)
                    {
                        if (outside.GetElement(i) != 0)
                            count += LumaInRange(p, x + i, r);
                    }
                }

                for (; x < _cw; x++)
                {
                    if (ur[x] < 16 || ur[x] > 240 || vr[x] < 16 || vr[x] > 240)
                        count += LumaInRange(p, x, r);
                }
            }

            return count;
        }

        int under = 16 * _factor, over = 240 * _factor;
        for (int r = 0; r < _ch; r++)
        {
            for (int x = 0; x < _cw; x++)
            {
                int a = p.Cb(x, r), b = p.Cr(x, r);
                if (a < under || a > over || b < under || b > over)
                    count += LumaInRange(p, x, r);
            }
        }

        return count;
    }

    /// <summary>The luma pixels under chroma sample (<paramref name="cx"/>, <paramref name="cy"/>) within 16 to 235, scaled to the depth.</summary>
    int LumaInRange(in Planes p, int cx, int cy)
    {
        int n = 0, under = 16 * _factor, over = 235 * _factor;
        for (int r = cy << _vsub; r < Math.Min(_height, (cy + 1) << _vsub); r++)
        {
            for (int x = cx << _hsub; x < Math.Min(_width, (cx + 1) << _hsub); x++)
            {
                int q = p.Luma(x, r);
                if (q >= under && q <= over)
                    n++;
            }
        }

        return n;
    }

    /// <summary>
    /// cropdetect's lines: a line whose mean luma (an integer, rounded down) is over its limit has content. The first
    /// row with content above the last (cropdetect never looks at the last for it), the last row with content, the same
    /// for columns; -1 for none. <see cref="PictureMeasures"/> replays cropdetect's scan over them.
    /// </summary>
    (short Top, short Bottom, short Left, short Right) ContentLines(in Planes p)
    {
        int top = -1, bottom = -1, left = -1, right = -1;
        for (int r = 0; r < _height - 1 && top < 0; r++)
        {
            if (RowSum(p, r) / _width > _cropLimit)
                top = r;
        }

        // Without content above the last row, only the last row can have it.
        for (int r = _height - 1; r >= Math.Max(top, top < 0 ? _height - 1 : 0) && bottom < 0; r--)
        {
            if (RowSum(p, r) / _width > _cropLimit)
                bottom = r;
        }

        Span<ulong> sums = stackalloc ulong[16];
        for (int c0 = 0; c0 < _width - 1 && left < 0; c0 += 16)
        {
            int n = ColumnSums(p, c0, sums);
            for (int j = 0; j < n && c0 + j < _width - 1; j++)
            {
                if (sums[j] / (ulong)_height > (ulong)_cropLimit)
                {
                    left = c0 + j;
                    break;
                }
            }
        }

        int lowest = left < 0 ? _width - 1 : left;
        for (int c1 = _width - 1; c1 >= lowest && right < 0; c1 -= 16)
        {
            int c0 = Math.Max(0, c1 - 15);
            ColumnSums(p, c0, sums);
            for (int c = c1; c >= Math.Max(c0, lowest); c--)
            {
                if (sums[c - c0] / (ulong)_height > (ulong)_cropLimit)
                {
                    right = c;
                    break;
                }
            }
        }

        return ((short)top, (short)bottom, (short)left, (short)right);
    }

    /// <summary>
    /// <see cref="ContentLines"/> of the upright picture, for a rotated video: whether each decoded row and column has
    /// content (a line's mean is the same whichever way it is read), then cropdetect's first and last lines in the
    /// upright picture's order.
    /// </summary>
    (short Top, short Bottom, short Left, short Right) TurnedContentLines(in Planes p)
    {
        var rows = new bool[_height];
        for (int r = 0; r < _height; r++)
            rows[r] = RowSum(p, r) / _width > _cropLimit;
        var columns = new bool[_width];
        Span<ulong> sums = stackalloc ulong[16];
        for (int c0 = 0; c0 < _width; c0 += 16)
        {
            int n = ColumnSums(p, c0, sums);
            for (int j = 0; j < n; j++)
                columns[c0 + j] = sums[j] / (ulong)_height > (ulong)_cropLimit;
        }

        // The upright picture's lines (cv::rotate's): turned clockwise by 90, its row r is decoded column r and its column
        // c decoded row H-1-c; by 180, row H-1-r and column W-1-c; by 270, column W-1-r and row c.
        var (w, h) = (_width, _height);
        Func<int, bool> uprightRow = _turn switch
        {
            90 => r => columns[r],
            180 => r => rows[h - 1 - r],
            _ => r => columns[w - 1 - r],
        };
        Func<int, bool> uprightColumn = _turn switch
        {
            90 => c => rows[h - 1 - c],
            180 => c => columns[w - 1 - c],
            _ => c => rows[c],
        };
        var (top, bottom) = FirstAndLast(uprightRow, _uprightHeight);
        var (left, right) = FirstAndLast(uprightColumn, _uprightWidth);
        return ((short)top, (short)bottom, (short)left, (short)right);

        // As ContentLines: the first line with content short of the last, then the last at or after it (only the last
        // line when none was).
        static (int First, int Last) FirstAndLast(Func<int, bool> content, int count)
        {
            int first = -1, last = -1;
            for (int i = 0; i < count - 1 && first < 0; i++)
            {
                if (content(i))
                    first = i;
            }

            for (int i = count - 1; i >= (first < 0 ? count - 1 : first) && last < 0; i--)
            {
                if (content(i))
                    last = i;
            }

            return (first, last);
        }
    }

    long RowSum(in Planes p, int r)
    {
        if (p.Wide)
        {
            long total16 = 0;
            foreach (ushort q in p.LumaRow16(r, _width))
                total16 += q;
            return total16;
        }

        var row = p.Y.Slice(r * p.YStride, _width);
        var sum = Vector128<uint>.Zero;
        int x = 0;
        for (; x + 16 <= row.Length; x += 16)
        {
            var (a, b) = Vector128.Widen(Vector128.Create(row.Slice(x, 16)));
            var (c, d) = Vector128.Widen(a + b);
            sum += c + d;
        }

        long total = Vector128.Sum(sum);
        for (; x < row.Length; x++)
            total += row[x];
        return total;
    }

    /// <summary>The sums over every row of columns <paramref name="c0"/> on, sixteen or up to the last: how many.</summary>
    int ColumnSums(in Planes p, int c0, Span<ulong> sums)
    {
        int n = Math.Min(16, _width - c0);
        sums.Clear();
        if (n == 16 && !p.Wide)
        {
            Vector128<uint> s0 = default, s1 = default, s2 = default, s3 = default;
            for (int r = 0; r < _height; r++)
            {
                var (a, b) = Vector128.Widen(Vector128.Create(p.Y.Slice((r * p.YStride) + c0, 16)));
                var (a0, a1) = Vector128.Widen(a);
                var (b0, b1) = Vector128.Widen(b);
                s0 += a0;
                s1 += a1;
                s2 += b0;
                s3 += b1;
            }

            for (int i = 0; i < 4; i++)
            {
                sums[i] = s0.GetElement(i);
                sums[4 + i] = s1.GetElement(i);
                sums[8 + i] = s2.GetElement(i);
                sums[12 + i] = s3.GetElement(i);
            }

            return 16;
        }

        for (int r = 0; r < _height; r++)
        {
            for (int j = 0; j < n; j++)
                sums[j] += (ulong)p.Luma(c0 + j, r);
        }

        return n;
    }

    /// <summary>
    /// Whether the upright picture's row <paramref name="row"/> is colour bars (<see cref="BarColours"/>): read at the
    /// upright picture's chroma resolution, each column's luma the mean of the pixels over its chroma sample (two in
    /// 4:2:0 and 4:2:2), its chroma the sample under its first pixel, from the decoded planes as the rotation turns them.
    /// </summary>
    bool InRow(in Planes p, int row)
    {
        int step = _turn % 180 == 90 ? _vsub : _hsub;
        int columns = ((_uprightWidth - 1) >> step) + 1;
        Span<int> luma = columns <= 4096 ? stackalloc int[columns] : new int[columns];
        Span<int> blue = columns <= 4096 ? stackalloc int[columns] : new int[columns];
        Span<int> red = columns <= 4096 ? stackalloc int[columns] : new int[columns];
        for (int c = 0; c < columns; c++)
        {
            int x0 = c << step;
            var (dx, dy) = Decoded(x0, row);
            luma[c] = step == 1 && x0 + 1 < _uprightWidth ? (p.Luma(dx, dy) + UprightLuma(p, x0 + 1, row) + 1) >> 1 : p.Luma(dx, dy);
            blue[c] = p.Cb(dx >> _hsub, dy >> _vsub);
            red[c] = p.Cr(dx >> _hsub, dy >> _vsub);
        }

        return _bars.InRow(luma, blue, red);
    }

    int UprightLuma(in Planes p, int x, int row)
    {
        var (dx, dy) = Decoded(x, row);
        return p.Luma(dx, dy);
    }

    /// <summary>The decoded pixel of the upright picture's (<paramref name="x"/>, <paramref name="row"/>) (cv::rotate's, as <see cref="TurnedContentLines"/>).</summary>
    (int X, int Y) Decoded(int x, int row) => _turn switch
    {
        0 => (x, row),
        90 => (row, _height - 1 - x),
        180 => (_width - 1 - x, _height - 1 - row),
        _ => (_width - 1 - row, x),
    };
}

/// <summary>
/// Colour bars by their definition, as FFmpeg draws them (<c>smptebars</c>, <c>smptehdbars</c>, <c>pal75bars</c>,
/// <c>pal100bars</c>): seven bars side by side, left to right white, yellow, cyan, green, magenta, red and blue, each
/// within <see cref="Tolerance"/> code values of its Y, U and V at 75% or 100%, in BT.601's or BT.709's matrix (one set
/// for all seven; the white may be 100% with 75% colours, EBU's 100/0/75/0 that <c>pal75bars</c> draws), each at least a
/// twelfth of the width and none more than 1.5 times another, other columns between them at most three chroma samples (a
/// coded edge). Over 8 bits the levels and the tolerance are scaled by 2^(depth − 8).
/// </summary>
sealed class BarColours
{
    const int Tolerance = 24;

    // Y, U, V in limited range (vsrc_testsrc.c's rainbow, rainbow100, rainbowhd; BT.709 at 100% from its matrix).
    static readonly byte[][,] Limited =
    [
        new byte[,] { { 180, 128, 128 }, { 162, 44, 142 }, { 131, 156, 44 }, { 112, 72, 58 }, { 84, 184, 198 }, { 65, 100, 212 }, { 35, 212, 114 } },
        new byte[,] { { 235, 128, 128 }, { 210, 16, 146 }, { 170, 166, 16 }, { 145, 54, 34 }, { 106, 202, 222 }, { 81, 90, 240 }, { 41, 240, 110 } },
        new byte[,] { { 180, 128, 128 }, { 168, 44, 136 }, { 145, 147, 44 }, { 133, 63, 52 }, { 63, 193, 204 }, { 51, 109, 212 }, { 28, 212, 120 } },
        new byte[,] { { 235, 128, 128 }, { 219, 16, 138 }, { 188, 154, 16 }, { 173, 42, 26 }, { 78, 214, 230 }, { 63, 102, 240 }, { 32, 240, 118 } },
    ];

    readonly int[][,] _sets;

    // 100% white (the 100% BT.601 set's first bar), which EBU's 100/0/75/0 bars put before 75% colours.
    readonly int[,] _white;
    readonly int _tolerance;

    BarColours(byte[][,] sets, int factor)
    {
        _sets = [.. sets.Select(s => Scale(s, factor))];
        _white = new int[,] { { _sets[1][0, 0], _sets[1][0, 1], _sets[1][0, 2] } };
        _tolerance = Tolerance * factor;
    }

    public static BarColours For(bool fullRange, int factor) => new(fullRange ? [.. Limited.Select(ToFull)] : Limited, factor);

    static int[,] Scale(byte[,] set, int factor)
    {
        var scaled = new int[set.GetLength(0), 3];
        for (int i = 0; i < set.GetLength(0); i++)
        {
            for (int c = 0; c < 3; c++)
                scaled[i, c] = set[i, c] * factor;
        }

        return scaled;
    }

    static byte[,] ToFull(byte[,] set)
    {
        var full = new byte[7, 3];
        for (int i = 0; i < 7; i++)
        {
            full[i, 0] = (byte)Math.Clamp(Math.Round((set[i, 0] - 16) * 255 / 219.0), 0, 255);
            for (int c = 1; c < 3; c++)
                full[i, c] = (byte)Math.Clamp(Math.Round(((set[i, c] - 128) * 255 / 224.0) + 128), 0, 255);
        }

        return full;
    }

    /// <summary>Whether a row's columns (each a luma, Cb and Cr) are the seven bars in one of the sets.</summary>
    public bool InRow(ReadOnlySpan<int> y, ReadOnlySpan<int> u, ReadOnlySpan<int> v)
    {
        int columns = y.Length;
        Span<sbyte> labels = columns <= 4096 ? stackalloc sbyte[columns] : new sbyte[columns];
        foreach (var set in _sets)
        {
            for (int c = 0; c < columns; c++)
                labels[c] = Nearest(set, y[c], u[c], v[c]) is var i and >= 0 ? i : Nearest(_white, y[c], u[c], v[c]);
            if (Sequence(labels, columns))
                return true;
        }

        return false;
    }

    sbyte Nearest(int[,] set, int y, int u, int v)
    {
        for (int i = 0; i < set.GetLength(0); i++)
        {
            if (Math.Abs(y - set[i, 0]) <= _tolerance && Math.Abs(u - set[i, 1]) <= _tolerance && Math.Abs(v - set[i, 2]) <= _tolerance)
                return (sbyte)i;
        }

        return -1;
    }

    /// <summary>Runs of labels 0 to 6 in order, each wide enough, with at most three other columns between them.</summary>
    static bool Sequence(ReadOnlySpan<sbyte> labels, int columns)
    {
        int least = Math.Max(2, columns / 12);
        for (int start = 0; start < columns; start++)
        {
            if (labels[start] != 0 || (start > 0 && labels[start - 1] == 0))
                continue;
            int at = start, narrowest = int.MaxValue, widest = 0;
            bool found = true;
            for (int bar = 0; bar < 7 && found; bar++)
            {
                int gap = 0;
                while (at < columns && labels[at] != bar && gap <= 2)
                {
                    at++;
                    gap++;
                }

                int begin = at;
                while (at < columns && labels[at] == bar)
                    at++;
                int run = at - begin;
                found = run >= least && (bar > 0 || gap == 0);
                narrowest = Math.Min(narrowest, run);
                widest = Math.Max(widest, run);
            }

            if (found && widest * 2 <= narrowest * 3)
                return true;
        }

        return false;
    }
}
