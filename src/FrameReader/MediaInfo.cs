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
    internal VideoStreamInfo() { }

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

    /// <summary>The frame count the container states; null if it doesn't.</summary>
    public long? FrameCount { get; init; }

    /// <summary>The display matrix's rotation in degrees, counterclockwise (av_display_rotation_get); null if none.</summary>
    public double? Rotation { get; init; }
}

/// <summary>A media file's properties from its headers (<see cref="MediaProbe"/>).</summary>
public sealed record MediaInfo
{
    internal MediaInfo() { }

    /// <summary>FFmpeg's name for the container (its demuxer): mov,mp4,m4a,3gp,3g2,mj2, matroska,webm, mxf, ...</summary>
    public string Container { get; init; } = "";

    /// <summary>The container's duration in microseconds; null if unknown.</summary>
    public long? DurationMicroseconds { get; init; }

    /// <summary>The first video stream; null if there is none.</summary>
    public VideoStreamInfo? Video { get; init; }

    /// <summary>Whether there is an audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>
    /// Every packet's pts in the video stream, in file order (null entries: packets without one); null unless asked
    /// for (<see cref="ProbeOptions.PacketTimestamps"/>). Reads the whole file's packets, without decoding.
    /// </summary>
    public IReadOnlyList<long?>? PacketTimestamps { get; init; }
}

/// <summary>What <see cref="MediaProbe"/> reads besides the headers, and how it opens the input.</summary>
public sealed record ProbeOptions
{
    /// <summary>Also read every video packet's pts (<see cref="MediaInfo.PacketTimestamps"/>).</summary>
    public bool PacketTimestamps { get; init; }

    /// <summary>
    /// FFmpeg demuxer options, as ffmpeg's input options without the dash (for an image sequence pattern such as
    /// <c>frames/%04d.png</c>: <c>framerate</c> = <c>25/1</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }

    /// <summary>The folder with FFmpeg's libraries (<see cref="FFmpegLibraries.Load"/>); null: next to the app, then the system's.</summary>
    public string? LibraryDirectory { get; init; }
}
