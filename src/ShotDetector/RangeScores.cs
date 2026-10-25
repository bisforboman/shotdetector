using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ShotDetector;

/// <summary>
/// One range's scores (<see cref="ShotDetection.ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>),
/// for <see cref="ShotDetection.Merge"/>: each detector's score of every frame from <see cref="FirstFrame"/> on, the
/// frames' pts (from the frame before, which the scores compared with), their stats, the warnings, and a fingerprint of
/// the video and options they were scored with, which the merge compares. <see cref="ToBytes"/> and
/// <see cref="FromBytes"/> carry them between runtimes (a browser Web Worker's postMessage).
/// </summary>
public sealed class RangeScores
{
    /// <summary>The bytes' first four: "SDRS".</summary>
    static ReadOnlySpan<byte> Magic => "SDRS"u8;

    /// <summary>The byte format's version, after the magic. A reader refuses any other.</summary>
    internal const ushort FormatVersion = 1;

    /// <summary>The most detectors bytes may hold, so a damaged count can't allocate much.</summary>
    const int MaxDetectors = 1024;

    internal RangeScores(string fingerprint, int firstFrame, int frameCount, long[] pts, double?[][] metrics, (int Frame, string Key, double Value)[]? stats, string[] warnings)
    {
        Fingerprint = fingerprint;
        FirstFrame = firstFrame;
        FrameCount = frameCount;
        Pts = pts;
        Metrics = metrics;
        Stats = stats;
        Warnings = warnings;
    }

    /// <summary>The 0-based index in the video of the range's first frame (ScoreRange's <c>firstFrame</c>).</summary>
    public int FirstFrame { get; }

    /// <summary>
    /// How many frames were scored from <see cref="FirstFrame"/> on: the range's own and, when another range follows, the
    /// frames from its first one on, which <see cref="ShotDetection.Merge"/> compares with that range's.
    /// </summary>
    public int FrameCount { get; }

    /// <summary>Warnings from scoring the range; the merge's result lists them.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The video and options the range was scored with (<see cref="ShotDetection"/>'s <c>Fingerprint</c>).</summary>
    internal string Fingerprint { get; }

    /// <summary>Every frame's pts from the frame before <see cref="FirstFrame"/> (when it is above 0); empty when the video has no time base.</summary>
    internal long[] Pts { get; }

    /// <summary>Each detector's score of each frame from <see cref="FirstFrame"/> on (<see cref="FrameCount"/> each).</summary>
    internal double?[][] Metrics { get; }

    /// <summary>The stats rows of the frames from <see cref="FirstFrame"/> on; null without <see cref="DetectionOptions.CollectStats"/>.</summary>
    internal (int Frame, string Key, double Value)[]? Stats { get; }

    /// <summary>
    /// The scores as bytes, for <see cref="FromBytes"/> in another runtime: little-endian binary, "SDRS" and the format's
    /// version first, a checksum last; every double as its exact bits.
    /// </summary>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes()
    {
        var w = new Writer();
        w.Bytes(Magic);
        w.UInt16(FormatVersion);
        w.String(Fingerprint);
        w.Int32(FirstFrame);
        w.Int32(FrameCount);
        w.Int32(Pts.Length);
        foreach (long p in Pts)
            w.Int64(p);
        w.Int32(Metrics.Length);
        foreach (var scores in Metrics)
        {
            foreach (double? m in scores)
            {
                w.Byte(m is null ? (byte)0 : (byte)1);
                if (m is { } v)
                    w.Int64(BitConverter.DoubleToInt64Bits(v));
            }
        }

        w.Byte(Stats is null ? (byte)0 : (byte)1);
        if (Stats is not null)
        {
            var keys = Stats.Select(s => s.Key).Distinct().ToList();
            var index = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
            w.Int32(keys.Count);
            foreach (string key in keys)
                w.String(key);
            w.Int32(Stats.Length);
            foreach (var (frame, key, value) in Stats)
            {
                w.Int32(frame);
                w.Int32(index[key]);
                w.Int64(BitConverter.DoubleToInt64Bits(value));
            }
        }

        w.Int32(Warnings.Count);
        foreach (string warning in Warnings)
            w.String(warning);
        w.UInt64(Checksum(w.Written));
        return w.ToArray();
    }

