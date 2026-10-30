namespace ShotDetector;

/// <summary>
/// A video whose frames the caller decodes (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// what the headers would say. Nothing is probed, so the values must be the ones FFmpeg reports for the same file for
/// the results to equal <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/>'s: the average
/// frame rate, the stream's time base and start pts, the first frame's colour tags and the display rotation.
/// </summary>
public sealed record YuvVideo
{
    /// <summary>Width of the decoded frames in pixels (all frames have this size), as decoded: before <see cref="Rotation"/>.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the decoded frames in pixels; even (swscale's yuv420p conversion, which the converter reproduces, needs that).</summary>
    public required int Height { get; init; }

    /// <summary>
    /// The average frame rate (FFmpeg's avg_frame_rate, which OpenCV reports): turned into scenedetect's fraction as a
    /// file's is. The frames must come at this constant rate (each within half a frame of where it puts it, from frame
    /// 0's pts), unless <see cref="FrameTimestamps"/> are given.
    /// </summary>
    public required Fps FrameRate { get; init; }

    /// <summary>FFmpeg's name of the frames' pixel format: yuv420p (the default) or yuvj420p (full range), so far.</summary>
    public string PixelFormat { get; init; } = "yuv420p";

    /// <summary>ffprobe's color_space of the first frame ("bt709", "smpte170m", "unknown", ...); empty: unknown.</summary>
    public string ColorSpace { get; init; } = "";

    /// <summary>Whether the samples are full range (color_range "pc"); yuvj420p is always.</summary>
    public bool FullRange { get; init; }

    /// <summary>
    /// The time base of <see cref="YuvFrame.Pts"/> (the stream's, e.g. 1/12800). Null: frames carry no timestamps, and
    /// frame n is at n / <see cref="FrameRate"/>; a file's frames are at their own timestamps, rounded to microseconds,
    /// so the times in the CSVs can then differ from a file's in the last digits.
    /// </summary>
    public FrameReader.Rational? TimeBase { get; init; }

    /// <summary>
    /// Every frame's pts in <see cref="TimeBase"/>, in presentation order (a file's packets' pts, sorted: what the file
    /// path labels its frames with); null: unknown, and the frames must come at the constant <see cref="FrameRate"/>.
    /// Given, the frames may come at any rate (variable frame rate, as a file's): each frame must be at its entry, and
    /// <see cref="DetectionOptions.StartTime"/> finds its frame from them as the file path does. A frame the decoder
    /// dropped (an entry skipped) is taken as a file's run takes it, in <see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>;
    /// ranges (<see cref="ShotDetection.Merge"/>) and a start time need every frame at its entry.
    /// </summary>
    public IReadOnlyList<long>? FrameTimestamps { get; init; }

    /// <summary>The stream's start time in <see cref="TimeBase"/> (FFmpeg's AVStream.start_time, ffprobe's start_pts): times count from it.</summary>
    public long StartPts { get; init; }

    /// <summary>The frame count from the headers (ffprobe's nb_frames), which scenedetect takes as the video's length; null if unknown.</summary>
    public long? FrameCount { get; init; }

    /// <summary>The container's duration; null if unknown. Without <see cref="FrameCount"/>, the length is this times the frame rate, as OpenCV's.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// The display rotation in degrees, counterclockwise (the display matrix's angle, ffprobe's rotation; 0 when none), as
    /// <see cref="DetectedVideo.Rotation"/> reports a file's: a quarter turn, 90 and -270 alike. The frames are given as
    /// decoded and turned upright after converting, as a file's (flips ignored, as OpenCV does), and
    /// <see cref="DetectedVideo"/>'s size is the upright one.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>The codec's FFmpeg name (h264, ...), reported back in <see cref="DetectedVideo.Codec"/>.</summary>
    public string Codec { get; init; } = "";

    /// <summary>The container's FFmpeg name (mov,mp4,m4a,3gp,3g2,mj2, ...), reported back in <see cref="DetectedVideo.Container"/>.</summary>
    public string Container { get; init; } = "";
}

/// <summary>
/// One decoded 8-bit 4:2:0 frame (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// its three planes, each row <c>Stride</c> bytes after the one before. The luma plane has the video's width and height,
/// the chroma planes half of each (rounded up). The planes are read before the enumeration moves on, so a decoder can
/// reuse its buffers for the next frame.
/// </summary>
/// <param name="Y">The luma plane.</param>
/// <param name="YStride">Bytes from one luma row to the next, at least the width.</param>
/// <param name="U">The Cb plane.</param>
/// <param name="UStride">Bytes from one Cb row to the next.</param>
/// <param name="V">The Cr plane.</param>
/// <param name="VStride">Bytes from one Cr row to the next.</param>
public readonly record struct YuvFrame(ReadOnlyMemory<byte> Y, int YStride, ReadOnlyMemory<byte> U, int UStride, ReadOnlyMemory<byte> V, int VStride)
{
    /// <summary>The frame's presentation timestamp in <see cref="YuvVideo.TimeBase"/> (unused when that is null).</summary>
    public long Pts { get; init; }
}
