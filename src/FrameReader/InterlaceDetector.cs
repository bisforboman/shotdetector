using System.Numerics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What <see cref="InterlaceDetector"/> counts as interlaced, progressive or a repeated field: FFmpeg's <c>idet</c> options.</summary>
public sealed record IdetOptions
{
    /// <summary>idet's intl_thres: how much likelier one field order must be than the other for a frame to be interlaced. 1.04 by default.</summary>
    public float InterlaceThreshold { get; init; } = 1.04f;

    /// <summary>idet's prog_thres: how much likelier interlacing must be than no combing for a frame to be progressive. 1.5 by default.</summary>
    public float ProgressiveThreshold { get; init; } = 1.5f;

    /// <summary>idet's rep_thres: how much likelier one field must be than the other to be a repeat of the frame before's. 3 by default.</summary>
    public float RepeatThreshold { get; init; } = 3f;
}

/// <summary>
/// idet's totals, as it prints them when the frames end: each frame alone (Single frame detection), through its
/// four-frame history (Multi frame detection), and which field repeats the frame before's (Repeated Fields).
/// </summary>
public sealed record IdetTotals(
    long SingleTff,
    long SingleBff,
    long SingleProgressive,
    long SingleUndetermined,
    long MultiTff,
    long MultiBff,
    long MultiProgressive,
    long MultiUndetermined,
    long RepeatedNeither,
    long RepeatedTop,
    long RepeatedBottom);

/// <summary>
/// FFmpeg 8's <c>idet</c> (vf_idet.c) on frames of yours, with nothing of FFmpeg: each frame weighed against the frames
/// before and after it, row by row over every plane (the sum of |a + c - 2b| from row 2 to the plane's height less 2,
/// exact integers), as top field first, bottom field first, progressive or undetermined, through a four-frame history,
/// and for a field repeated from the frame before. Each frame is weighed once the next has come; the last against a copy
/// of itself, as idet does at the end of its input. The planes' sizes are idet's: the frame's width and height, the
/// chroma planes' rounded up (AV_CEIL_RSHIFT), an alpha plane's whole. The sums run on several threads, the totals the
/// same. Not thread-safe: one caller feeds it, in order.
/// </summary>
public sealed class InterlaceDetector
{
    const int Tff = 0, Bff = 1, Progressive = 2, Undetermined = 3;
    const int RepeatNone = 0, RepeatTop = 1, RepeatBottom = 2;

    readonly (int Width, int Height)[] _planes;
    readonly int _depth, _bytes;
    readonly IdetOptions _options;
    readonly long[] _single = new long[4], _multi = new long[4], _repeats = new long[3];
    readonly int[] _history = [Undetermined, Undetermined, Undetermined, Undetermined];
    int _lastType = Undetermined;
    byte[][]? _prev, _cur, _next;
    bool _finished;

    /// <summary>A detector of frames whose planes are <paramref name="planes"/> (in samples), at <paramref name="bitDepth"/> bits.</summary>
    /// <param name="planes">Each plane's width and height in samples, as idet sizes them: 1 to 4 planes.</param>
    /// <param name="bitDepth">The first component's bits, 8 to 16 (over 8: 16-bit little-endian samples).</param>
    /// <param name="options">The thresholds; null: idet's defaults.</param>
    /// <exception cref="ArgumentException">No planes, a size that isn't positive, or a depth outside 8 to 16.</exception>
    public InterlaceDetector(IReadOnlyList<(int Width, int Height)> planes, int bitDepth, IdetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (planes.Count is < 1 or > 4 || planes.Any(p => p.Width <= 0 || p.Height <= 0))
            throw new ArgumentException("One to four planes of a positive size.", nameof(planes));
        if (bitDepth is < 8 or > 16)
            throw new ArgumentException("The bit depth must be 8 to 16.", nameof(bitDepth));
        _planes = [.. planes];
        (_depth, _bytes, _options) = (bitDepth, bitDepth > 8 ? 2 : 1, options ?? new IdetOptions());
    }

