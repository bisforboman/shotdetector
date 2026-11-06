using System.Runtime.InteropServices;

namespace FrameReader;

/// <summary>What <see cref="ConstantColourDetector"/> counts as one flat colour.</summary>
public sealed record ConstantColourOptions
{
    /// <summary>
    /// How flat a frame is: each of Y, U and V's signalstats HIGH less LOW (90th less 10th percentile) at most this many
    /// 8-bit codes (scaled to the depth), and a run's averages within it of its first frame's. 4 by default.
    /// </summary>
    public double Spread { get; init; } = 4;

    /// <summary>The shortest run reported, from its first frame to the frame after its last. 2 s by default.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// One frame's figures as FFmpeg's <c>signalstats</c> gives them at the frame's depth: each plane's LOW and HIGH (the
/// first values at which its histogram's running count reaches lrint(10%) and lrint(90%) of its samples) and AVG (their
/// mean), for Y, U and V.
/// </summary>
public readonly record struct SignalStats(ushort YLow, ushort YHigh, float YAvg, ushort ULow, ushort UHigh, float UAvg, ushort VLow, ushort VHigh, float VAvg);

/// <summary>
/// A run of frames of one flat colour (<see cref="ConstantColourDetector"/>): from its first frame to the frame after its
/// last (for one running to the end, after the last frame by the step between the last two), and its frames' mean Y, Cb
/// and Cr in 8-bit codes.
/// </summary>
/// <param name="FirstFrame">The run's first frame, counted from the detector's first.</param>
/// <param name="EndFrame">The frame after its last (the count of frames, for one running to the end).</param>
/// <param name="StartPts">The first frame's pts.</param>
/// <param name="EndPts">The pts of the frame after its last (for one running to the end, the last's plus the last step).</param>
/// <param name="Y">The frames' mean YAVG, in 8-bit codes.</param>
/// <param name="Cb">The frames' mean UAVG, in 8-bit codes.</param>
/// <param name="Cr">The frames' mean VAVG, in 8-bit codes.</param>
public sealed record ConstantColourSpan(int FirstFrame, int EndFrame, long StartPts, long EndPts, double Y, double Cb, double Cr);

/// <summary>
/// Runs of one flat colour (a slate or test colour left in, EBU QC 0016B) on frames of yours, with nothing of FFmpeg:
/// each frame's <c>signalstats</c> YLOW, YHIGH, YAVG, ULOW ... VAVG, exactly (vf_signalstats.c's histograms). A frame
/// is flat when every plane's HIGH less LOW is at most <see cref="ConstantColourOptions.Spread"/>; a run is flat frames
/// whose averages stay within the spread of its first frame's, kept when it lasts
/// <see cref="ConstantColourOptions.MinDuration"/>. Every fourth row of the luma first: when those rows alone put more
/// than 10% of the frame's samples at or below one value and more than 10% at or above another over the spread from it,
/// the frame's YLOW and YHIGH are that far apart at least and it isn't flat, without the rest (a moving picture costs a
/// quarter of its luma). Planar YUV, 8 to 16 bits. Not thread-safe: one caller feeds it, in order.
/// </summary>
public sealed class ConstantColourDetector
{
    readonly int _width, _height, _chromaWidth, _chromaHeight, _depth;
    readonly double _spread, _minSeconds;
    readonly Rational _timeBase;
    readonly List<SignalStats?> _frames = [];
    readonly List<ConstantColourSpan> _spans = [];
    readonly int[] _histogram;
    Run? _run;
    long _last, _step;
    int _index;

    /// <summary>
    /// A detector of frames of <paramref name="width"/> by <paramref name="height"/>, <paramref name="chroma"/> and
    /// <paramref name="bitDepth"/> bits, their pts in <paramref name="timeBase"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A size that isn't positive, a depth outside 8 to 16, a spread or duration below 0, or a time base that isn't positive.</exception>
    public ConstantColourDetector(int width, int height, ChromaSubsampling chroma, int bitDepth, Rational timeBase, ConstantColourOptions? options = null)
    {
        var o = options ?? new ConstantColourOptions();
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The picture's size must be positive.", nameof(width));
        if (bitDepth is < 8 or > 16)
            throw new ArgumentException("The bit depth must be 8 to 16.", nameof(bitDepth));
        if (!(o.Spread >= 0) || o.MinDuration < TimeSpan.Zero)
            throw new ArgumentException("The spread and the shortest run must not be negative.", nameof(options));
        if (timeBase.Num <= 0 || timeBase.Den <= 0)
            throw new ArgumentException("The time base must be positive.", nameof(timeBase));
        (_width, _height, _depth, _timeBase) = (width, height, bitDepth, timeBase);
        _chromaWidth = chroma == ChromaSubsampling.Yuv444 ? width : (width + 1) / 2;
        _chromaHeight = chroma == ChromaSubsampling.Yuv420 ? (height + 1) / 2 : height;
        _spread = o.Spread * (1 << (bitDepth - 8));
        _minSeconds = o.MinDuration.TotalSeconds;
        _histogram = new int[4 << bitDepth];
    }

