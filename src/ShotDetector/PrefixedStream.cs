namespace ShotDetector;

/// <summary>
/// The bytes already read from a stream (to probe it), then the rest of it: read once, not seekable, as ffmpeg reads a
/// pipe. The inner stream isn't disposed.
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
{
    int _at;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_at < prefix.Length)
        {
            int n = Math.Min(buffer.Length, prefix.Length - _at);
            prefix.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
        return rest.Read(buffer);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
