using System.Globalization;

namespace ShotDetector;

/// <summary>
/// ffmpeg's <c>blackdetect</c> on the detection pass (<see cref="DetectionOptions.BlackDetect"/>): its options, with its
/// defaults. A frame is black when at least <see cref="PictureThreshold"/> of its pixels have a luma at or under
/// <see cref="PixelThreshold"/> (of the range above black: 16 + that × 219 in limited range, that × 255 in full, scaled
/// to the depth); runs of black frames lasting <see cref="MinDuration"/> or more are reported.
/// </summary>
public sealed record BlackDetectOptions
{
    /// <summary>blackdetect's d (black_min_duration): the shortest span reported. 2 s by default.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>blackdetect's pic_th (picture_black_ratio_th): the share of black pixels that makes a frame black, 0 to 1. 0.98 by default.</summary>
    public double PictureThreshold { get; init; } = 0.98;

    /// <summary>blackdetect's pix_th (pixel_black_th): the luma at or under which a pixel is black, 0 to 1. 0.10 by default.</summary>
    public double PixelThreshold { get; init; } = 0.10;

    /// <summary>The options as blackdetect's (its option names, invariant culture), for the executable's graph and messages.</summary>
    internal string Filter(double minSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"blackdetect=d={minSeconds:R}:pic_th={PictureThreshold:R}:pix_th={PixelThreshold:R}");

    /// <summary>The options checked (an <see cref="ArgumentException"/>): blackdetect's ranges.</summary>
    internal void Check()
    {
        if (!(MinDuration >= TimeSpan.Zero))
            throw new ArgumentException("BlackDetect.MinDuration can't be negative.");
        if (!(PictureThreshold is >= 0 and <= 1) || !(PixelThreshold is >= 0 and <= 1))
            throw new ArgumentException("BlackDetect.PictureThreshold and PixelThreshold are 0 to 1, as blackdetect's pic_th and pix_th.");
    }
}

/// <summary>
/// A run of black frames as ffmpeg's <c>blackdetect</c> reports it (<see cref="DetectionResult.BlackSpans"/>): from the
/// first black frame to the first frame after it that isn't, or, for a run still black at the end of what was read, to
/// the last frame itself (blackdetect's black_end there).
/// </summary>
/// <param name="StartFrame">The first black frame's index (display order, the numbering of <see cref="Shot"/>'s frames).</param>
/// <param name="EndFrame">The frame black_end is at: the first frame after the run that isn't black, or the run's last frame at the end.</param>
/// <param name="Start">blackdetect's black_start in seconds: the first black frame's pts times the time base, from the video stream's start.</param>
/// <param name="End">blackdetect's black_end in seconds: <paramref name="EndFrame"/>'s pts times the time base, from the video stream's start.</param>
public readonly record struct BlackSpan(int StartFrame, int EndFrame, double Start, double End)
{
    /// <summary>blackdetect's black_duration in seconds.</summary>
    public double Duration => End - Start;

    /// <summary>As blackdetect logs the span: <c>black_start:2.92 black_end:5.64 black_duration:2.72</c> (%g, six significant digits).</summary>
    public override string ToString() => $"black_start:{G(Start)} black_end:{G(End)} black_duration:{G(Duration)}";

    /// <summary>C's %g, as FFmpeg's av_ts2timestr prints a time: six significant digits, no trailing zeros.</summary>
    internal static string G(double v)
    {
        if (v == 0)
            return "0";
        string s = v.ToString("E5", CultureInfo.InvariantCulture); // the six digits %g rounds to, and their exponent
        int exponent = int.Parse(s[(s.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        if (exponent is < -4 or >= 6)
        {
            string mantissa = s[..s.IndexOf('E')].TrimEnd('0').TrimEnd('.');
            return string.Create(CultureInfo.InvariantCulture, $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}");
        }

        string fixedPoint = v.ToString("F" + Math.Max(0, 5 - exponent), CultureInfo.InvariantCulture);
        return fixedPoint.Contains('.', StringComparison.Ordinal) ? fixedPoint.TrimEnd('0').TrimEnd('.') : fixedPoint;
    }
}

/// <summary>One frame's black judgement on the detection pass: its pts (from the video stream's start, in <see cref="BlackFrames.TimeBase"/>) and whether it is black.</summary>
internal readonly record struct BlackFrame(long Pts, bool Black);

/// <summary>
/// A run's frames' black judgements, in display order from <see cref="FirstFrame"/>, and their spans as blackdetect
/// reports them. The judgement of a frame depends on that frame alone, so the frames of chunks read apart join by
/// concatenation into the whole run's, and the spans (whose ends depend on the runs) are worked out once, after.
/// </summary>
internal sealed class BlackFrames(double timeBase, int firstFrame)
{
    /// <summary>The pts' time base as blackdetect's av_q2d gives it: seconds per unit.</summary>
    public double TimeBase { get; } = timeBase;

    public int FirstFrame { get; } = firstFrame;

    public List<BlackFrame> Frames { get; } = [];

    /// <summary>
    /// blackdetect's spans of the frames (its filter_frame and uninit): a run starts at a black frame and ends at the next
    /// frame that isn't (black_end), or at the last frame's pts when still black at the end; kept when black_end -
    /// black_start, in the time base, reaches d / av_q2d(time base) truncated to an integer (check_black_end).
    /// </summary>
    public IReadOnlyList<BlackSpan> Spans(BlackDetectOptions o, int count)
    {
        double tb = TimeBase;
        long least = (long)(o.MinDuration.TotalSeconds / tb);
        var spans = new List<BlackSpan>();
        int start = -1, n = Math.Min(count, Frames.Count);
        void Close(int end)
        {
            if (Frames[end].Pts - Frames[start].Pts >= least)
                spans.Add(new BlackSpan(FirstFrame + start, FirstFrame + end, Frames[start].Pts * tb, Frames[end].Pts * tb));
            start = -1;
        }

        for (int i = 0; i < n; i++)
        {
            if (Frames[i].Black && start < 0)
                start = i;
            else if (!Frames[i].Black && start >= 0)
                Close(i);
        }

        if (start >= 0)
            Close(n - 1);
        return spans;
    }
}
