namespace FrameReader;

/// <summary>Why reading frames failed.</summary>
public enum FrameReaderError
{
    /// <summary>FFmpeg 8's shared libraries couldn't be loaded.</summary>
    LibrariesNotFound,

    /// <summary>FFmpeg can't open or read the input.</summary>
    InvalidInput,

    /// <summary>Decoding, converting or filtering failed.</summary>
    DecodeFailed,

    /// <summary>Encoding, muxing or writing the file failed (the writers, <see cref="Remux"/>).</summary>
    WriteFailed,
}

/// <summary>
/// A failure reading, processing or writing media, with its <see cref="Reason"/>. The rule: what the caller passed
/// (sizes, rates, unknown option names) is an <see cref="ArgumentException"/>; what the media or the libraries do
/// (an input FFmpeg can't open, a codec or filter these libraries leave out, a failing encoder) is this.
/// </summary>
/// <param name="reason">Why it failed.</param>
/// <param name="message">What happened.</param>
/// <param name="innerException">The underlying exception, if any.</param>
public sealed class FrameReaderException(FrameReaderError reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Why it failed.</summary>
    public FrameReaderError Reason { get; } = reason;
}
