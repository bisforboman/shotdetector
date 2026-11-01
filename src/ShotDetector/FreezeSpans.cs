using System.Globalization;

namespace ShotDetector;

/// <summary>
/// ffmpeg's <c>freezedetect</c> on the detection pass (<see cref="DetectionOptions.FreezeDetect"/>): its options, with
/// its defaults. Each frame is compared with the still picture's first (the last frame that differed from the one before
/// it) by the mean absolute difference over all its samples, as a share of their range; within <see cref="Noise"/> it is
/// the same picture, and a picture lasting <see cref="MinDuration"/> or more is reported.
/// </summary>
public sealed record FreezeDetectOptions
{
    /// <summary>freezedetect's n (noise): the largest mean absolute difference, as a share of the range, of the same picture. 0.001 by default.</summary>
    public double Noise { get; init; } = 0.001;

    /// <summary>freezedetect's d: the shortest still picture reported (whole microseconds, as FFmpeg's duration option). 2 s by default.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The options as freezedetect's (its option names, invariant culture), for the executable's graph.</summary>
    internal string Filter() => string.Create(CultureInfo.InvariantCulture, $"freezedetect=n={Noise:R}:d={MinDuration.Ticks / 10}us");

    /// <summary>The options checked (an <see cref="ArgumentException"/>): freezedetect's ranges.</summary>
    internal void Check()
    {
        if (!(MinDuration >= TimeSpan.Zero))
            throw new ArgumentException("FreezeDetect.MinDuration can't be negative.");
        if (!(Noise is >= 0 and <= 1))
            throw new ArgumentException("FreezeDetect.Noise is 0 to 1, as freezedetect's n.");
    }
}

/// <summary>
/// A still picture as ffmpeg's <c>freezedetect</c> reports it (<see cref="DetectionResult.FreezeSpans"/>): from its first
/// frame (the one the others were compared with) to the first frame that differs. One still running at the end of what
/// was read has no end (freezedetect prints no freeze_end then): <see cref="EndFrame"/>, <see cref="End"/> and
/// <see cref="Duration"/> null.
/// </summary>
/// <param name="StartFrame">The still picture's first frame (display order, the numbering of <see cref="Shot"/>'s frames).</param>
/// <param name="EndFrame">The first frame that differs; null while still at the end.</param>
/// <param name="Start">freeze_start in seconds: the first frame's pts times the time base, from the video stream's start.</param>
/// <param name="End">freeze_end in seconds, from the video stream's start; null while still at the end.</param>
public readonly record struct FreezeSpan(int StartFrame, int? EndFrame, double Start, double? End)
{
    /// <summary>freeze_duration in seconds (whole microseconds: the pts between the ends rescaled to them, as FFmpeg rounds); null while still at the end.</summary>
    public double? Duration { get; init; } = End - Start;

    /// <summary>
    /// As freezedetect logs the span, its lines' values on one line: <c>freeze_start:1.2 freeze_duration:3.04
    /// freeze_end:4.24</c>, or the start alone (FFmpeg's time strings).
    /// </summary>
    public override string ToString() => End is { } end
        ? $"freeze_start:{BlackSpan.Time(Start)} freeze_duration:{BlackSpan.Time(Duration ?? end - Start)} freeze_end:{BlackSpan.Time(end)}"
        : $"freeze_start:{BlackSpan.Time(Start)}";
}
