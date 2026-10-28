using System.Runtime.ExceptionServices;

namespace FrameReader;

/// <summary>
/// <see cref="VideoFrameReaderOptions.ReadAhead"/>: a thread that decodes ahead, taking each frame from the decoder
/// (<see cref="FrameDecoder.TakeFrame"/>: a reference, no copy) into a queue of at most N, while the reader's caller
/// works. The decoder belongs to that thread while it runs: the reader pauses it (<see cref="Pause"/>, <see cref="Clear"/>)
/// before using the decoder itself (a seek, the index), and the next <see cref="Next"/> lets it go on. Started by the
/// first <see cref="Next"/>, so a seek before the first read is the decoder's own (a deinterlacing decoder can seek
/// only then).
/// </summary>
internal sealed class ReadAhead : IDisposable
{
    /// <summary>A frame read ahead, with what the reader asks of the decoder's current frame.</summary>
    internal sealed record Item(DecodedFrame Frame, long Pts, bool IsKeyframe, long Duration);

    readonly FrameDecoder _decoder;
    readonly int _depth;
    readonly object _lock = new();
    readonly Queue<Item> _queue = new();
    Thread? _thread;
    Item? _current;
    bool _pause, _paused, _stop, _ended;
    ExceptionDispatchInfo? _error;

    internal ReadAhead(FrameDecoder decoder, int depth) => (_decoder, _depth) = (decoder, depth);

    /// <summary>The frame <see cref="Next"/> gave last.</summary>
    internal Item Current => _current ?? throw new InvalidOperationException("No frame read.");

    /// <summary>How many times the reader paused the thread to look at the index (tests: a read every second doesn't).</summary>
    internal int Pauses { get; private set; }

    static int _held;

    /// <summary>Frames taken from decoders and not let go yet, in all readers (tests: none after Dispose).</summary>
    internal static int Held => Volatile.Read(ref _held);

    /// <summary>The next frame, as <see cref="FrameDecoder.Next"/>; what decoding it threw is thrown here, in its turn.</summary>
    internal bool Next()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop, this);
            if (_thread is null)
            {
                _thread = new Thread(Decode) { IsBackground = true, Name = "FrameReader read-ahead" };
                _thread.Start();
            }

            (_pause, _paused) = (false, false);
            Monitor.PulseAll(_lock);
            while (_queue.Count == 0 && !_ended)
                Monitor.Wait(_lock);
            if (_queue.TryDequeue(out var item))
            {
                Monitor.PulseAll(_lock); // room for another
                Release(_current);
                _current = item;
                return true;
            }

            if (_error is { } error)
            {
                // As the decoder: a read after a failed one decodes on.
                (_error, _ended) = (null, false);
                error.Throw();
            }

            Release(_current);
            _current = null;
            return false;
        }
    }

    /// <summary>Waits for the thread to stop between frames, to look at the index; it goes on at the next <see cref="Next"/>.</summary>
    internal void Pause()
    {
        Pauses++;
        Stop();
    }

    void Stop()
    {
        lock (_lock)
        {
            _pause = true;
            Monitor.PulseAll(_lock);
            while (_thread is not null && !_paused)
                Monitor.Wait(_lock);
        }
    }

    /// <summary>Pauses the thread and lets go of the frames read ahead (and the current one): before a seek.</summary>
    internal void Clear()
    {
        Stop();
        lock (_lock)
        {
            DropFrames(); // those from before the seek
            (_ended, _error) = (false, null);
        }
    }

    void DropFrames()
    {
        Release(_current);
        _current = null;
        while (_queue.TryDequeue(out var item))
            Release(item);
    }

    void Decode()
    {
        while (true)
        {
            lock (_lock)
            {
                while (!_stop && (_pause || _ended || _queue.Count >= _depth))
                {
                    if (_pause && !_paused)
                    {
                        _paused = true;
                        Monitor.PulseAll(_lock);
                    }

                    Monitor.Wait(_lock);
                }

                if (_stop)
                    return;
            }

            Item? item = null;
            ExceptionDispatchInfo? error = null;
            try
            {
                if (_decoder.Next())
                {
                    item = new Item(_decoder.TakeFrame(), _decoder.Pts, _decoder.IsKeyframe, _decoder.Duration);
                    Interlocked.Increment(ref _held);
                }
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            lock (_lock)
            {
                if (item is not null)
                    _queue.Enqueue(item);
                else
                    (_ended, _error) = (true, error);
                Monitor.PulseAll(_lock);
            }
        }
    }

    static void Release(Item? item)
    {
        if (item is null)
            return;
        item.Frame.Dispose();
        Interlocked.Decrement(ref _held);
    }

    /// <summary>Stops the thread (after the frame it is decoding) and lets go of the frames.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_lock)
        {
            _stop = true;
            Monitor.PulseAll(_lock);
            thread = _thread;
        }

        thread?.Join();
        DropFrames(); // all, the thread gone
    }
}