    /// <summary>A detector for frames of <paramref name="format"/> (its size, chroma, depth and time base).</summary>
    public static ConstantColourDetector For(PictureFormat format, ConstantColourOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        return new ConstantColourDetector(format.Width, format.Height, format.Chroma, format.BitDepth, format.TimeBase, options);
    }

    /// <summary>Each frame's figures, in the order added; null for a frame its luma's every fourth row shows isn't flat.</summary>
    public IReadOnlyList<SignalStats?> Frames => _frames;

    /// <summary>Adds a decoded frame (its planes and pts).</summary>
    public void Add(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Add(frame.Pts, frame.GetPlane(0, out int ys), ys, frame.GetPlane(1, out int us), us, frame.GetPlane(2, out int vs), vs);
    }

    /// <summary>Adds the decoder's current frame (its planes and pts).</summary>
    public void Add(FrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        Add(decoder.Pts, decoder.GetPlane(0, out int ys), ys, decoder.GetPlane(1, out int us), us, decoder.GetPlane(2, out int vs), vs);
    }

    /// <summary>
    /// Adds the next frame: its pts and its Y, U and V planes, each with the bytes from row to row (8 bits a sample, else
    /// 16-bit little-endian).
    /// </summary>
    /// <exception cref="ArgumentException">A plane smaller than its size.</exception>
    public void Add(long pts, ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, int uStride, ReadOnlySpan<byte> v, int vStride)
    {
        int bytes = _depth > 8 ? 2 : 1;
        Check(y, yStride, _width * bytes, _height, 0);
        Check(u, uStride, _chromaWidth * bytes, _chromaHeight, 1);
        Check(v, vStride, _chromaWidth * bytes, _chromaHeight, 2);
        int index = _index++;
        if (Wide(y, yStride))
        {
            _frames.Add(null);
            End(index, pts);
            Next(pts);
            return;
        }

        var (yLow, yHigh, yAvg) = Plane(y, yStride, _width, _height);
        var (uLow, uHigh, uAvg) = Plane(u, uStride, _chromaWidth, _chromaHeight);
        var (vLow, vHigh, vAvg) = Plane(v, vStride, _chromaWidth, _chromaHeight);
        var stats = new SignalStats(yLow, yHigh, (float)yAvg, uLow, uHigh, (float)uAvg, vLow, vHigh, (float)vAvg);
        _frames.Add(stats);

        bool flat = yHigh - yLow <= _spread && uHigh - uLow <= _spread && vHigh - vLow <= _spread;
        if (_run is { } run && !(flat && run.Near(stats, _spread)))
            End(index, pts);
        if (flat)
            (_run ??= new Run(index, pts, stats)).Add(stats);
        Next(pts);
    }

    /// <summary>The runs found, a run still going at the end ending after the last frame (by the step between the last two).</summary>
    public IReadOnlyList<ConstantColourSpan> Finish()
    {
        End(_index, _last + _step);
        return _spans;
    }

    static void Check(ReadOnlySpan<byte> plane, int stride, int rowBytes, int height, int index)
    {
        if (stride < rowBytes || plane.Length < ((long)stride * (height - 1)) + rowBytes)
            throw new ArgumentException($"Plane {index} is smaller than its size.", nameof(plane));
    }

    void Next(long pts)
    {
        if (_index > 1 && pts > _last)
            _step = pts - _last;
        _last = pts;
    }

    void End(int frame, long pts)
    {
        if (_run is not { } run)
            return;
        _run = null;
        double seconds = (double)(pts - run.StartPts) * _timeBase.Num / _timeBase.Den;
        if (seconds < _minSeconds - 1e-9)
            return;
        double scale = 1.0 / (1 << (_depth - 8));
        _spans.Add(new ConstantColourSpan(run.FirstFrame, frame, run.StartPts, pts, run.Y / run.Count * scale, run.U / run.Count * scale, run.V / run.Count * scale));
    }

