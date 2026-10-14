namespace FrameReader;

/// <summary>
/// The base of <see cref="FrameReaderException"/> and ShotDetector's <c>ShotDetectionException</c> (issue #239): one
/// catch for a failure of the media, the libraries or ffmpeg from either package, each keeping its own
/// <c>Reason</c>. An <see cref="InvalidOperationException"/>, so code catching that still catches both.
/// </summary>
public abstract class MediaException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    protected MediaException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
