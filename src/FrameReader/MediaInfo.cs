namespace FrameReader;

/// <summary>A rational number as FFmpeg keeps frame rates and time bases (num/den).</summary>
/// <param name="Num">Numerator.</param>
/// <param name="Den">Denominator (0 when FFmpeg doesn't know the value).</param>
public readonly record struct Rational(int Num, int Den)
{
    /// <summary>The value as a double; 0 when <see cref="Den"/> is 0.</summary>
    public double Value => Den == 0 ? 0 : Num / (double)Den;

    /// <inheritdoc/>
    public override string ToString() => $"{Num}/{Den}";

    /// <summary><paramref name="pts"/> in <paramref name="timeBase"/> units as a TimeSpan, to the nearest tick; null if unknown.</summary>
    internal static TimeSpan? Ticks(long? pts, Rational timeBase) => pts is long p && timeBase.Den != 0
        ? TimeSpan.FromTicks((long)Math.Round((decimal)p * timeBase.Num * TimeSpan.TicksPerSecond / timeBase.Den))
        : null;
}

/// <summary>How a video stream's fields are ordered (FFmpeg's AVFieldOrder).</summary>
public enum FieldOrder
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>Progressive.</summary>
    Progressive,

    /// <summary>Interlaced, top field coded and shown first (tt).</summary>
    TopFirst,

    /// <summary>Interlaced, bottom field coded and shown first (bb).</summary>
    BottomFirst,

    /// <summary>Interlaced, top field coded first, bottom shown first (tb).</summary>
    TopCodedBottomFirst,

    /// <summary>Interlaced, bottom field coded first, top shown first (bt).</summary>
    BottomCodedTopFirst,
}

/// <summary>
/// A video stream's properties as its headers give them (FFmpeg's values, unconverted: what ffprobe prints for the
/// stream). Timestamps are in <see cref="TimeBase"/> units.
/// </summary>
public sealed record VideoStreamInfo
{
    internal VideoStreamInfo()
    {
    }

    /// <summary>The stream's index in the container.</summary>
    public int Index { get; init; }

    /// <summary>The codec's FFmpeg name (h264, hevc, mpeg2video, ...).</summary>
    public string Codec { get; init; } = "";

    /// <summary>Coded width in pixels (before any display rotation).</summary>
    public int Width { get; init; }

    /// <summary>Coded height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>FFmpeg's pixel format name (yuv420p, yuv422p10le, ...); null if unknown.</summary>
    public string? PixelFormat { get; init; }

    /// <summary>FFmpeg's colour range name (tv, pc); null if unspecified.</summary>
    public string? ColorRange { get; init; }

    /// <summary>FFmpeg's colour space name (bt709, smpte170m, ...); null if unspecified.</summary>
    public string? ColorSpace { get; init; }

    /// <summary>Where the chroma samples sit (left, center, topleft, ...), as ffprobe's chroma_location; null when unspecified.</summary>
    public string? ChromaLocation { get; init; }

    /// <summary>Field order.</summary>
    public FieldOrder FieldOrder { get; init; }

    /// <summary>The real base frame rate (r_frame_rate).</summary>
    public Rational FrameRate { get; init; }

    /// <summary>The average frame rate (avg_frame_rate).</summary>
    public Rational AverageFrameRate { get; init; }

    /// <summary>The unit of the stream's timestamps, in seconds.</summary>
    public Rational TimeBase { get; init; }

    /// <summary>The first timestamp; null if unknown.</summary>
    public long? StartPts { get; init; }

    /// <summary>The stream's duration in <see cref="TimeBase"/> units; null if unknown.</summary>
    public long? DurationPts { get; init; }

    /// <summary>The duration as a <see cref="TimeSpan"/> (<see cref="DurationPts"/> in <see cref="TimeBase"/> units); null if unknown.</summary>
    public TimeSpan? Duration => Rational.Ticks(DurationPts, TimeBase);

    /// <summary>The frame count the container states; null if it doesn't.</summary>
    public long? FrameCount { get; init; }

    /// <summary>The display matrix's rotation in degrees, counterclockwise (av_display_rotation_get); null if none.</summary>
    public double? Rotation { get; init; }

    /// <summary>
    /// The codec's extradata (its out-of-band headers: avcC for H.264 in mp4, an MPEG-4 Part 2 stream's VOL, ...), as
    /// ffprobe's <c>-show_data</c> prints it for the stream; empty if none.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData { get; init; }
}

/// <summary>What a stream carries (FFmpeg's media type).</summary>
public enum StreamType
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>Video.</summary>
    Video,

    /// <summary>Audio.</summary>
    Audio,

    /// <summary>Subtitles.</summary>
    Subtitle,

    /// <summary>Data (timecode tracks, metadata, ...).</summary>
    Data,

    /// <summary>An attachment (fonts in mkv, ...).</summary>
    Attachment,
}

/// <summary>
/// One stream of a media file as its headers describe it: what ffprobe's <c>-show_streams</c> prints for it, FFmpeg's
/// values unconverted (timestamps in <see cref="TimeBase"/> units).
/// </summary>
public sealed record StreamInfo
{
    /// <summary>An empty description, to fill from another source (ShotDetector does from ffprobe, without the libraries).</summary>
    public StreamInfo()
    {
    }

    /// <summary>The stream's index in the container.</summary>
    public int Index { get; init; }

