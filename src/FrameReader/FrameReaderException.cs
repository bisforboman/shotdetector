namespace FrameReader;

/// <summary>Why reading frames failed.</summary>
public enum FrameReaderError
{
    /// <summary>FFmpeg 8's shared libraries couldn't be loaded.</summary>
    LibrariesNotFound,

    /// <summary>FFmpeg can't open or read the input.</summary>
    InvalidInput,
}

/// <summary>A failure reading a video, with its <see cref="Reason"/>.</summary>
/// <param name="reason">Why it failed.</param>
/// <param name="message">What happened.</param>
/// <param name="innerException">The underlying exception, if any.</param>
public sealed class FrameReaderException(FrameReaderError reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Why it failed.</summary>
    public FrameReaderError Reason { get; } = reason;
}