    /// <summary>
    /// Scores from <see cref="ToBytes"/>'s bytes. Refuses (a <see cref="ShotDetectionException"/>,
    /// <see cref="ShotDetectionError.InvalidInput"/>) bytes that aren't RangeScores, of another format version, truncated
    /// or corrupted (the checksum), or inconsistent.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The scores.</returns>
    /// <exception cref="ShotDetectionException">The bytes aren't RangeScores this version reads, whole and unchanged.</exception>
    public static RangeScores FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length + 2 || !bytes[..Magic.Length].SequenceEqual(Magic))
            throw Invalid("These bytes aren't RangeScores (no \"SDRS\" header).");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[Magic.Length..]);
        if (version != FormatVersion)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture,
                $"These RangeScores bytes are format version {version}; this ShotDetector reads version {FormatVersion}: score the ranges and merge them with the same ShotDetector version."));
        }

        if (bytes.Length < Magic.Length + 2 + 8)
            throw Invalid("These RangeScores bytes are truncated.");
        var body = bytes[..^8];
        if (Checksum(body) != BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]))
            throw Invalid("These RangeScores bytes are truncated or corrupted (their checksum doesn't match).");

        var r = new Reader(body[(Magic.Length + 2)..]);
        string fingerprint = r.String();
        int firstFrame = r.Count();
        int frameCount = r.Count();
        if (frameCount > int.MaxValue - 1 - firstFrame)
            throw Invalid("These RangeScores bytes are inconsistent (the range ends past the largest frame index).");
        var pts = new long[r.Count(8)];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = r.Int64();
        int detectors = r.Int32();
        if (detectors is < 1 or > MaxDetectors)
            throw Invalid("These RangeScores bytes are inconsistent (the detector count).");
        var metrics = new double?[detectors][];
        for (int d = 0; d < metrics.Length; d++)
        {
            metrics[d] = new double?[r.Check(frameCount, 1)];
            for (int k = 0; k < frameCount; k++)
            {
                metrics[d][k] = r.Byte() switch
                {
                    0 => null,
                    1 => BitConverter.Int64BitsToDouble(r.Int64()),
                    _ => throw Invalid("These RangeScores bytes are inconsistent (a score's flag)."),
                };
            }
        }

        (int, string, double)[]? stats = null;
        switch (r.Byte())
        {
            case 0:
                break;
            case 1:
                var keys = new string[r.Count(4)];
                for (int i = 0; i < keys.Length; i++)
                    keys[i] = r.String();
                stats = new (int, string, double)[r.Count(16)];
                for (int i = 0; i < stats.Length; i++)
                {
                    int frame = r.Int32();
                    int key = r.Int32();
                    if (key < 0 || key >= keys.Length)
                        throw Invalid("These RangeScores bytes are inconsistent (a stats key).");
                    stats[i] = (frame, keys[key], BitConverter.Int64BitsToDouble(r.Int64()));
                }

                break;
            default:
                throw Invalid("These RangeScores bytes are inconsistent (the stats flag).");
        }

        var warnings = new string[r.Count(4)];
        for (int i = 0; i < warnings.Length; i++)
            warnings[i] = r.String();
        if (!r.AtEnd)
            throw Invalid("These RangeScores bytes are inconsistent (bytes left over).");
        if (pts.Length != 0 && pts.Length != frameCount + (firstFrame > 0 ? 1 : 0))
            throw Invalid("These RangeScores bytes are inconsistent (the frame counts).");
        return new RangeScores(fingerprint, firstFrame, frameCount, pts, metrics, stats, warnings);
    }

    static ShotDetectionException Invalid(string message) => new(ShotDetectionError.InvalidInput, message);

    /// <summary>FNV-1a, 64-bit, over the bytes before it: catches a truncated or damaged message (not a forged one).</summary>
    internal static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in bytes)
            hash = (hash ^ b) * 1099511628211;
        return hash;
    }

    sealed class Writer
    {
        readonly MemoryStream _stream = new();

        public ReadOnlySpan<byte> Written => _stream.GetBuffer().AsSpan(0, (int)_stream.Length);

        public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void Byte(byte value) => _stream.WriteByte(value);

        public void UInt16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            _stream.Write(b);
        }

        public void Int32(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            _stream.Write(b);
        }

        public void Int64(long value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, value);
            _stream.Write(b);
        }

        public void UInt64(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            _stream.Write(b);
        }

        public void String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Int32(bytes.Length);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    /// <summary>Reads the body, refusing any count or length past its end before allocating for it.</summary>
    ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        readonly ReadOnlySpan<byte> _bytes = bytes;
        int _at;

        public readonly bool AtEnd => _at == _bytes.Length;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > _bytes.Length - _at)
                throw Invalid("These RangeScores bytes are inconsistent (they end too early).");
            var span = _bytes.Slice(_at, n);
            _at += n;
            return span;
        }

        public byte Byte() => Take(1)[0];

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        /// <summary>A count, not negative, of items at least <paramref name="itemBytes"/> bytes each that the rest can hold.</summary>
        public int Count(int itemBytes = 0) => Check(Int32(), itemBytes);

        public readonly int Check(int count, int itemBytes)
        {
            if (count < 0 || (long)count * itemBytes > _bytes.Length - _at)
                throw Invalid("These RangeScores bytes are inconsistent (a count past their end).");
            return count;
        }

        public string String() => Encoding.UTF8.GetString(Take(Count()));
    }
}
