using FFmpeg.AutoGen;

namespace ShotDetector;

/// <summary>
/// A .NET Stream as FFmpeg's input (a custom AVIOContext): <c>prefix</c> first (the bytes already read to probe it),
/// then the rest of the stream, read once and not seekable, as ffmpeg reads a pipe.
/// </summary>
internal sealed unsafe class StreamInput : IDisposable
{
    const int BufferSize = 1 << 16;
    readonly byte[] _prefix;
    readonly Stream? _rest;
    readonly avio_alloc_context_read_packet _read; // kept alive while FFmpeg holds a pointer to it
    int _at;

    /// <summary>The context to set as AVFormatContext.pb (with AVFMT_FLAG_CUSTOM_IO).</summary>
    public AVIOContext* Context { get; private set; }

    /// <summary>What reading the stream threw, if it did (FFmpeg only sees an I/O error).</summary>
    public Exception? Error { get; private set; }

    public StreamInput(byte[] prefix, Stream? rest)
    {
        _prefix = prefix;
        _rest = rest;
        _read = Read;
        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
        Context = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, _read, null, null);
        Context->seekable = 0;
    }

    int Read(void* opaque, byte* buf, int size)
    {
        try
        {
            var span = new Span<byte>(buf, size);
            if (_at < _prefix.Length)
            {
                int n = Math.Min(size, _prefix.Length - _at);
                _prefix.AsSpan(_at, n).CopyTo(span);
                _at += n;
                return n;
            }
            int read = _rest?.Read(span) ?? 0;
            return read > 0 ? read : ffmpeg.AVERROR_EOF;
        }
        catch (Exception e)
        {
            Error = e;
            return ffmpeg.AVERROR(5); // EIO
        }
    }

    /// <summary>Frees the context (after avformat_close_input, which leaves a custom one alone).</summary>
    public void Dispose()
    {
        if (Context == null)
            return;
        var c = Context;
        ffmpeg.av_freep(&c->buffer);
        ffmpeg.avio_context_free(&c);
        Context = null;
    }
}
