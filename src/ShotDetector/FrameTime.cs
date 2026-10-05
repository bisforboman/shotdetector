using System.Numerics;

namespace ShotDetector;

/// <summary>
/// A position held the way PySceneDetect's FrameTimecode holds it, so that printed timecodes and
/// seconds match digit for digit. In 0.7.1 it is either a presentation timestamp (<see cref="Value"/>
/// in units of 1/<see cref="TbDen"/> s; decoded frame positions) or a bare frame number (TbDen == 0;
/// cuts that ThresholdDetector computes). In 0.6.4 it is always a frame number whose seconds are
/// frame / fps in floating point (TbDen == -1). Arithmetic, conversions and rounding mirror
/// FrameTimecode, including Python's round(), which rounds the exact binary value half to even.
/// </summary>
public readonly record struct FrameTime(long Value, long TbDen, Fps Fps)
{
    /// <summary>A bare frame number.</summary>
    public static FrameTime Frame(long frame, Fps fps) => new(frame, 0, fps);
    /// <summary>A frame number as PySceneDetect 0.6.4 holds every position: seconds = frame / fps.</summary>
    public static FrameTime Frame064(long frame, Fps fps) => new(frame, -1, fps);
    /// <summary>A presentation timestamp in units of 1/<paramref name="tbDen"/> seconds.</summary>
    public static FrameTime Pts(long pts, long tbDen, Fps fps) => new(pts, tbDen, fps);

    bool IsFrame => TbDen <= 0;
    double FpsValue => Fps.Value; // float(frame_rate)

    /// <summary>FrameTimecode.seconds: an exact rational, rounded once to double (0.6.4: frame / float fps).</summary>
    public double Seconds => TbDen switch
    {
        -1 => Value / FpsValue,
        0 => (double)(Value * Fps.Den) / Fps.Num,
        _ => (double)Value / TbDen,
    };

    /// <summary>FrameTimecode.frame_num.</summary>
    public long FrameNum => IsFrame ? Value : (long)Math.Round(Seconds * FpsValue);

    /// <summary>FrameTimecode.get_timecode(): HH:MM:SS.mmm.</summary>
    public string Timecode()
    {
        // nearest_frame: frame numbers go through the float frame rate; timestamps use their seconds.
        double secs = IsFrame ? Value / FpsValue : Seconds;
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
    public FrameTime PlusFrames(int frames) => IsFrame
        ? this with { Value = Value + frames }
        : this with { Value = Math.Max(0, Value + (long)Math.Round(frames / FpsValue / (1.0 / TbDen))) };

    /// <summary>FrameTimecode - FrameTimecode (clamped at 0), e.g. a scene's duration.</summary>
    public FrameTime Minus(FrameTime other)
    {
        if (IsFrame && other.IsFrame)
            return this with { Value = Math.Max(0, Value - other.Value) };
        if (!IsFrame && !other.IsFrame)
        {
            if (TbDen == other.TbDen)
                return this with { Value = Math.Max(0, Value - other.Value) };
            long finer = Math.Max(TbDen, other.TbDen); // smaller time base
            long a = RoundHalfEven(Value * (BigInteger)finer, TbDen);
            long b = RoundHalfEven(other.Value * (BigInteger)finer, other.TbDen);
            return this with { Value = Math.Max(0, a - b), TbDen = finer };
        }
        if (!IsFrame) // timestamp - frame number: in this time base
            return this with { Value = Math.Max(0, Value - (long)Math.Round(other.Seconds / (1.0 / TbDen))) };
        // frame number - timestamp: in the other's time base
        long self = (long)Math.Round(Seconds / (1.0 / other.TbDen));
        return this with { Value = Math.Max(0, self - other.Value), TbDen = other.TbDen };
    }

    /// <summary>
    /// round(x * scale) on the exact binary value of x, ties to even. This is what Python's
    /// round(x, 3) and format(x, ".3f") do; Math.Round(x, 3) scales in floating point first and so
    /// rounds e.g. 3.5035 (really 3.50349999...) up.
    /// </summary>
    public static long RoundScaled(double x, long scale)
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
