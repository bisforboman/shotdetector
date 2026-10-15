namespace ShotDetector;

/// <summary>
/// The video as a detection read it (<see cref="DetectionResult.Video"/>): the source's properties, the size the detectors
/// analysed, how it was decoded, and each frame's position as scenedetect reports it. Its streams aren't here (finding
/// them is another probe): <see cref="ShotDetection.Probe"/> or FrameReader's <c>MediaProbe.Probe</c> give them.
/// </summary>
public sealed class DetectedVideo
{
    internal DetectedVideo(VideoReader reader) => Reader = reader;

    internal VideoReader Reader { get; }

    /// <summary>Width of the video in pixels, upright (after its rotation).</summary>
    public int Width => Reader.SourceWidth;

    /// <summary>Height of the video in pixels, upright.</summary>
    public int Height => Reader.SourceHeight;

    /// <summary>Width of the frames the detectors analysed: downscaled as PySceneDetect does, after any crop.</summary>
    public int ProcessedWidth => Reader.Width;

    /// <summary>Height of the frames the detectors analysed.</summary>
    public int ProcessedHeight => Reader.Height;

    /// <summary>The frame rate as OpenCV reports it to scenedetect (the average rate).</summary>
    public Fps FrameRate => Reader.Fps;

    /// <summary>The frame count OpenCV reports (CAP_PROP_FRAME_COUNT), which scenedetect takes as the video's end; null if unknown.</summary>
    public long? OpenCvFrameCount => Reader.OpenCvFrameCount > 0 ? Reader.OpenCvFrameCount : null;

    /// <summary>The container's duration; null if unknown.</summary>
    public TimeSpan? Duration => Reader.Duration;

    /// <summary>The video codec's FFmpeg name.</summary>
    public string Codec => Reader.Codec;

    /// <summary>The decoded pixel format's FFmpeg name (yuv420p, ...); empty when unknown.</summary>
    public string PixelFormat => Reader.PixelFormat;

    /// <summary>The video stream's field order (progressive, interlaced, or unknown).</summary>
    public FrameReader.FieldOrder FieldOrder => Reader.FieldOrder;

    /// <summary>The display rotation in degrees, counterclockwise (0 when none); frames are analysed upright.</summary>
    public int Rotation => Reader.Rotation;

    /// <summary>FFmpeg's name for the container (its demuxer).</summary>
    public string Container => Reader.Container;

    /// <summary>The part of the upright frame analysed, in pixels: left, top, width, height. The whole frame unless cropped.</summary>
    public (int X, int Y, int Width, int Height) CropRegion => Reader.CropRegion;

    /// <summary>
    /// <see cref="CropRegion"/> as <see cref="DetectionOptions.Crop"/> takes it: inclusive pixel coordinates of the
    /// top-left and bottom-right corners (the option's corners in that order, clamped to the frame), so it can be passed back.
    /// </summary>
    public (int X0, int Y0, int X1, int Y1) Crop => (CropRegion.X, CropRegion.Y, CropRegion.X + CropRegion.Width - 1, CropRegion.Y + CropRegion.Height - 1);

    /// <summary>Which PySceneDetect release's frame rate, downscaling and positions the detection reproduced.</summary>
    public PySceneDetectVersion Compatibility => Reader.Compatibility;

    /// <summary>Whether frames were deinterlaced (<see cref="DetectionOptions.Deinterlace"/>, Auto resolved).</summary>
    public bool Deinterlaced => Reader.Deinterlaces;

    /// <summary>Whether FFmpeg's libraries decoded it in this process, rather than the ffmpeg executable.</summary>
    public bool DecodedInProcess => Reader.DecodesInProcess;

    /// <summary>
    /// Whether it was read once, start to end, without probing all of it first: a <see cref="Stream"/>, a URL or
    /// <see cref="DetectionOptions.Streaming"/>. Frame timestamps then came as frames arrived.
    /// </summary>
    public bool Streamed => Reader.Streaming;

    /// <summary>
    /// A frame's position as PySceneDetect's OpenCV backend reports it (CAP_PROP_POS_MSEC, rounded to µs), which its
    /// scene lists and stats print: what <see cref="Stats.Csv"/> and the exports take.
    /// </summary>
    public FrameTime Position(int frame) => Reader.Position(frame);
}
