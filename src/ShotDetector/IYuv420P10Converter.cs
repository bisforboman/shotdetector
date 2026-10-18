namespace ShotDetector;

/// <summary>
/// Converts yuv420p10le pixels (10-bit 4:2:0) to BGR exactly as OpenCV gets them from ffmpeg (swscale) on x86, as
/// <see cref="IYuv420Converter"/> does for yuv420p. The pipeline uses it when the object given as
/// <see cref="DetectionOptions.Yuv420Converter"/> implements this interface too: then ffmpeg sends raw yuv420p10le and
/// only the pixels the resize reads are converted. Without it, such video takes the normal path; results are identical
/// either way on x86, and x86's on other CPUs.
/// The ShotDetector.FastYuv package (LGPL) provides one: SwscaleYuv420 implements it.
/// </summary>
public interface IYuv420P10Converter
{
    /// <summary>
    /// A converter for 10-bit 4:2:0 video of this size and colour metadata, or null if it isn't supported (the normal
    /// path is used then). <paramref name="colorSpace"/> is ffprobe's color_space ("bt709", "smpte170m", "unknown",
    /// ...); full range means color_range "pc"; <paramref name="chromaLocation"/> is ffprobe's chroma_location ("left",
    /// "center", "topleft", ...; null when unspecified, which swscale takes as center). Unlike 8-bit, swscale
    /// interpolates the chroma here, so where the samples sit and the frame's size matter.
    /// </summary>
    IYuv420P10Converter? ForVideo(string colorSpace, bool fullRange, string? chromaLocation, int width, int height);

    /// <summary>
    /// Converts the pixels at <paramref name="cols"/> of row <paramref name="y"/> of a packed yuv420p10le frame (16-bit
    /// little-endian samples, Y then U then V) into <paramref name="bgrRow"/> (a packed BGR row); other pixels are left
    /// untouched. Called concurrently for different rows.
    /// </summary>
    void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);

    /// <summary>Size in bytes of a tightly packed yuv420p10le frame (ffmpeg rawvideo).</summary>
    static int FrameSize(int width, int height) => 2 * IYuv420Converter.FrameSize(width, height);
}
