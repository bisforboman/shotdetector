namespace ShotDetector;

/// <summary>
/// The frames from the caller as the reader takes them (<see cref="VideoReader"/>'s <c>TakeFrame</c>): one frame at a time,
/// put here by the run's driver just before the step that takes it (<see cref="ShotDetection"/>'s <c>Steps</c>), from the
/// caller's <see cref="IEnumerator{T}"/> or, awaited, <see cref="IAsyncEnumerator{T}"/>. So both frame-source overloads run
/// the same steps on the same frames, and the asynchronous one awaits its source between steps, on no other thread.
/// </summary>
internal sealed class FrameFeed : IEnumerator<YuvFrame>
{
    YuvFrame _next;
    bool _full;

    /// <summary>The source has no more frames: the reader is told so from now on.</summary>
    public bool Ended { get; private set; }

    /// <summary>Whether the driver must put a frame (or the end) here before the next step.</summary>
    public bool NeedsFrame => !_full && !Ended;

    /// <inheritdoc/>
    public YuvFrame Current { get; private set; }

    object System.Collections.IEnumerator.Current => Current;

    public void Put(YuvFrame frame)
    {
        _next = frame;
        _full = true;
    }

    public void End() => Ended = true;

    /// <inheritdoc/>
    public bool MoveNext()
    {
        if (_full)
        {
            Current = _next;
            _next = default;
            _full = false;
            return true;
        }

        // A step took a frame its driver didn't put here: a bug in the steps, never the end of the video.
        return Ended ? false : throw new InvalidOperationException("A frame was taken before the driver put one in the feed.");
    }

    /// <inheritdoc/>
    public void Reset() => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
