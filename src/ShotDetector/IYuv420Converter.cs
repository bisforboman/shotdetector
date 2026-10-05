namespace ShotDetector;

/// <summary>
/// Converts yuv420p pixels to BGR exactly as OpenCV gets them from ffmpeg (swscale). When one is
/// given (<see cref="DetectionOptions.Yuv420Converter"/>), ffmpeg sends raw yuv420p and only the
/// pixels the resize reads are converted, which is much faster on large video. Without one,
/// ffmpeg converts whole frames; results are identical either way.
/// The ShotDetector.FastYuv package (LGPL) provides one: SwscaleYuv420.
/// </summary>
public interface IYuv420Converter
{
    /// <summary>
    /// A converter for video with this colour metadata, or null if it isn't supported (the normal
    /// path is used then). <paramref name="colorSpace"/> is ffprobe's color_space ("bt709",
    /// "smpte170m", "unknown", ...); full range means yuvj420p or color_range "pc".
    /// </summary>
    IYuv420Converter? ForColor(string colorSpace, bool fullRange);

    /// <summary>
    /// Converts the pixels at <paramref name="cols"/> of row <paramref name="y"/> of a packed yuv420p
    /// frame into <paramref name="bgrRow"/> (a packed BGR row); other pixels are left untouched.
    /// Called concurrently for different rows.
    /// </summary>
    void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);

    /// <summary>Size in bytes of a tightly packed yuv420p frame (ffmpeg rawvideo).</summary>
    static int FrameSize(int width, int height) => width * height + 2 * ((width + 1) / 2) * ((height + 1) / 2);
}