    // Whether every fourth row of the luma shows the frame's YHIGH less YLOW is over the spread: those rows' counts are
    // at most the frame's, so a value they reach the 10% mark at (lrint) is at least YLOW, and one with more than the
    // frame's last 10% at or above it at most YHIGH.
    bool Wide(ReadOnlySpan<byte> plane, int stride)
    {
        int size = 1 << _depth;
        var histogram = _histogram.AsSpan(0, size);
        histogram.Clear();
        for (int r = 0; r < _height; r += 4)
        {
            if (_depth > 8)
            {
                foreach (ushort sample in MemoryMarshal.Cast<byte, ushort>(plane.Slice(r * stride, _width * 2)))
                    histogram[sample & (size - 1)]++;
            }
            else
            {
                foreach (byte sample in plane.Slice(r * stride, _width))
                    histogram[sample]++;
            }
        }

        long count = (long)_width * _height;
        var (low, high) = Marks(count);
        long above = count - high + 1;
        int lowAt = -1, highAt = -1;
        long running = 0;
        for (int i = 0; i < size && lowAt < 0; i++)
        {
            running += histogram[i];
            if (running >= low)
                lowAt = i;
        }

        running = 0;
        for (int i = size - 1; i >= 0 && highAt < 0; i--)
        {
            running += histogram[i];
            if (running >= above)
                highAt = i;
        }

        return lowAt >= 0 && highAt >= 0 && highAt - lowAt > _spread;
    }

    // signalstats' 10% and 90% marks of a plane's samples: lrint(count * 10 / 100.) and lrint(count * 90 / 100.).
    static (long Low, long High) Marks(long count) =>
        ((long)Math.Round(count * 10 / 100.0, MidpointRounding.ToEven), (long)Math.Round(count * 90 / 100.0, MidpointRounding.ToEven));

    // signalstats' LOW, HIGH and AVG of one plane: the first values at which the histogram's running count reaches
    // lrint(10% and 90% of the samples), and their mean.
    (ushort Low, ushort High, double Avg) Plane(ReadOnlySpan<byte> plane, int stride, int width, int height)
    {
        // Four histograms, a sample in each in turn, so a flat plane's runs of one value don't wait on each other's
        // counts; added together after.
        int size = 1 << _depth;
        var counts = _histogram.AsSpan(0, 4 * size);
        counts.Clear();
        int mask = size - 1;
        for (int r = 0; r < height; r++)
        {
            if (_depth > 8)
                Count(MemoryMarshal.Cast<byte, ushort>(plane.Slice(r * stride, width * 2)), counts, size, mask);
            else
                Count(plane.Slice(r * stride, width), counts, size);
        }

        var histogram = counts[..size];
        for (int i = 0; i < size; i++)
            histogram[i] += counts[size + i] + counts[(2 * size) + i] + counts[(3 * size) + i];

        long total = 0;
        long count = (long)width * height;
        var (low, high) = Marks(count);
        int lowAt = -1, highAt = -1;
        long running = 0;
        for (int i = 0; i < size; i++)
        {
            running += histogram[i];
            total += (long)histogram[i] * i;
            if (lowAt < 0 && running >= low)
                lowAt = i;
            if (highAt < 0 && running >= high)
                highAt = i;
        }

        return ((ushort)Math.Max(lowAt, 0), (ushort)Math.Max(highAt, 0), count == 0 ? 0 : (double)total / count);
    }

    static void Count(ReadOnlySpan<byte> row, Span<int> counts, int size)
    {
        int i = 0;
        for (; i <= row.Length - 4; i += 4)
        {
            counts[row[i]]++;
            counts[size + row[i + 1]]++;
            counts[(2 * size) + row[i + 2]]++;
            counts[(3 * size) + row[i + 3]]++;
        }

        for (; i < row.Length; i++)
            counts[row[i]]++;
    }

    static void Count(ReadOnlySpan<ushort> row, Span<int> counts, int size, int mask)
    {
        int i = 0;
        for (; i <= row.Length - 4; i += 4)
        {
            counts[row[i] & mask]++;
            counts[size + (row[i + 1] & mask)]++;
            counts[(2 * size) + (row[i + 2] & mask)]++;
            counts[(3 * size) + (row[i + 3] & mask)]++;
        }

        for (; i < row.Length; i++)
            counts[row[i] & mask]++;
    }

    // A run of flat frames: where it starts, its first frame's averages and the sums of its frames'.
    sealed class Run(int firstFrame, long startPts, SignalStats first)
    {
        public int FirstFrame { get; } = firstFrame;

        public long StartPts { get; } = startPts;

        public double Y { get; private set; }

        public double U { get; private set; }

        public double V { get; private set; }

        public int Count { get; private set; }

        public bool Near(SignalStats stats, double spread) =>
            Math.Abs(stats.YAvg - first.YAvg) <= spread && Math.Abs(stats.UAvg - first.UAvg) <= spread && Math.Abs(stats.VAvg - first.VAvg) <= spread;

        public void Add(SignalStats stats)
        {
            (Y, U, V) = (Y + stats.YAvg, U + stats.UAvg, V + stats.VAvg);
            Count++;
        }
    }
}