    /// <summary>Video, audio, subtitle, data or attachment.</summary>
    public StreamType Type { get; init; }

    /// <summary>The codec's FFmpeg name (h264, aac, subrip, ...); "unknown" when FFmpeg doesn't know it.</summary>
    public string Codec { get; init; } = "";

    /// <summary>The codec tag as text, as ffprobe's codec_tag_string (hvc1 or hev1 for HEVC in mp4; [0][0][0][0] when none).</summary>
    public string CodecTag { get; init; } = "";

    /// <summary>The language tag (eng, swe, ...); null when there's none.</summary>
    public string? Language { get; init; }

    /// <summary>The title tag; null when there's none.</summary>
    public string? Title { get; init; }

    /// <summary>Flagged as the default track of its type.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Flagged as forced (subtitles shown even when subtitles are off).</summary>
    public bool IsForced { get; init; }

    /// <summary>The bit rate in bits per second, when the container says; null otherwise.</summary>
    public long? BitRate { get; init; }

    /// <summary>The stream's duration in <see cref="TimeBase"/> units; null if unknown.</summary>
    public long? DurationPts { get; init; }

    /// <summary>The duration as a <see cref="TimeSpan"/> (<see cref="DurationPts"/> in <see cref="TimeBase"/> units); null if unknown.</summary>
    public TimeSpan? Duration => Rational.Ticks(DurationPts, TimeBase);

    /// <summary>The unit of the stream's timestamps, in seconds.</summary>
    public Rational TimeBase { get; init; }

    /// <summary>Video: width in pixels; otherwise null.</summary>
    public int? Width { get; init; }

    /// <summary>Video: height in pixels; otherwise null.</summary>
    public int? Height { get; init; }

    /// <summary>Audio: samples per second; otherwise null.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Audio: channels; otherwise null.</summary>
    public int? Channels { get; init; }

    /// <summary>Audio: FFmpeg's channel layout name (mono, stereo, 5.1(side), ...); null when unspecified or not audio.</summary>
    public string? ChannelLayout { get; init; }

    /// <summary>Audio: FFmpeg's sample format name (fltp, s16, ...); otherwise null.</summary>
    public string? SampleFormat { get; init; }
}

/// <summary>A media file's properties from its headers (<see cref="MediaProbe"/>).</summary>
public sealed record MediaInfo
{
    internal MediaInfo()
    {
    }

    /// <summary>FFmpeg's name for the container (its demuxer): mov,mp4,m4a,3gp,3g2,mj2, matroska,webm, mxf, ...</summary>
    public string Container { get; init; } = "";

    /// <summary>The container's duration; null if unknown.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The first video stream; null if there is none.</summary>
    public VideoStreamInfo? Video { get; init; }

    /// <summary>Whether there is an audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Every stream, in the container's order.</summary>
    public IReadOnlyList<StreamInfo> Streams { get; init; } = [];

    /// <summary>
    /// Every packet's pts in the video stream, in file order (null entries: packets without one); null unless asked
    /// for (<see cref="ProbeOptions.PacketTimestamps"/>). Reads the whole file's packets, without decoding.
    /// </summary>
    public IReadOnlyList<long?>? PacketTimestamps { get; init; }

    /// <summary>
    /// Every video packet's byte position in the file, as ffprobe's <c>packet=pos</c>, in the order of
    /// <see cref="PacketTimestamps"/> (null entries: unknown); read along with them.
    /// </summary>
    public IReadOnlyList<long?>? PacketPositions { get; init; }

    /// <summary>
    /// The video stream's first packet's bytes, as ffprobe's <c>-show_entries packet=data -show_data -read_intervals
    /// %+#1</c> prints them (what a decoder reads first: an MPEG-4 Part 2 stream's headers and user data, ...); null
    /// unless asked for (<see cref="ProbeOptions.FirstVideoPacket"/>), or when there is no video packet.
    /// </summary>
    public ReadOnlyMemory<byte>? FirstVideoPacket { get; init; }
}

/// <summary>What <see cref="MediaProbe"/> reads besides the headers, and how it opens the input.</summary>
public sealed record ProbeOptions
{
    /// <summary>Also read every video packet's pts and position (<see cref="MediaInfo.PacketTimestamps"/>, <see cref="MediaInfo.PacketPositions"/>).</summary>
    public bool PacketTimestamps { get; init; }

    /// <summary>Also read the first video packet's bytes (<see cref="MediaInfo.FirstVideoPacket"/>): reads up to that packet only.</summary>
    public bool FirstVideoPacket { get; init; }

    /// <summary>
    /// FFmpeg demuxer options, as ffmpeg's input options without the dash (for an image sequence pattern such as
    /// <c>frames/%04d.png</c>: <c>framerate</c> = <c>25/1</c>). A name no demuxer or protocol takes is an <see cref="ArgumentException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }

    /// <summary>
    /// Stops opening and reading the input where FFmpeg waits, as on a network read (<see cref="OperationCanceledException"/>,
    /// or the read ends with an error); none by default.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Tests: read an MP4/MOV file's media data in the packet scan (<see cref="PacketTimestamps"/>) rather than only its
    /// sample tables (same timestamps and positions).
    /// </summary>
    internal bool ReadsMediaData { get; init; }

    /// <summary>Tests: told how many bytes the probe read of a file it read through FileInput (else not called).</summary>
    internal Action<long>? OnFileBytesRead { get; init; }
}
