using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What <see cref="FreezeDetector"/> counts as a still picture: FFmpeg's <c>freezedetect</c> options.</summary>
public sealed record FreezeOptions
{
    /// <summary>freezedetect's n (noise): the largest mean absolute frame difference, as a share of the range, of a frame that still counts as the same picture. 0.001 by default.</summary>
    public double Noise { get; init; } = 0.001;

    /// <summary>freezedetect's d: the shortest still picture reported (FFmpeg's duration, whole microseconds). 2 s by default.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The frame rate freezedetect falls back on where the timestamps don't give a duration (a frame without pts, or one
    /// earlier than the picture's first): its input's frame rate. Null: none (such a duration is 0, as freezedetect's).
    /// </summary>
    public Rational? FrameRate { get; init; }
}

/// <summary>
/// A still picture as FFmpeg's <c>freezedetect</c> reports it (<see cref="FreezeDetector"/>): from its first frame (the
/// frame the others are compared with) to the first frame that differs, or still running at the end (no end then:
/// freezedetect prints no freeze_end).
/// </summary>
/// <param name="FirstFrame">The picture's first frame, counted from the detector's first.</param>
/// <param name="EndFrame">The first frame that differs (freeze_end's); null while frozen at the end.</param>
/// <param name="StartPts">The first frame's pts (freeze_start).</param>
/// <param name="EndPts">The first frame that differs' pts (freeze_end); null while frozen at the end.</param>
/// <param name="DurationMicros">freeze_duration in microseconds (FFmpeg's AV_TIME_BASE); null while frozen at the end.</param>
public sealed record FrozenSpan(int FirstFrame, int? EndFrame, long StartPts, long? EndPts, long? DurationMicros);

/// <summary>
/// FFmpeg 8.1's <c>freezedetect</c> on frames of yours, with nothing of FFmpeg: each frame is compared with the picture's
/// first (the last frame that differed from the one before it), by the mean absolute difference over every plane's
/// samples (its <c>scene_sad</c>, an exact integer sum, divided by the samples and by 2^depth); a frame within
/// <see cref="FreezeOptions.Noise"/> is the same picture. Once a picture has lasted <see cref="FreezeOptions.MinDuration"/>
/// (timestamps rescaled to microseconds, rounded as av_rescale_q does), it is a span, ending at the first frame that
/// differs. The planes' sizes are freezedetect's: each plane's line in samples (av_image_get_linesize), the chroma
/// planes' height shifted down (an odd height's last chroma row left out). Not thread-safe: one caller feeds it, in order.
/// </summary>
public sealed class FreezeDetector
{
    readonly (int Width, int Height)[] _planes;
    readonly int _depth, _bytes;
    readonly long _count;
    readonly double _noise;
    readonly long _minMicros;
    readonly Rational _timeBase;
    readonly Rational? _frameRate;
    readonly byte[][] _reference;
    readonly List<FrozenSpan> _spans = [];
    bool _hasReference, _frozen;
    long _n, _referenceN;
    long? _referencePts;
    int _referenceIndex, _index;
    (int Frame, long Pts)? _open;

    /// <summary>A detector of frames whose planes are <paramref name="planes"/> (in samples), at <paramref name="bitDepth"/> bits.</summary>
    /// <param name="planes">Each plane's width (the line's samples) and height, as freezedetect sizes them: 1 to 4 planes.</param>
    /// <param name="bitDepth">The first component's bits, 8 to 16 (over 8: 16-bit little-endian samples).</param>
    /// <param name="timeBase">The pts' time base.</param>
    /// <param name="options">n, d and the frame rate; null: freezedetect's defaults.</param>
    /// <exception cref="ArgumentException">No planes, a size that isn't positive, a depth outside 8 to 16, or noise outside 0 to 1.</exception>
    public FreezeDetector(IReadOnlyList<(int Width, int Height)> planes, int bitDepth, Rational timeBase, FreezeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        var o = options ?? new FreezeOptions();
        if (planes.Count is < 1 or > 4 || planes.Any(p => p.Width <= 0 || p.Height <= 0))
            throw new ArgumentException("One to four planes of a positive size.", nameof(planes));
        if (bitDepth is < 8 or > 16)
            throw new ArgumentException("The bit depth must be 8 to 16.", nameof(bitDepth));
        if (!(o.Noise is >= 0 and <= 1) || o.MinDuration < TimeSpan.Zero)
            throw new ArgumentException("freezedetect's n is 0 to 1 and d not negative.", nameof(options));
        if (timeBase.Num <= 0 || timeBase.Den <= 0)
            throw new ArgumentException("The time base must be positive.", nameof(timeBase));
        _planes = [.. planes];
        (_depth, _bytes) = (bitDepth, bitDepth > 8 ? 2 : 1);
        _count = _planes.Sum(p => (long)p.Width * p.Height);
        (_noise, _minMicros, _timeBase, _frameRate) = (o.Noise, o.MinDuration.Ticks / 10, timeBase, o.FrameRate);
        _reference = [.. _planes.Select(p => new byte[p.Width * _bytes * p.Height])];
    }

