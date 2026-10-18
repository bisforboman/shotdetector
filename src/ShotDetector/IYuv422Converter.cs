namespace ShotDetector;

/// <summary>
/// Converts yuv422p pixels to BGR exactly as OpenCV gets them from ffmpeg (swscale), as <see cref="IYuv420Converter"/>
/// does for yuv420p. The pipeline uses it when the object given as <see cref="DetectionOptions.Yuv420Converter"/>
/// implements this interface too: then ffmpeg sends raw yuv422p (and yuvj422p) and only the pixels the resize reads are
/// converted. Without it, such video takes the normal path; results are identical either way.
/// The ShotDetector.FastYuv package (LGPL) provides one: SwscaleYuv420 implements both.
/// </summary>
public interface IYuv422Converter
{
    /// <summary>
    /// A converter for 4:2:2 video with this colour metadata, or null if it isn't supported (the normal path is used
    /// then). <paramref name="colorSpace"/> is ffprobe's color_space ("bt709", "smpte170m", "unknown", ...); full range
    /// means yuvj422p or color_range "pc".
    /// </summary>
    IYuv422Converter? ForColor(string colorSpace, bool fullRange);

    /// <summary>
    /// Converts the pixels at <paramref name="cols"/> of row <paramref name="y"/> of a packed yuv422p frame into
    /// <paramref name="bgrRow"/> (a packed BGR row); other pixels are left untouched. Called concurrently for different
    /// rows.
    /// </summary>
    void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);

    /// <summary>Size in bytes of a tightly packed yuv422p frame (ffmpeg rawvideo).</summary>
    static int FrameSize(int width, int height) => width * height + 2 * ((width + 1) / 2) * height;
}
