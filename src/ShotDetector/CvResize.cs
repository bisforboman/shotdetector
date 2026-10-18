using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ShotDetector;

/// <summary>
/// Port of cv2.resize(..., interpolation=INTER_LINEAR) for 8-bit 3-channel images
/// (imgproc/resize.cpp), matching OpenCV's fixed-point arithmetic: 11-bit coefficients, an exact
/// horizontal pass, then the vertical pass as done by its SIMD path (VResizeLinearVec_32s8u).
/// When downscaling it samples only the 2x2 nearest source pixels, so fine detail aliases, and it
/// reads only a fraction of the source: ResizeYuv converts just those pixels.
/// </summary>
internal sealed class CvResize
{
    const int CoefScale = 1 << 11;
    const int ParallelPixels = 1 << 17; // ~362 x 362

    readonly int _sw, _sh, _dw, _dh;
    readonly int[] _xofs, _yofs;
    readonly short[] _xa0, _xa1, _yb0, _yb1;

    // Output rows are independent, so they're split into chunks resized in parallel, each with its
    // own row buffers. Chunks are fixed so buffers can be allocated once.
    readonly Chunk[] _chunks;

    sealed class Chunk(int start, int end, int dw, int sw)
    {
        public readonly int Start = start, End = end;
        public int[] Row0 = new int[dw * 3], Row1 = new int[dw * 3];
        public int LastRow0 = -1, LastRow1 = -1;

        /// <summary>
        /// Before resizing output row (r0, r1): when r0 is the source row Row1 holds (downscaling by less than 2, and
        /// upscaling), the buffers swap instead of resizing it again, as OpenCV keeps it. Returns which to resize.
        /// </summary>
        public (bool Row0, bool Row1) Need(int r0, int r1)
        {
            if (r0 != LastRow0 && r0 == LastRow1)
                (Row0, Row1, LastRow0, LastRow1) = (Row1, Row0, LastRow1, LastRow0);
            (bool need0, bool need1) = (r0 != LastRow0, r1 != LastRow1);
            (LastRow0, LastRow1) = (r0, r1);
            return (need0, need1);
        }

        public byte[] BgrRow0 = new byte[sw * 3], BgrRow1 = new byte[sw * 3];
    }

    /// <summary>The source columns the horizontal pass reads, ascending.</summary>
    public int[] SourceCols { get; }

    /// <summary>The source rows the vertical pass reads, ascending.</summary>
    public int[] SourceRows { get; }

    int[]? _rowIndex; // source row -> its index in SourceRows

    // For a sampled row (SourceCols only): byte offsets of each output column's two source pixels, and their
    // weights; at the right border the second weight is 0, which gives HResize's s * CoefScale exactly.
    int[]? _sampledX0, _sampledX1;
    short[]? _sampledW0, _sampledW1;
    int[]? _shiftedCols;

    // The same for a whole row (Resize, ResizeYuv).
    readonly int[] _fullX0, _fullX1;
    readonly short[] _fullW0, _fullW1;

