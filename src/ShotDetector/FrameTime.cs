using System.Numerics;

namespace ShotDetector;

/// <summary>
/// A position held the way PySceneDetect's FrameTimecode holds it, so that printed timecodes and
/// seconds match digit for digit. In 0.7.1 it is either a presentation timestamp (<see cref="Value"/>
/// in units of 1/<see cref="TbDen"/> s; decoded frame positions) or a bare frame number (TbDen == 0;
/// cuts that ThresholdDetector computes), or a time in seconds (TbDen == -2, the double's bits in Value;
/// times typed by the user or read from a scene list). In 0.6.4 it is always a frame number whose seconds are
/// frame / fps in floating point (TbDen == -1). Arithmetic, conversions and rounding mirror
/// FrameTimecode, including Python's round(), which rounds the exact binary value half to even.
/// </summary>
public readonly record struct FrameTime
{
    internal FrameTime(long value, long tbDen, Fps fps) => (Value, TbDen, Fps) = (value, tbDen, fps);

    /// <summary>A frame number, or a pts in units of 1/<see cref="TbDen"/> seconds.</summary>
    internal long Value { get; init; }

    /// <summary>0 for a frame number, else the time base denominator of <see cref="Value"/>.</summary>
    internal long TbDen { get; init; }

    /// <summary>The frame rate frame numbers are counted at.</summary>
    public Fps Fps { get; }

    /// <summary>The timecode, "HH:MM:SS.mmm".</summary>
    public override string ToString() => Timecode();

    /// <summary>A bare frame number.</summary>
    public static FrameTime Frame(long frame, Fps fps) => new(frame, 0, fps);
    /// <summary>A frame number as PySceneDetect 0.6.4 holds every position: seconds = frame / fps.</summary>
    internal static FrameTime Frame064(long frame, Fps fps) => new(frame, -1, fps);
    /// <summary>A presentation timestamp in units of 1/<paramref name="tbDen"/> seconds.</summary>
    internal static FrameTime Pts(long pts, long tbDen, Fps fps) => new(pts, tbDen, fps);
    /// <summary>A time in seconds (FrameTimecode's _Seconds), e.g. "5s" or "00:00:05.000" given by the user.</summary>
    internal static FrameTime FromSeconds(double seconds, Fps fps) => new(BitConverter.DoubleToInt64Bits(seconds), -2, fps);

    bool IsFrame => TbDen is 0 or -1;
    bool IsSecs => TbDen == -2;
    double FpsValue => Fps.Value; // float(frame_rate)

    /// <summary>FrameTimecode.seconds: an exact rational, rounded once to double (0.6.4: frame / float fps).</summary>
    public double Seconds => TbDen switch
    {
        -2 => BitConverter.Int64BitsToDouble(Value),
        -1 => Value / FpsValue,
        0 => (double)(Value * Fps.Den) / Fps.Num,
        _ => (double)Value / TbDen,
    };

    /// <summary>FrameTimecode.frame_num.</summary>
    public long FrameNum => IsFrame ? Value : (long)Math.Round(Seconds * FpsValue);

    /// <summary>FrameTimecode.get_timecode(): HH:MM:SS.mmm.</summary>
    public string Timecode() =>
        // nearest_frame: frame numbers and seconds go through the frame number and the float frame rate;
        // timestamps use their own seconds.
        FormatTimecode(TbDen <= 0 ? FrameNum / FpsValue : Seconds);

    /// <summary>get_timecode() of a time in seconds: HH:MM:SS.mmm, milliseconds rounded like Python's round().</summary>
    internal static string FormatTimecode(double secs)
    {
        long hrs = (long)(secs / 3600);
        secs -= hrs * 3600;
        long mins = (long)(secs / 60);
        secs = Math.Max(0.0, secs - mins * 60);
        long ms = RoundScaled(secs, 1000);
        if (ms >= 60_000)
        {
            ms = 0;
            if (++mins >= 60) { mins = 0; hrs++; }
        }
        return $"{hrs:00}:{mins:00}:{ms / 1000:00}.{ms % 1000:000}";
    }

    /// <summary>f"{seconds:.3f}".</summary>
    public string SecondsText()
    {
        long ms = RoundScaled(Seconds, 1000);
        return $"{ms / 1000}.{ms % 1000:000}";
    }

    /// <summary>FrameTimecode + n frames (only used for the end of the last scene).</summary>
    public FrameTime PlusFrames(int frames) => IsFrame ? this with { Value = Value + frames }
        : IsSecs ? FromSeconds(Math.Max(0.0, Seconds + frames / FpsValue), Fps)
        : this with { Value = Math.Max(0, Value + (long)Math.Round(frames / FpsValue / (1.0 / TbDen))) };

    /// <summary>FrameTimecode + FrameTimecode for frame numbers and seconds (a start plus a duration).</summary>
    internal FrameTime Plus(FrameTime other) =>
        IsSecs ? FromSeconds(Math.Max(0.0, Seconds + other.Seconds), Fps)
        : IsFrame && !(other.TbDen > 0) ? this with { Value = Math.Max(0, Value + other.FrameNum) }
        : throw new NotSupportedException("Adding timestamps isn't needed here.");

    /// <summary>FrameTimecode - FrameTimecode (clamped at 0), e.g. a scene's duration.</summary>
    public FrameTime Minus(FrameTime other)
    {
        // Seconds minus anything but a timestamp stays in seconds; a frame number minus seconds counts frames.
        if (IsSecs && !(other.TbDen > 0))
            return FromSeconds(Math.Max(0.0, Seconds - other.Seconds), Fps);
        if (IsFrame && other.IsSecs)
            return this with { Value = Math.Max(0, Value - other.FrameNum) };
        if (IsFrame && other.IsFrame)
            return this with { Value = Math.Max(0, Value - other.Value) };
        if (TbDen > 0 && other.TbDen > 0)
        {
            if (TbDen == other.TbDen)
                return this with { Value = Math.Max(0, Value - other.Value) };
            long finer = Math.Max(TbDen, other.TbDen); // smaller time base
            long a = RoundHalfEven(Value * (BigInteger)finer, TbDen);
            long b = RoundHalfEven(other.Value * (BigInteger)finer, other.TbDen);
            return this with { Value = Math.Max(0, a - b), TbDen = finer };
        }
        if (TbDen > 0) // timestamp - frame number or seconds: in this time base
            return this with { Value = Math.Max(0, Value - (long)Math.Round(other.Seconds / (1.0 / TbDen))) };
        // frame number or seconds - timestamp: in the other's time base
        long self = (long)Math.Round(Seconds / (1.0 / other.TbDen));
        return this with { Value = Math.Max(0, self - other.Value), TbDen = other.TbDen };
    }

    /// <summary>
    /// round(x * scale) on the exact binary value of x, ties to even. This is what Python's
    /// round(x, 3) and format(x, ".3f") do; Math.Round(x, 3) scales in floating point first and so
    /// rounds e.g. 3.5035 (really 3.50349999...) up.
    /// </summary>
    internal static long RoundScaled(double x, long scale)
    {
        if (x < 0)
            return -RoundScaled(-x, scale);
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        int shift = exponent - 1075; // x = mantissa * 2^shift
        BigInteger num = mantissa * (BigInteger)scale;
        return shift >= 0 ? (long)(num << shift) : RoundHalfEven(num, BigInteger.One << -shift);
    }

    static long RoundHalfEven(BigInteger num, BigInteger den)
    {
        var q = BigInteger.DivRem(num, den, out var r);
        int cmp = (2 * r).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        return (long)q;
    }
}
