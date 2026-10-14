namespace FrameReader;

/// <summary>
/// How far a whole-file job is (<see cref="Audio.Convert"/>, <see cref="Remux.Copy"/>, <see cref="WaveformData.Read"/>,
/// <see cref="ReferenceTone.Find"/>): reported about 10 times a second through the options' <c>Progress</c>, and once
/// at the end. One type for all, so one logger serves every job.
/// </summary>
/// <param name="Position">How far into the input, as its timestamps say.</param>
/// <param name="Duration">The input's duration (or how much of it is searched); null when unknown.</param>
public readonly record struct MediaProgress(TimeSpan Position, TimeSpan? Duration)
{
    /// <summary>0..1, or null when the duration is unknown.</summary>
    public double? Fraction => Duration is { } d && d > TimeSpan.Zero ? Math.Clamp(Position / d, 0, 1) : null;
}

/// <summary>A job's progress reports: at most every 100 ms, and the last one when it ends.</summary>
internal sealed class ProgressReporter(IProgress<MediaProgress>? progress, TimeSpan? duration)
{
    long _last = -1000; // so the first report goes out at once (TickCount64 counts from boot, never negative)
    TimeSpan _position;

    internal void Report(TimeSpan position)
    {
        // An input's first timestamp can round to a tick below zero (an MP3's start after the encoder delay, issue #274).
        _position = position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (progress is null || Environment.TickCount64 - _last < 100)
            return;
        _last = Environment.TickCount64;
        progress.Report(new MediaProgress(position, duration));
    }

    internal void Done() => progress?.Report(new MediaProgress(duration ?? _position, duration));
}