    /// <summary>
    /// A detector for frames like <paramref name="frame"/> (its pixel format and size), if freezedetect takes the
    /// format as it is; null for any other (ffmpeg converts those before freezedetect).
    /// </summary>
    public static unsafe FreezeDetector? For(DecodedFrame frame, Rational timeBase, FreezeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Planes(frame.Frame) is { } p ? new FreezeDetector(p.Planes, p.Depth, timeBase, options) : null;
    }

    /// <summary><see cref="For(DecodedFrame, Rational, FreezeOptions?)"/> for a decoder's current frame.</summary>
    public static unsafe FreezeDetector? For(FrameDecoder decoder, FreezeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _ = decoder.GetPlane(0, out _);
        return Planes(decoder.CurrentFrame) is { } p ? new FreezeDetector(p.Planes, p.Depth, decoder.TimeBase, options) : null;
    }

    /// <summary>freezedetect's plane sizes for a frame's format (config_input), or null for a format it doesn't take.</summary>
    static unsafe ((int Width, int Height)[] Planes, int Depth)? Planes(AVFrame* f)
    {
        var format = (AVPixelFormat)f->format;
        if (!Takes(format))
            return null;
        var d = ffmpeg.av_pix_fmt_desc_get(format);
        int depth = d->comp[0].depth;
        var planes = new List<(int, int)>();
        for (int plane = 0; plane < 4; plane++)
        {
            int line = ffmpeg.av_image_get_linesize(format, f->width, plane);
            int width = line > 0 ? line >> (depth > 8 ? 1 : 0) : 0;
            if (width > 0)
                planes.Add((width, f->height >> (plane is 1 or 2 ? d->log2_chroma_h : 0)));
        }

        return ([.. planes], depth);
    }

    /// <summary>Whether freezedetect takes the format as it is (its pix_fmts, FFmpeg 8.1; little-endian), by name.</summary>
    static bool Takes(AVPixelFormat format) => (ffmpeg.av_get_pix_fmt_name(format) ?? "") is "yuv420p" or "yuyv422" or "rgb24" or "bgr24"
        or "yuv422p" or "yuv444p" or "yuv410p" or "yuv411p" or "gray" or "yuvj420p" or "yuvj422p" or "yuvj444p" or "uyvy422"
        or "nv12" or "nv21" or "argb" or "rgba" or "abgr" or "bgra" or "gray16le" or "yuv440p" or "yuvj440p" or "yuva420p"
        or "yuv420p16le" or "yuv422p16le" or "yuv444p16le" or "ya8" or "yuv420p9le" or "yuv420p10le" or "yuv422p10le"
        or "yuv444p9le" or "yuv444p10le" or "yuv422p9le" or "gbrp" or "gbrp9le" or "gbrp10le" or "gbrp16le" or "yuva422p"
        or "yuva444p" or "yuva420p9le" or "yuva422p9le" or "yuva444p9le" or "yuva420p10le" or "yuva422p10le" or "yuva444p10le"
        or "yuva420p16le" or "yuva422p16le" or "yuva444p16le" or "nv16" or "yvyu422" or "gbrap" or "gbrap16le"
        or "yuv420p12le" or "yuv420p14le" or "yuv422p12le" or "yuv422p14le" or "yuv444p12le" or "yuv444p14le" or "gbrp12le"
        or "gbrp14le" or "yuvj411p" or "yuv440p10le" or "yuv440p12le" or "gbrap12le" or "gbrap10le" or "gray12le"
        or "gray10le" or "gray9le" or "gray14le";

    /// <summary>The still pictures found so far that have ended, in order.</summary>
    public IReadOnlyList<FrozenSpan> Spans => _spans;

    /// <summary>
    /// The still pictures found: those that ended, and one still running (no end), as freezedetect reports them by
    /// the end of the frames.
    /// </summary>
    public IReadOnlyList<FrozenSpan> Finish() =>
        _open is { } open ? [.. _spans, new FrozenSpan(open.Frame, null, open.Pts, null, null)] : _spans;

    /// <summary>Adds a decoded frame (its planes and pts).</summary>
    public void Add(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var p = default(FourPlanes);
        for (int i = 0; i < _planes.Length; i++)
            p.Set(i, frame.GetPlane(i, out int stride), stride);
        Add(p, frame.Pts);
    }

    /// <summary>Adds the decoder's current frame (its planes and pts).</summary>
    public void Add(FrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var p = default(FourPlanes);
        for (int i = 0; i < _planes.Length; i++)
            p.Set(i, decoder.GetPlane(i, out int stride), stride);
        Add(p, decoder.Pts);
    }

    /// <summary>
    /// Adds the next frame (freezedetect's activate): its pts (null: none, AV_NOPTS_VALUE) and planes as the detector
    /// sizes them, each with the bytes from row to row (the planes past the detector's left out).
    /// </summary>
    /// <exception cref="ArgumentException">A plane smaller than its size.</exception>
    public void Add(long? pts,
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
        Add(p, pts);
    }

