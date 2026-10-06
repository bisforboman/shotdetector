namespace ShotDetector;

/// <summary>What went wrong, for callers that handle failures differently (<see cref="ShotDetectionException.Reason"/>).</summary>
public enum ShotDetectionError
{
    /// <summary>ffmpeg or ffprobe couldn't be started, or FFmpeg's libraries couldn't be loaded: a deployment problem.</summary>
    FfmpegNotFound,

    /// <summary>The input couldn't be read as a video: missing, unreadable, not a video, or headers ffmpeg can't parse.</summary>
    InvalidInput,

    /// <summary>Decoding stopped part way (ffmpeg failed or gave frames it couldn't place).</summary>
    DecodeFailed,

    /// <summary>ffmpeg failed writing images or clips (<see cref="Export"/>).</summary>
    ExportFailed,
}

/// <summary>
/// A failure from ffmpeg, ffprobe or the input, with a <see cref="Reason"/> to tell a deployment problem from bad input.
/// An <see cref="InvalidOperationException"/>, so code catching that still catches it.
/// </summary>
public sealed class ShotDetectionException(ShotDetectionError reason, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>What went wrong.</summary>
    public ShotDetectionError Reason { get; } = reason;
}