    public CvResize(int sw, int sh, int dw, int dh)
    {
        (_sw, _sh, _dw, _dh) = (sw, sh, dw, dh);
        (_xofs, _xa0, _xa1) = Table(dw, sw, clampFraction: true);
        (_yofs, _yb0, _yb1) = Table(dh, sh, clampFraction: false);

        // Small outputs (scenedetect's sizes are at most 256 pixels wide) take well under a millisecond;
        // splitting them across threads costs more CPU than it saves, so only large ones go parallel.
        int chunks = dw * dh < ParallelPixels ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Max(1, dh / 8));
        _chunks = Enumerable.Range(0, chunks).Select(c => new Chunk(c * dh / chunks, (c + 1) * dh / chunks, dw, sw)).ToArray();
        SourceCols = _xofs.SelectMany(x => x >= sw - 1 ? new[] { x } : new[] { x, x + 1 }).Distinct().Order().ToArray();
        SourceRows = Enumerable.Range(0, dh).SelectMany(dy => new[] { Row0(dy), Row1(dy) }).Distinct().Order().ToArray();
        (_fullX0, _fullX1, _fullW0, _fullW1) = (new int[dw], new int[dw], new short[dw], new short[dw]);
        for (int dx = 0; dx < dw; dx++)
        {
            int sx = _xofs[dx];
            bool edge = sx >= sw - 1;
            (_fullX0[dx], _fullX1[dx]) = (sx * 3, edge ? sx * 3 : sx * 3 + 3);
            (_fullW0[dx], _fullW1[dx]) = edge ? ((short)CoefScale, (short)0) : (_xa0[dx], _xa1[dx]);
        }
    }

    /// <summary>
    /// Resizes from only the pixels the resize reads: <see cref="SourceRows"/> × <see cref="SourceCols"/>, packed BGR
    /// (what ffmpeg's remap filter sends). Same arithmetic as <see cref="Resize"/>, so the same result.
    /// </summary>
    public void ResizeSampled(byte[] sampled, byte[] dst)
    {
        if (_rowIndex is null)
            PrepareSampled();
        ForChunks(c =>
        {
            int rowBytes = SourceCols.Length * 3;
            c.LastRow0 = c.LastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);
                var (need0, need1) = c.Need(r0, r1);
                if (need0)
                    HResizeSampled(sampled.AsSpan(_rowIndex![r0] * rowBytes, rowBytes), c.Row0);
                if (need1)
                    HResizeSampled(sampled.AsSpan(_rowIndex![r1] * rowBytes, rowBytes), c.Row1);
                VResize(dy, c, dst);
            }
        });
    }

    /// <summary>
    /// Resizes from <see cref="SourceRows"/>' rows alone, whole (the source's width), one after another: row i of
    /// <paramref name="rows"/> is source row SourceRows[i]. What <see cref="ResizeRows"/> gives the frame they came from.
    /// </summary>
    public void ResizePackedRows(byte[] rows, byte[] dst)
    {
        if (_rowIndex is null)
            PrepareSampled();
        int rowBytes = _sw * 3;
        ForChunks(c =>
        {
            c.LastRow0 = c.LastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);
                var (need0, need1) = c.Need(r0, r1);
                if (need0)
                    HResize(rows.AsSpan(_rowIndex![r0] * rowBytes, rowBytes), c.Row0);
                if (need1)
                    HResize(rows.AsSpan(_rowIndex![r1] * rowBytes, rowBytes), c.Row1);
                VResize(dy, c, dst);
            }
        });
    }

    /// <summary>
    /// Resizes straight from a converted frame's rows (only <see cref="SourceRows"/> need be converted), the source
    /// being its crop at (<paramref name="x"/>, <paramref name="y"/>): what <see cref="Resize"/> gives that crop, with
    /// no copy of it or of the sampled pixels. On the calling thread.
    /// </summary>
    public void ResizeRows(ReadOnlySpan<byte> frame, int stride, int x, int y, byte[] dst)
    {
        foreach (var c in _chunks)
        {
            c.LastRow0 = c.LastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);
                var (need0, need1) = c.Need(r0, r1);
                if (need0)
                    HResize(frame.Slice((y + r0) * stride + x * 3, _sw * 3), c.Row0);
                if (need1)
                    HResize(frame.Slice((y + r1) * stride + x * 3, _sw * 3), c.Row1);
                VResize(dy, c, dst);
            }
        }
    }

    void PrepareSampled()
    {
        var col = new Dictionary<int, int>();
        for (int k = 0; k < SourceCols.Length; k++)
            col[SourceCols[k]] = k;
        (_sampledX0, _sampledX1) = (new int[_dw], new int[_dw]);
        (_sampledW0, _sampledW1) = (new short[_dw], new short[_dw]);
        for (int dx = 0; dx < _dw; dx++)
        {
            int sx = _xofs[dx];
            bool edge = sx >= _sw - 1;
            _sampledX0[dx] = col[sx] * 3;
            _sampledX1[dx] = edge ? col[sx] * 3 : col[sx + 1] * 3;
            (_sampledW0[dx], _sampledW1[dx]) = edge ? ((short)CoefScale, (short)0) : (_xa0[dx], _xa1[dx]);
        }

        var index = new int[_sh];
        for (int i = 0; i < SourceRows.Length; i++)
            index[SourceRows[i]] = i;
        _rowIndex = index;
    }

    /// <summary>HResize from a sampled row: the same products and sums, read from the packed columns.</summary>
    void HResizeSampled(ReadOnlySpan<byte> s, int[] d) => HResize(s, d, _sampledX0!, _sampledX1!, _sampledW0!, _sampledW1!);

    /// <summary>
    /// OpenCV's horizontal pass: per output pixel, the two source pixels at byte offsets x0/x1 times 11-bit weights
    /// (at the right border the second weight is 0: s * CoefScale). The offsets are in range by construction, so no
    /// bounds checks: this runs for every row of every frame.
    /// </summary>
    static unsafe void HResize(ReadOnlySpan<byte> s, int[] d, int[] x0, int[] x1, short[] w0, short[] w1)
    {
        if (x0.Length > 0 && Math.Max(x0[^1], x1[^1]) + 2 >= s.Length)
            throw new ArgumentException("The row is shorter than the resize reads.");
        fixed (byte* sp = s)
        {
            fixed (int* dp = d, a0 = x0, a1 = x1)
            {
                fixed (short* v0 = w0, v1 = w1)
                {
                    int dx = 0;
                    if (Sse2.IsSupported)
                    {
                        // A pixel at a time, its three channels at once: the two pixels' bytes as words, interleaved
                        // (a0 b0 a1 b1 ...), times (wa wb) pairs, summed pairwise (pmaddwd): a * wa + b * wb per channel,
                        // the scalar loop's integers. Each 4-byte load must stay in the row, and each 16-byte store
                        // writes a fourth value into the next pixel's place (it overwrites it): the last pixel, and any
                        // whose load would leave the row, go the scalar way.
                        var zero = Vector128<byte>.Zero;
                        for (; dx < x0.Length - 1 && a1[dx] + 3 < s.Length; dx++)
                        {
                            var pa = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<int>(sp + a0[dx])).AsByte(), zero).AsInt16();
                            var pb = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<int>(sp + a1[dx])).AsByte(), zero).AsInt16();
                            var w = Vector128.Create((ushort)v0[dx] | (v1[dx] << 16)).AsInt16();
                            Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(pa, pb), w).Store(dp + dx * 3);
                        }
                    }

                    int* o = dp + dx * 3;
                    for (; dx < x0.Length; dx++, o += 3)
                    {
                        byte* a = sp + a0[dx], b = sp + a1[dx];
                        int wa = v0[dx], wb = v1[dx];
                        o[0] = a[0] * wa + b[0] * wb;
                        o[1] = a[1] * wa + b[1] * wb;
                        o[2] = a[2] * wa + b[2] * wb;
                    }
                }
            }
        }
    }

    /// <summary>Runs the body on every chunk: inline when there is one, else in parallel.</summary>
    void ForChunks(Action<Chunk> body)
    {
        if (_chunks.Length == 1)
            body(_chunks[0]);
        else
            Parallel.ForEach(_chunks, body);
    }

    /// <summary>One-off resize of a packed BGR image.</summary>
    public static void Linear(byte[] src, int sw, int sh, byte[] dst, int dw, int dh) =>
        new CvResize(sw, sh, dw, dh).Resize(src, dst);

    /// <summary>Resizes a packed BGR source.</summary>
    public void Resize(byte[] src, byte[] dst) => ForChunks(c =>
    {
        int stride = _sw * 3;
        c.LastRow0 = c.LastRow1 = -1;
        for (int dy = c.Start; dy < c.End; dy++)
        {
            int r0 = Row0(dy), r1 = Row1(dy);
            var (need0, need1) = c.Need(r0, r1);
            if (need0)
                HResize(src.AsSpan(r0 * stride, stride), c.Row0);
            if (need1)
                HResize(src.AsSpan(r1 * stride, stride), c.Row1);
            VResize(dy, c, dst);
        }
    });

    /// <summary>
    /// Resizes a packed yuv420p source as if it had first been converted to BGR with
    /// <paramref name="converter"/>, converting only the pixels the resize reads.
    /// </summary>
    public void ResizeYuv420(byte[] yuv, byte[] dst, IYuv420Converter converter) =>
        ResizeYuv(yuv, dst, converter.RowToBgr, _sw, _sh, 0, 0, 0);

    /// <summary>
    /// Resizes the <c>sw × sh</c> region at (<paramref name="cropX"/>, <paramref name="cropY"/>) of a planar YUV frame
    /// (<paramref name="width"/> × <paramref name="height"/>, as decoded) turned clockwise by <paramref name="turn"/>, as
    /// if the frame had first been converted to BGR with <paramref name="convert"/> and then turned; only the pixels the
    /// resize reads are converted. The chroma stays aligned to the decoded frame, as converting before cropping and
    /// turning keeps it.
    /// </summary>
    public void ResizeYuv(byte[] yuv, byte[] dst, YuvRowConverter convert, int width, int height, int turn, int cropX, int cropY) =>
        ResizeYuv(yuv, dst, new YuvConversion(convert), width, height, turn, cropX, cropY);

    /// <summary>
    /// <see cref="ResizeYuv(byte[], byte[], YuvRowConverter, int, int, int, int, int)"/> with the pipeline's conversion:
    /// a frame at a time when it converts that way (the same bytes, the work rows share done once), else row by row.
    /// </summary>
    public void ResizeYuv(byte[] yuv, byte[] dst, YuvConversion convert, int width, int height, int turn, int cropX, int cropY)
    {
        if (turn != 0)
        {
            ResizeYuvTurned(yuv, dst, convert, width, height, turn, cropX, cropY);
            return;
        }

        if (convert.Frame is { } frame)
        {
            ResizeYuvFrame(yuv, dst, frame, width, height, cropX, cropY);
            return;
        }

        ForChunks(c =>
        {
            int[] cols = cropX == 0 ? SourceCols : _shiftedCols ??= SourceCols.Select(x => x + cropX).ToArray();
            int rowBytes = width * 3;
            if (c.BgrRow0.Length < rowBytes)
                (c.BgrRow0, c.BgrRow1) = (new byte[rowBytes], new byte[rowBytes]);
            c.LastRow0 = c.LastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);

                // Rows already converted and resized (upscaling, and downscaling by less than 2) aren't done again.
                var (need0, need1) = c.Need(r0, r1);
                if (need0)
                {
                    convert.Row(yuv, width, height, r0 + cropY, cols, c.BgrRow0);
                    HResize(c.BgrRow0.AsSpan(cropX * 3), c.Row0);
                }

                if (need1)
                {
                    convert.Row(yuv, width, height, r1 + cropY, cols, c.BgrRow1);
                    HResize(c.BgrRow1.AsSpan(cropX * 3), c.Row1);
                }

                VResize(dy, c, dst);
            }
        });
    }

    // ResizeYuvFrame's plan: the decoded frame's rows and columns the resize reads (SourceRows and SourceCols offset by
    // the crop), and the converted pixels there (rows x columns x 3: ResizeSampled's layout).
    (int CropX, int CropY)? _frameFor;
    int[] _frameRows = [], _frameCols = [];
    byte[] _frameBgr = [];

    /// <summary>
    /// <see cref="ResizeYuv(byte[], byte[], YuvConversion, int, int, int, int, int)"/> for a frame converter, not turned:
    /// the pixels the resize reads converted in one pass, then resized from them.
    /// </summary>
    void ResizeYuvFrame(byte[] yuv, byte[] dst, IYuv420P10FrameConverter frame, int width, int height, int cropX, int cropY)
    {
        if (_frameFor != (cropX, cropY))
        {
            _frameRows = SourceRows.Select(r => r + cropY).ToArray();
            _frameCols = SourceCols.Select(x => x + cropX).ToArray();
            _frameBgr = new byte[_frameRows.Length * _frameCols.Length * 3];
            _frameFor = (cropX, cropY);
        }

        ConvertFrame(frame, yuv, width, height, _frameRows, _frameCols, _frameBgr);
        ResizeSampled(_frameBgr, dst);
    }

    /// <summary>
    /// <paramref name="rows"/> × <paramref name="cols"/> converted by a frame converter into <paramref name="bgr"/>
    /// (packed): in one call, or, for outputs resized in chunks, in as many runs of rows, in parallel.
    /// </summary>
    void ConvertFrame(IYuv420P10FrameConverter frame, byte[] yuv, int width, int height, int[] rows, int[] cols, byte[] bgr)
    {
        if (_chunks.Length == 1)
        {
            frame.FrameToBgr(yuv, width, height, rows, cols, bgr);
            return;
        }

        int parts = _chunks.Length, n = cols.Length * 3;
        Parallel.For(0,
            parts,
            p =>
            {
                int first = p * rows.Length / parts, end = (p + 1) * rows.Length / parts;
                frame.FrameToBgr(yuv, width, height, rows.AsSpan(first, end - first), cols, bgr.AsSpan(first * n, (end - first) * n));
            });
    }

    /// <summary>
    /// The decoded frame's pixel under output pixel (x, y) of the frame turned clockwise by <paramref name="turn"/> (90,
    /// 180 or 270), <paramref name="width"/> × <paramref name="height"/> being the decoded frame's size: as ffmpeg's
    /// transpose=clock (output row y is source column y, read bottom up), transpose=cclock (source column w-1-y, top
    /// down) and hflip,vflip (the opposite pixel) turn it, and cv::rotate.
    /// </summary>
    internal static (int X, int Y) TurnedSource(int turn, int x, int y, int width, int height) => turn switch
    {
        90 => (y, height - 1 - x),
        270 => (width - 1 - y, x),
        _ => (width - 1 - x, height - 1 - y),
    };

    // ResizeYuvTurned's plan: the decoded frame's rows and columns under the pixels the resize reads, each one's index
    // among them (-1: not read), and the converted pixels at those rows and columns only (rows x columns x 3).
    (int Turn, int Width, int Height, int CropX, int CropY)? _turnedFor;
    int[] _turnedRows = [], _turnedCols = [], _turnedRowIndex = [], _turnedColIndex = [];
    byte[] _turnedBgr = [], _turnedScratch = [];

    /// <summary>
    /// <see cref="ResizeYuv(byte[], byte[], YuvConversion, int, int, int, int, int)"/> for a turned frame: as OpenCV, which turns the converted image (cv::rotate), and as the
    /// ffmpeg executable's transpose or hflip,vflip after format=bgr24, the pixels are converted where they sit in the
    /// decoded frame and then turned. Turning the planes first would differ wherever the chroma layout doesn't survive
    /// the turn (4:2:2 at 90 and 270, odd sizes).
    /// </summary>
    void ResizeYuvTurned(byte[] yuv, byte[] dst, YuvConversion convert, int width, int height, int turn, int cropX, int cropY)
    {
        if (_turnedFor != (turn, width, height, cropX, cropY))
        {
            // Turned, the resize reads rows cropY + SourceRows and columns cropX + SourceCols; under them in the decoded
            // frame, each a row or a column there.
            var under = SourceRows.SelectMany(r => SourceCols.Select(x => TurnedSource(turn, x + cropX, r + cropY, width, height))).ToArray();
            _turnedRows = under.Select(p => p.Y).Distinct().Order().ToArray();
            _turnedCols = under.Select(p => p.X).Distinct().Order().ToArray();
            _turnedRowIndex = Index(_turnedRows, height);
            _turnedColIndex = Index(_turnedCols, width);
            _turnedBgr = new byte[_turnedRows.Length * _turnedCols.Length * 3];
            _turnedScratch = new byte[width * 3];
            _turnedFor = (turn, width, height, cropX, cropY);
        }

        int[] rows = _turnedRows, cols = _turnedCols, rowIndex = _turnedRowIndex, colIndex = _turnedColIndex;
        byte[] bgr = _turnedBgr;
        int n = cols.Length;

        // Each row converted into a whole-width scratch row (the converter writes at the columns' own offsets), then
        // only the columns read kept, packed.
        void ConvertRow(int i, byte[] scratch)
        {
            convert.Row(yuv, width, height, rows[i], cols, scratch);
            var packed = bgr.AsSpan(i * n * 3, n * 3);
            for (int j = 0; j < n; j++)
            {
                int s = cols[j] * 3;
                (packed[j * 3], packed[j * 3 + 1], packed[j * 3 + 2]) = (scratch[s], scratch[s + 1], scratch[s + 2]);
            }
        }

        if (convert.Frame is { } frame)
        {
            // A frame converter packs them itself.
            ConvertFrame(frame, yuv, width, height, rows, cols, bgr);
        }
        else if (_chunks.Length == 1)
        {
            for (int i = 0; i < rows.Length; i++)
                ConvertRow(i, _turnedScratch);
        }
        else
        {
            Parallel.For(0,
                rows.Length,
                () => new byte[width * 3],
                (i, _, scratch) =>
                {
                    ConvertRow(i, scratch);
                    return scratch;
                },
                _ => { });
        }

        // Then the rows of the turned frame the resize reads, gathered from the converted pixels.
        int turnedWidth = turn == 180 ? width : height;
        ForChunks(c =>
        {
            int rowBytes = turnedWidth * 3;
            if (c.BgrRow0.Length < rowBytes)
                (c.BgrRow0, c.BgrRow1) = (new byte[rowBytes], new byte[rowBytes]);
            c.LastRow0 = c.LastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);
                var (need0, need1) = c.Need(r0, r1);
                if (need0)
                {
                    Gather(r0 + cropY, c.BgrRow0);
                    HResize(c.BgrRow0.AsSpan(cropX * 3), c.Row0);
                }

                if (need1)
                {
                    Gather(r1 + cropY, c.BgrRow1);
                    HResize(c.BgrRow1.AsSpan(cropX * 3), c.Row1);
                }

                VResize(dy, c, dst);
            }
        });

        void Gather(int y, byte[] row)
        {
            foreach (int x in SourceCols)
            {
                var (sx, sy) = TurnedSource(turn, x + cropX, y, width, height);
                int s = (rowIndex[sy] * n + colIndex[sx]) * 3, d = (x + cropX) * 3;
                (row[d], row[d + 1], row[d + 2]) = (bgr[s], bgr[s + 1], bgr[s + 2]);
            }
        }
    }

    /// <summary>Each of <paramref name="values"/>' index among them, by value (0 .. <paramref name="size"/> - 1; -1: not among them).</summary>
    static int[] Index(int[] values, int size)
    {
        var index = new int[size];
        Array.Fill(index, -1);
        for (int i = 0; i < values.Length; i++)
            index[values[i]] = i;
        return index;
    }

    int Row0(int dy) => Math.Clamp(_yofs[dy], 0, _sh - 1);

    int Row1(int dy) => Math.Clamp(_yofs[dy] + 1, 0, _sh - 1);

    void HResize(ReadOnlySpan<byte> s, int[] d) => HResize(s, d, _fullX0, _fullX1, _fullW0, _fullW1);

    /// <summary>OpenCV's vertical pass (VResizeLinearVec_32s8u's arithmetic), 32 values at a time where possible.</summary>
    void VResize(int dy, Chunk c, byte[] dst)
    {
        int b0 = _yb0[dy], b1 = _yb1[dy], n = _dw * 3, x = 0;
        var d = dst.AsSpan(dy * n, n);
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(c.Row0);
        ref int r1 = ref MemoryMarshal.GetArrayDataReference(c.Row1);
        if (Vector256.IsHardwareAccelerated)
        {
            var vb0 = Vector256.Create(b0);
            var vb1 = Vector256.Create(b1);
            ref byte o = ref MemoryMarshal.GetReference(d);
            for (; x <= n - 32; x += 32)
            {
                var p = Pass(ref r0, ref r1, (nuint)x, vb0, vb1);
                var q = Pass(ref r0, ref r1, (nuint)x + 8, vb0, vb1);
                var r = Pass(ref r0, ref r1, (nuint)x + 16, vb0, vb1);
                var t = Pass(ref r0, ref r1, (nuint)x + 24, vb0, vb1);

                // Clamped to 0..255, so narrowing keeps every value.
                Vector256.Narrow(Vector256.Narrow(p, q), Vector256.Narrow(r, t)).AsByte().StoreUnsafe(ref o, (nuint)x);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            x = VResize128(ref r0, ref r1, ref MemoryMarshal.GetReference(d), n, b0, b1);
        }

        for (; x < n; x++)
        {
            int v = (((b0 * (Unsafe.Add(ref r0, x) >> 4)) >> 16) + ((b1 * (Unsafe.Add(ref r1, x) >> 4)) >> 16) + 2) >> 2;
            d[x] = (byte)Math.Clamp(v, 0, 255);
        }
    }

    /// <summary>The vertical pass 16 values at a time with 128-bit vectors (ARM); returns how many it did.</summary>
    internal static int VResize128(ref int r0, ref int r1, ref byte o, int n, int b0, int b1)
    {
        var vb0 = Vector128.Create(b0);
        var vb1 = Vector128.Create(b1);
        int x = 0;
        for (; x <= n - 16; x += 16)
        {
            var p = Pass(ref r0, ref r1, (nuint)x, vb0, vb1);
            var q = Pass(ref r0, ref r1, (nuint)x + 4, vb0, vb1);
            var r = Pass(ref r0, ref r1, (nuint)x + 8, vb0, vb1);
            var t = Pass(ref r0, ref r1, (nuint)x + 12, vb0, vb1);
            Vector128.Narrow(Vector128.Narrow(p, q), Vector128.Narrow(r, t)).AsByte().StoreUnsafe(ref o, (nuint)x);
        }

        return x;
    }

    static Vector128<int> Pass(ref int r0, ref int r1, nuint x, Vector128<int> b0, Vector128<int> b1)
    {
        var v = (((b0 * (Vector128.LoadUnsafe(ref r0, x) >> 4)) >> 16) + ((b1 * (Vector128.LoadUnsafe(ref r1, x) >> 4)) >> 16)
            + Vector128.Create(2)) >> 2;
        return Vector128.Min(Vector128.Max(v, Vector128<int>.Zero), Vector128.Create(255));
    }

    static Vector256<int> Pass(ref int r0, ref int r1, nuint x, Vector256<int> b0, Vector256<int> b1)
    {
        var v = (((b0 * (Vector256.LoadUnsafe(ref r0, x) >> 4)) >> 16) + ((b1 * (Vector256.LoadUnsafe(ref r1, x) >> 4)) >> 16)
            + Vector256.Create(2)) >> 2;
        return Vector256.Min(Vector256.Max(v, Vector256<int>.Zero), Vector256.Create(255));
    }

    /// <summary>Source offset and the two fixed-point weights for each destination index.</summary>
    static (int[] Ofs, short[] W0, short[] W1) Table(int dsize, int ssize, bool clampFraction)
    {
        double scale = 1.0 / ((double)dsize / ssize);
        var ofs = new int[dsize];
        var w0 = new short[dsize];
        var w1 = new short[dsize];
        for (int d = 0; d < dsize; d++)
        {
            float f = (float)((d + 0.5) * scale - 0.5);
            int s = (int)MathF.Floor(f);
            f -= s;

            // Horizontally OpenCV pins the borders; vertically it clamps the row index instead.
            if (clampFraction && s < 0)
            {
                f = 0;
                s = 0;
            }

            if (clampFraction && s >= ssize - 1)
            {
                f = 0;
                s = ssize - 1;
            }

            ofs[d] = s;
            w0[d] = (short)MathF.Round((1f - f) * CoefScale);
            w1[d] = (short)MathF.Round(f * CoefScale);
        }

        return (ofs, w0, w1);
    }
}
