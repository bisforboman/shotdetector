using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A .NET Stream as FFmpeg's input (a custom AVIOContext): <c>prefix</c> first (the bytes already read to probe it),
/// then the rest of the stream, read once and not seekable, as ffmpeg reads a pipe: like its <c>fd:</c> protocol (what
/// <c>-i -</c> opens) on one, the size is 0 and seeking fails. With no seek at all, a demuxer asking for the size got an
/// error, which mp3dec takes for a huge file ("invalid concatenated file") and then keeps the encoder's end padding.
/// </summary>
internal sealed unsafe class StreamInput : IDisposable
{
    const int BufferSize = 1 << 16;
    readonly byte[] _prefix;
    readonly Stream? _rest;
    readonly avio_alloc_context_read_packet _read; // kept alive while FFmpeg holds a pointer to it
    readonly avio_alloc_context_seek _seek;
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
        _seek = (_, _, whence) => whence == ffmpeg.AVSEEK_SIZE ? 0 : ffmpeg.AVERROR(29); // fstat of a FIFO; lseek: ESPIPE
        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
        // Without a seek function FFmpeg marks the context not seekable itself; the function goes in afterwards. Not
        // through the seekable field: FFmpeg.AutoGen lays AVIOContext out for 8-byte longs, and on Windows (4-byte
        // `unsigned long checksum`) every field from checksum on sits 8 bytes earlier than it says.
        Context = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, _read, null, null);
        Context->seek = new AVIOContext_seek_func { Pointer = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_seek) };
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