    /// <summary>
    /// A detector for frames like <paramref name="frame"/> (its pixel format and size), if idet takes the format as it
    /// is; null for any other (ffmpeg converts those before idet).
    /// </summary>
    public static unsafe InterlaceDetector? For(DecodedFrame frame, IdetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Planes(frame.Frame) is { } p ? new InterlaceDetector(p.Planes, p.Depth, options) : null;
    }

    /// <summary><see cref="For(DecodedFrame, IdetOptions?)"/> for a decoder's current frame.</summary>
    public static unsafe InterlaceDetector? For(FrameDecoder decoder, IdetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _ = decoder.GetPlane(0, out _);
        return Planes(decoder.CurrentFrame) is { } p ? new InterlaceDetector(p.Planes, p.Depth, options) : null;
    }

    /// <summary>idet's plane sizes for a frame's format (its filter), or null for a format it doesn't take.</summary>
    static unsafe ((int Width, int Height)[] Planes, int Depth)? Planes(AVFrame* f)
    {
        var format = (AVPixelFormat)f->format;
        if (!Takes(format))
            return null;
        var d = ffmpeg.av_pix_fmt_desc_get(format);
        var planes = new (int, int)[d->nb_components];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = i is 1 or 2
                ? (-(-f->width >> d->log2_chroma_w), -(-f->height >> d->log2_chroma_h))
                : (f->width, f->height);
        }

