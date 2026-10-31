namespace ShotDetector;

/// <summary>
/// A video whose frames the caller decodes (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// what the headers would say. Nothing is probed, so the values must be the ones FFmpeg reports for the same file for
/// the results to equal <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/>'s: the average
/// frame rate, the stream's time base and start pts, the first frame's colour tags, the display rotation and the field
/// order.
/// </summary>
public sealed record YuvVideo
{
    /// <summary>Width of the decoded frames in pixels (all frames have this size), as decoded: before <see cref="Rotation"/>.</summary>
    public required int Width { get; init; }

    /// <summary>
    /// Height of the decoded frames in pixels. An odd one (8-bit formats) needs a converter that implements
    /// <see cref="IYuvOddHeightConverter"/> (FastYuv's does: swscale scales such frames, as 10-bit ones) and an even width.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// The average frame rate (FFmpeg's avg_frame_rate, which OpenCV reports): turned into scenedetect's fraction as a
    /// file's is. The frames must come at this constant rate (each within half a frame of where it puts it, from frame
    /// 0's pts), unless <see cref="FrameTimestamps"/> are given.
    /// </summary>
    public required Fps FrameRate { get; init; }

    /// <summary>
    /// FFmpeg's name of the frames' pixel format: yuv420p (the default), yuvj420p (full range), yuv422p, yuvj422p or
    /// yuv420p10le (10-bit 4:2:0, 16-bit little-endian samples: see <see cref="YuvFrame"/>), so far.
    /// </summary>
    public string PixelFormat { get; init; } = "yuv420p";

    /// <summary>ffprobe's color_space of the first frame ("bt709", "smpte170m", "unknown", ...); empty: unknown.</summary>
    public string ColorSpace { get; init; } = "";

    /// <summary>Whether the samples are full range (color_range "pc"); yuvj420p and yuvj422p are always.</summary>
    public bool FullRange { get; init; }

    /// <summary>
    /// ffprobe's chroma_location of the first frame ("left", "center", "topleft", ...); null (the default), empty or
    /// "unspecified": unspecified, which swscale takes as center. Used for yuv420p10le and for 8-bit frames of an odd
    /// height only, as by the file path: swscale interpolates their chroma from where its samples sit (h264 and HEVC
    /// 4:2:0 are usually "left").
    /// </summary>
    public string? ChromaLocation { get; init; }

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

    /// <summary>
    /// The stream's field order as FFmpeg reports it (ffprobe's field_order: from the H.264 parser, the MPEG-2 sequence
    /// header, ...), which <see cref="DeinterlaceMode.Auto"/> goes by and <see cref="DetectedVideo.FieldOrder"/> reports.
    /// Unknown (the default) and Progressive aren't deinterlaced by Auto.
    /// </summary>
    public FrameReader.FieldOrder FieldOrder { get; init; }

    /// <summary>The codec's FFmpeg name (h264, ...), reported back in <see cref="DetectedVideo.Codec"/>.</summary>
    public string Codec { get; init; } = "";

    /// <summary>The container's FFmpeg name (mov,mp4,m4a,3gp,3g2,mj2, ...), reported back in <see cref="DetectedVideo.Container"/>.</summary>
    public string Container { get; init; } = "";
}

/// <summary>
/// One decoded frame (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>)
/// in <see cref="YuvVideo.PixelFormat"/>: its three planes, each row <c>Stride</c> bytes after the one before. The luma
/// plane has the video's width and height; the chroma planes half its width (rounded up) and, for 4:2:0, half its height
/// (rounded up), for 4:2:2 all of it. yuv420p10le's samples are 16-bit little-endian (2 bytes each, the strides still in
/// bytes), as FFmpeg's planes and WebCodecs' I420P10 copies are laid out. A frame of another size (<see cref="Width"/>, <see cref="Height"/>) is
/// refused so far. The planes are read before the enumeration moves on, so a decoder can
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
    /// <summary>
    /// The frame's width in pixels as decoded; 0 (the default): <see cref="YuvVideo.Width"/>. A frame of another size
    /// than the video's (a stream whose resolution changes midway) is refused with a <see cref="ShotDetectionException"/>
    /// (<see cref="ShotDetectionError.InvalidInput"/>) so far: the file path scales it to the stream's size as OpenCV
    /// does, which the frame source can't reproduce yet, and its planes read at the video's size would give another
    /// video's results.
    /// </summary>
    public int Width { get; init; }

    /// <summary>The frame's height in pixels as decoded; 0 (the default): <see cref="YuvVideo.Height"/>. See <see cref="Width"/>.</summary>
    public int Height { get; init; }

    /// <summary>
    /// The frame's colour matrix as ffprobe names it (its color_space: "bt709", "smpte170m", ...); null (the default),
    /// empty or "unknown": <see cref="YuvVideo.ColorSpace"/>. As the file path in-process (and OpenCV's
    /// sws_scale_frame), each frame is converted with its own colours, so a spliced stream whose tags change midway
    /// gives a file's results. A frame whose colours the converter doesn't take is refused
    /// (<see cref="ShotDetectionException"/>, <see cref="ShotDetectionError.InvalidInput"/>), where the file path
    /// converts it with FFmpeg.
    /// </summary>
    public string? ColorSpace { get; init; }

    /// <summary>
    /// The frame's chroma_location as ffprobe names it ("left", "topleft", ...); null (the default), empty or
    /// "unspecified": <see cref="YuvVideo.ChromaLocation"/>. Followed per frame for yuv420p10le and 8-bit frames of an odd
    /// height, as the file path in-process follows each frame's siting; unused otherwise. See <see cref="ColorSpace"/>.
    /// </summary>
    public string? ChromaLocation { get; init; }

    /// <summary>
    /// Whether the frame's samples are full range (its color_range "pc"; false: "tv"); null (the default):
    /// <see cref="YuvVideo.FullRange"/>. A yuvj420p video's frames are full range whatever this says, as a file's.
    /// See <see cref="ColorSpace"/>.
    /// </summary>
    public bool? FullRange { get; init; }

    /// <summary>The frame's presentation timestamp in <see cref="YuvVideo.TimeBase"/> (unused when that is null).</summary>
    public long Pts { get; init; }

    /// <summary>
    /// Whether the frame is flagged interlaced (FFmpeg's AV_FRAME_FLAG_INTERLACED, ffprobe's interlaced_frame). With
    /// <see cref="TopFieldFirst"/>, the field order yadif takes for the frame when deinterlacing
    /// (<see cref="DetectionOptions.Deinterlace"/>): top first unless flagged interlaced and not top field first.
    /// </summary>
    public bool Interlaced { get; init; }

    /// <summary>Whether the frame's top field comes first (FFmpeg's AV_FRAME_FLAG_TOP_FIELD_FIRST, ffprobe's top_field_first); see <see cref="Interlaced"/>.</summary>
    public bool TopFieldFirst { get; init; }
}
