using System.Text;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A local MP4/MOV file as FFmpeg's input (a custom AVIOContext, seekable as the file protocol is) whose media data, the
/// payload of each top-level <c>mdat</c> box, reads as zeros once <see cref="SkipMediaData"/> is set: libavformat's mov
/// demuxer gives every packet's pts, dts and position from the sample tables in <c>moov</c>, so a packet scan that wants
/// only those needn't read the file. Made by <see cref="For"/> only for a file it can say that of.
/// </summary>
internal sealed unsafe class FileInput : IDisposable
{
    const int BufferSize = 1 << 15; // the file protocol's (IO_BUFFER_SIZE)
    readonly FileStream _file;
    readonly long _length;
    readonly (long Start, long End)[] _mediaData;
    readonly avio_alloc_context_read_packet _read; // kept alive while FFmpeg holds a pointer to it
    readonly avio_alloc_context_seek _seek;
    readonly CancellationToken _cancellationToken;
    long _at;

    FileInput(FileStream file, (long Start, long End)[] mediaData, CancellationToken cancellationToken)
    {
        _file = file;
        _cancellationToken = cancellationToken;
        _length = file.Length;
        _mediaData = mediaData;
        _read = Read;
        _seek = Seek;
        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
        Context = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, _read, null, _seek);
    }

    /// <summary>The context to set as AVFormatContext.pb (with AVFMT_FLAG_CUSTOM_IO).</summary>
    public AVIOContext* Context { get; private set; }

    /// <summary>Reads inside the media data give zeros from now on (after the headers have been probed).</summary>
    public bool SkipMediaData { get; set; }

    /// <summary>Bytes read from the file so far (tests).</summary>
    public long BytesRead { get; private set; }

    /// <summary>What reading the file threw, if it did (FFmpeg only sees an I/O error).</summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// The input for <paramref name="path"/> when it is a local file laid out as ISO base media (top-level boxes from the
    /// start, the first a known one) with media data and no movie fragments (<c>moof</c>: their timing is read from boxes
    /// among the media data); null otherwise, or where its boxes don't add up.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Stops a read (FFmpeg's interrupt callback isn't asked on a custom context's reads).</param>
    public static FileInput? For(string path, CancellationToken cancellationToken)
    {
        if (path.Contains("://", StringComparison.Ordinal) || path.Contains('%', StringComparison.Ordinal) || !File.Exists(path))
            return null;
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            if (MediaData(file) is { Length: > 0 } mediaData)
                return new FileInput(file, mediaData, cancellationToken);
        }
        catch (IOException)
        {
        }

        file.Dispose();
        return null;
    }

    /// <summary>The payload of every top-level <c>mdat</c> box (within the file); null where the file isn't one <see cref="For"/> takes.</summary>
    internal static (long Start, long End)[]? MediaData(Stream file)
    {
        long length = file.Length, at = 0;
        var result = new List<(long, long)>();
        Span<byte> header = stackalloc byte[16];
        bool first = true;
        while (at + 8 <= length)
        {
            file.Position = at;
            file.ReadExactly(header[..8]);
            long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            string type = Encoding.ASCII.GetString(header[4..8]);
            int headerSize = 8;
            if (size == 1)
            {
                if (at + 16 > length)
                    return null;
                file.ReadExactly(header[8..16]);
                size = 0;
                for (int i = 8; i < 16; i++)
                    size = size << 8 | header[i];
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - at; // to the end of the file
            }

            if (size < headerSize || (first && type is not ("ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "pnot" or "uuid")))
                return null;
            if (type is "moof" or "mfra" or "sidx")
                return null; // fragmented
            if (type == "mdat" && at + headerSize < Math.Min(at + size, length))
                result.Add((at + headerSize, Math.Min(at + size, length)));
            first = false;
            if (size > length - at)
                break; // the last box, cut short
            at += size;
        }

        return [.. result];
    }

    int Read(void* opaque, byte* buf, int size)
    {
        try
        {
            if (_cancellationToken.IsCancellationRequested)
                return ffmpeg.AVERROR_EXIT;
            if (_at >= _length)
                return ffmpeg.AVERROR_EOF;
            int n = (int)Math.Min(size, _length - _at);
            if (SkipMediaData)
            {
                foreach (var (start, end) in _mediaData)
                {
                    if (_at >= start && _at < end)
                    {
                        n = (int)Math.Min(n, end - _at);
                        new Span<byte>(buf, n).Clear();
                        _at += n;
                        return n;
                    }

                    if (_at < start && _at + n > start)
                        n = (int)(start - _at); // up to the media data, then zeros
                }
            }

            _file.Position = _at;
            int read = _file.Read(new Span<byte>(buf, n));
            if (read <= 0)
                return ffmpeg.AVERROR_EOF;
            _at += read;
            BytesRead += read;
            return read;
        }
        catch (Exception e)
        {
            Error = e;
            return ffmpeg.AVERROR(5); // EIO
        }
    }

    long Seek(void* opaque, long offset, int whence)
    {
        whence &= ~ffmpeg.AVSEEK_FORCE;
        if (whence == ffmpeg.AVSEEK_SIZE)
            return _length;
        long to = whence switch
        {
            0 => offset,              // SEEK_SET
            1 => _at + offset,        // SEEK_CUR
            2 => _length + offset,    // SEEK_END
            _ => -1,
        };
        if (to < 0)
            return ffmpeg.AVERROR(22); // EINVAL
        _at = to;
        return to;
    }

    /// <summary>Frees the context (after avformat_close_input, which leaves a custom one alone) and closes the file.</summary>
    public void Dispose()
    {
        if (Context != null)
        {
            var c = Context;
            ffmpeg.av_freep(&c->buffer);
            ffmpeg.avio_context_free(&c);
            Context = null;
        }

        _file.Dispose();
    }
}