        return (planes, d->comp[0].depth);
    }

    /// <summary>Whether idet takes the format as it is (its pix_fmts, FFmpeg 8; little-endian), by name.</summary>
    static bool Takes(AVPixelFormat format) => (ffmpeg.av_get_pix_fmt_name(format) ?? "") is "yuv420p" or "yuv422p"
        or "yuv444p" or "yuv410p" or "yuv411p" or "gray" or "yuvj420p" or "yuvj422p" or "yuvj444p" or "gray16le"
        or "yuv440p" or "yuvj440p" or "yuv420p9le" or "yuv422p9le" or "yuv444p9le" or "yuv420p10le" or "yuv422p10le"
        or "yuv444p10le" or "yuv420p12le" or "yuv422p12le" or "yuv444p12le" or "yuv420p14le" or "yuv422p14le"
        or "yuv444p14le" or "yuv420p16le" or "yuv422p16le" or "yuv444p16le" or "yuva420p" or "yuva422p" or "yuva444p";

    /// <summary>Adds a decoded frame (its planes).</summary>
    public void Add(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var p = default(FourPlanes);
        for (int i = 0; i < _planes.Length; i++)
            p.Set(i, frame.GetPlane(i, out int stride), stride);
        Add(p);
    }

    /// <summary>Adds the decoder's current frame (its planes).</summary>
    public void Add(FrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var p = default(FourPlanes);
        for (int i = 0; i < _planes.Length; i++)
            p.Set(i, decoder.GetPlane(i, out int stride), stride);
        Add(p);
    }

    /// <summary>
    /// Adds the next frame: its planes as the detector sizes them, each with the bytes from row to row (the planes past
    /// the detector's left out).
    /// </summary>
    /// <exception cref="ArgumentException">A plane smaller than its size.</exception>
    /// <exception cref="InvalidOperationException">After <see cref="Finish"/>.</exception>
    public void Add(
        ReadOnlySpan<byte> plane0,
        int stride0,
        ReadOnlySpan<byte> plane1 = default,
        int stride1 = 0,
        ReadOnlySpan<byte> plane2 = default,
        int stride2 = 0,
        ReadOnlySpan<byte> plane3 = default,
        int stride3 = 0)
    {
        var p = default(FourPlanes);
        p.Set(0, plane0, stride0);
        p.Set(1, plane1, stride1);
        p.Set(2, plane2, stride2);
        p.Set(3, plane3, stride3);
        Add(p);
    }

    /// <summary>
    /// The totals as idet prints them at the end of its input: the last frame weighed once more, against a copy of itself
    /// as the next. No frame can be added after.
    /// </summary>
    public IdetTotals Finish()
    {
        if (!_finished && _cur is not null && _next is not null)
        {
            var again = NewFrame();
            for (int p = 0; p < _planes.Length; p++)
                _next[p].CopyTo(again[p], 0);
            Step(again);
        }

        _finished = true;
        return new IdetTotals(_single[Tff],
            _single[Bff],
            _single[Progressive],
            _single[Undetermined],
            _multi[Tff],
            _multi[Bff],
            _multi[Progressive],
            _multi[Undetermined],
            _repeats[RepeatNone],
            _repeats[RepeatTop],
            _repeats[RepeatBottom]);
    }

    void Add(in FourPlanes planes)
    {
        if (_finished)
            throw new InvalidOperationException("The detector has finished.");

        // idet's frames: prev ← cur ← next ← this one; the first is its own cur, and nothing is weighed until there is a prev.
        var frame = _prev ?? NewFrame();
        for (int p = 0; p < _planes.Length; p++)
            Copy(planes.Plane(p), planes.Stride(p), frame[p], p);
        Step(frame);
    }

    void Step(byte[][] frame)
    {
        (_prev, _cur, _next) = (_cur, _next, frame);
        if (_cur is null)
        {
            _cur = NewFrame();
            for (int p = 0; p < _planes.Length; p++)
                frame[p].CopyTo(_cur[p], 0);
        }

        if (_prev is not null)
            Filter(_prev, _cur, _next);
    }

    // idet's filter(): whole-number sums, so the rows can be added up in any order (in bands, in parallel).
    void Filter(byte[][] prev, byte[][] cur, byte[][] next)
    {
        long alpha0 = 0, alpha1 = 0, delta = 0, gamma0 = 0, gamma1 = 0;
        var gate = new object();
        for (int p = 0; p < _planes.Length; p++)
        {
            int h = _planes[p].Height, row = _planes[p].Width * _bytes, plane = p;
            if (h <= 4)
                continue;
            int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, (h - 4) / 32));
            Parallel.For(0, bands, band =>
            {
                long a0 = 0, a1 = 0, d = 0, g0 = 0, g1 = 0;
                int from = 2 + (int)((long)(h - 4) * band / bands), to = 2 + (int)((long)(h - 4) * (band + 1) / bands);
                for (int y = from; y < to; y++)
                {
                    var above = cur[plane].AsSpan((y - 1) * row, row);
                    var here = cur[plane].AsSpan(y * row, row);
                    var below = cur[plane].AsSpan((y + 1) * row, row);
                    var before = prev[plane].AsSpan(y * row, row);
                    long withPrev = Line(above, before, below);
                    long withNext = Line(above, next[plane].AsSpan(y * row, row), below);
                    long repeat = Line(here, before, here);
                    if ((y & 1) == 0)
                        (a0, a1, g1) = (a0 + withPrev, a1 + withNext, g1 + repeat);
                    else
                        (a0, a1, g0) = (a0 + withNext, a1 + withPrev, g0 + repeat);
                    d += Line(above, here, below);
                }

                lock (gate)
                    (alpha0, alpha1, delta, gamma0, gamma1) = (alpha0 + a0, alpha1 + a1, delta + d, gamma0 + g0, gamma1 + g1);
            });
        }

        // As C does it: the int64 sums made floats, against float thresholds.
        int type = (float)alpha0 > _options.InterlaceThreshold * alpha1 ? Tff
            : (float)alpha1 > _options.InterlaceThreshold * alpha0 ? Bff
            : (float)alpha1 > _options.ProgressiveThreshold * delta ? Progressive
            : Undetermined;
        int repeated = (float)gamma0 > _options.RepeatThreshold * gamma1 ? RepeatTop
            : (float)gamma1 > _options.RepeatThreshold * gamma0 ? RepeatBottom
            : RepeatNone;

        for (int i = _history.Length - 1; i > 0; i--)
            _history[i] = _history[i - 1];
        _history[0] = type;
        int best = Undetermined, match = 0;
        foreach (int seen in _history)
        {
            if (seen == Undetermined)
                continue;
            if (best == Undetermined)
                best = seen;
            if (seen == best)
            {
                match++;
            }
            else
            {
                match = 0;
                break;
            }
        }

        if (_lastType == Undetermined ? match > 0 : match > 2)
            _lastType = best;
        _single[type]++;
        _multi[_lastType]++;
        _repeats[repeated]++;
    }

    // idet's filter_line: the sum over the row of |a + c - 2b|.
    long Line(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c) =>
        _bytes == 1 ? Line8(a, b, c)
        : _depth <= 14 ? LineShort(MemoryMarshal.Cast<byte, short>(a), MemoryMarshal.Cast<byte, short>(b), MemoryMarshal.Cast<byte, short>(c))
        : Line16(MemoryMarshal.Cast<byte, ushort>(a), MemoryMarshal.Cast<byte, ushort>(b), MemoryMarshal.Cast<byte, ushort>(c));

    // Up to 14 bits a sample, |a + c - 2b| fits a short: a lane of shorts, widened once to add up.
    static long LineShort(ReadOnlySpan<short> a, ReadOnlySpan<short> b, ReadOnlySpan<short> c)
    {
        var sum = Vector<int>.Zero;
        int x = 0;
        for (; x <= a.Length - Vector<short>.Count; x += Vector<short>.Count)
        {
            var v = Vector.Abs(new Vector<short>(a[x..]) + new Vector<short>(c[x..]) - (new Vector<short>(b[x..]) * 2));
            Vector.Widen(v, out var low, out var high);
            sum += low + high;
        }

        long total = Vector.Sum(sum);
        for (; x < a.Length; x++)
            total += Math.Abs(a[x] + c[x] - (2 * b[x]));
        return total;
    }

    static long Line8(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var sum = Vector<int>.Zero;
        int x = 0;
        for (; x <= a.Length - Vector<byte>.Count; x += Vector<byte>.Count)
        {
            Vector.Widen(new Vector<byte>(a[x..]), out var a0, out var a1);
            Vector.Widen(new Vector<byte>(b[x..]), out var b0, out var b1);
            Vector.Widen(new Vector<byte>(c[x..]), out var c0, out var c1);
            sum += Abs(a0, b0, c0) + Abs(a1, b1, c1);
        }

        long total = Vector.Sum(sum);
        for (; x < a.Length; x++)
            total += Math.Abs(a[x] + c[x] - (2 * b[x]));
        return total;

        static Vector<int> Abs(Vector<ushort> a, Vector<ushort> b, Vector<ushort> c)
        {
            var v = Vector.Abs(Vector.AsVectorInt16(a) + Vector.AsVectorInt16(c) - (Vector.AsVectorInt16(b) * 2));
            Vector.Widen(v, out var low, out var high);
            return low + high;
        }
    }

    static long Line16(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, ReadOnlySpan<ushort> c)
    {
        var sum = Vector<long>.Zero;
        int x = 0;
        for (; x <= a.Length - Vector<ushort>.Count; x += Vector<ushort>.Count)
        {
            Vector.Widen(new Vector<ushort>(a[x..]), out var a0, out var a1);
            Vector.Widen(new Vector<ushort>(b[x..]), out var b0, out var b1);
            Vector.Widen(new Vector<ushort>(c[x..]), out var c0, out var c1);
            var v = Vector.Abs(Vector.AsVectorInt32(a0) + Vector.AsVectorInt32(c0) - (Vector.AsVectorInt32(b0) * 2))
                + Vector.Abs(Vector.AsVectorInt32(a1) + Vector.AsVectorInt32(c1) - (Vector.AsVectorInt32(b1) * 2));
            Vector.Widen(v, out var low, out var high);
            sum += Vector.AsVectorInt64(low) + Vector.AsVectorInt64(high);
        }

        long total = Vector.Sum(sum);
        for (; x < a.Length; x++)
            total += Math.Abs(a[x] + c[x] - (2 * b[x]));
        return total;
    }

    byte[][] NewFrame() => [.. _planes.Select(p => new byte[p.Width * _bytes * p.Height])];

    void Copy(ReadOnlySpan<byte> plane, int stride, byte[] into, int p)
    {
        int row = _planes[p].Width * _bytes, height = _planes[p].Height;
        if (stride < row || plane.Length < ((long)stride * (height - 1)) + row)
            throw new ArgumentException($"Plane {p} is smaller than {_planes[p].Width}x{height} samples.");
        if (stride == row)
        {
            plane[..(row * height)].CopyTo(into);
            return;
        }

        for (int r = 0; r < height; r++)
            plane.Slice(r * stride, row).CopyTo(into.AsSpan(r * row, row));
    }
}