    ref struct FourPlanes
    {
        ReadOnlySpan<byte> _p0, _p1, _p2, _p3;
        int _s0, _s1, _s2, _s3;

        public void Set(int i, ReadOnlySpan<byte> plane, int stride)
        {
            switch (i)
            {
                case 0:
                    _p0 = plane;
                    _s0 = stride;
                    break;
                case 1:
                    _p1 = plane;
                    _s1 = stride;
                    break;
                case 2:
                    _p2 = plane;
                    _s2 = stride;
                    break;
                default:
                    _p3 = plane;
                    _s3 = stride;
                    break;
            }
        }

        public readonly ReadOnlySpan<byte> Plane(int i) => i switch { 0 => _p0, 1 => _p1, 2 => _p2, _ => _p3 };

        public readonly int Stride(int i) => i switch { 0 => _s0, 1 => _s1, 2 => _s2, _ => _s3 };
    }

    void Add(in FourPlanes planes, long? pts)
    {
        for (int p = 0; p < _planes.Length; p++)
        {
            int rowBytes = _planes[p].Width * _bytes;
            if (planes.Stride(p) < rowBytes || planes.Plane(p).Length < ((long)planes.Stride(p) * (_planes[p].Height - 1)) + rowBytes)
                throw new ArgumentException($"Plane {p} is smaller than {_planes[p].Width}x{_planes[p].Height} samples.", nameof(planes));
        }

        int index = _index++;
        _n++;
        bool frozen = false;
        if (_hasReference)
        {
            long micros = _referencePts is not { } rp || pts is not { } fp || fp < rp
                ? _frameRate is { Num: > 0 } fr ? Rescale(_n - _referenceN, fr.Den, fr.Num, 1, 1_000_000) : 0
                : Rescale(fp - rp, _timeBase.Num, _timeBase.Den, 1, 1_000_000);
            ulong sad = 0;
            for (int p = 0; p < _planes.Length; p++)
                sad += Sad(planes.Plane(p), planes.Stride(p), _reference[p], _planes[p].Width * _bytes, _planes[p].Width, _planes[p].Height, _bytes == 2);
            double mafd = (double)sad / _count / (1UL << _depth);
            frozen = mafd <= _noise;
            if (micros >= _minMicros)
            {
                if (!_frozen)
                    _open = (_referenceIndex, _referencePts ?? long.MinValue);
                if (!frozen)
                {
                    _spans.Add(new FrozenSpan(_open!.Value.Frame, index, _open.Value.Pts, pts, micros));
                    _open = null;
                }

                _frozen = frozen;
            }
        }

        if (!frozen)
        {
            for (int p = 0; p < _planes.Length; p++)
            {
                int rowBytes = _planes[p].Width * _bytes;
                for (int r = 0; r < _planes[p].Height; r++)
                    planes.Plane(p).Slice(r * planes.Stride(p), rowBytes).CopyTo(_reference[p].AsSpan(r * rowBytes));
            }

            (_hasReference, _referenceN, _referencePts, _referenceIndex) = (true, _n, pts, index);
        }
    }

    /// <summary>av_rescale_q(a, b, c) with AV_ROUND_NEAR_INF: a × bNum × cDen / (bDen × cNum), halves away from zero.</summary>
    internal static long Rescale(long a, long bNum, long bDen, long cNum, long cDen)
    {
        Int128 num = (Int128)a * bNum * cDen, den = (Int128)bDen * cNum;
        Int128 half = den / 2;
        return (long)(num >= 0 ? (num + half) / den : -((-num + half) / den));
    }

    /// <summary>scene_sad: the sum of absolute differences of two planes' samples.</summary>
    static ulong Sad(ReadOnlySpan<byte> a, int aStride, ReadOnlySpan<byte> b, int bStride, int width, int height, bool wide)
    {
        ulong sad = 0;
        for (int r = 0; r < height; r++)
        {
            if (wide)
            {
                var x = MemoryMarshal.Cast<byte, ushort>(a.Slice(r * aStride, 2 * width));
                var y = MemoryMarshal.Cast<byte, ushort>(b.Slice(r * bStride, 2 * width));
                for (int i = 0; i < width; i++)
                    sad += (ulong)Math.Abs(x[i] - y[i]);
                continue;
            }

            var p = a.Slice(r * aStride, width);
            var q = b.Slice(r * bStride, width);
            int k = 0;
            var lanes = Vector128<ulong>.Zero;
            for (; k + 16 <= width; k += 16)
            {
                var u = Vector128.Create(p.Slice(k, 16));
                var v = Vector128.Create(q.Slice(k, 16));
                var (lo, hi) = Vector128.Widen(Vector128.Max(u, v) - Vector128.Min(u, v));
                var (l1, l2) = Vector128.Widen(lo + hi);
                var (w1, w2) = Vector128.Widen(l1 + l2);
                lanes += w1 + w2;
            }

            sad += Vector128.Sum(lanes);
            for (; k < width; k++)
                sad += (ulong)Math.Abs(p[k] - q[k]);
        }

        return sad;
    }
}
