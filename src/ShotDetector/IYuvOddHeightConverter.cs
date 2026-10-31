namespace ShotDetector;

/// <summary>
/// Converts 8-bit 4:2:0 and 4:2:2 video of an odd height (yuv420p, yuvj420p, yuv422p, yuvj422p) to BGR exactly as
/// OpenCV gets it from ffmpeg (swscale) on x86, as <see cref="IYuv420Converter"/> and <see cref="IYuv422Converter"/> do
/// for even heights. swscale's unscaled converter needs an even height, so at an odd one it scales instead, as for
/// 10-bit (<see cref="IYuv420P10Converter"/>): it interpolates the chroma from where it sits, so the chroma location and
/// the frame's size matter. The pipeline uses it when the object given as <see cref="DetectionOptions.Yuv420Converter"/>
/// implements this interface too; without it, such video takes the normal path (ffmpeg's conversion), and frames from
/// the caller of an odd height are refused. Results are identical either way on x86, and x86's on other CPUs.
/// The ShotDetector.FastYuv package (LGPL) provides one: SwscaleYuv420 implements it.
/// </summary>
public interface IYuvOddHeightConverter
{
    /// <summary>
    /// A converter for frames of this layout (<paramref name="pixelFormat"/>, FFmpeg's name), odd size and colour
    /// metadata, or null if it isn't supported (an even height, an odd width, another format, colours it doesn't
    /// reproduce). <paramref name="colorSpace"/> is ffprobe's color_space; full range means a yuvj format or color_range
    /// "pc"; <paramref name="chromaLocation"/> is ffprobe's chroma_location (null when unspecified: center). The
    /// converter implements <see cref="IYuvChromaRows"/> too.
    /// </summary>
    IYuvFrameConverter? ForOddHeight(string pixelFormat, string colorSpace, bool fullRange, string? chromaLocation, int width, int height);
}

/// <summary>
/// One video's conversion to BGR (<see cref="IYuvOddHeightConverter.ForOddHeight"/>): of a row, or of all the pixels a
/// frame's resize reads in one call (preferred: the work rows share, each chroma row filtered horizontally, is done once
/// per frame). Both give the same bytes.
/// </summary>
public interface IYuvFrameConverter
{
    /// <summary>
    /// Converts the pixels at <paramref name="cols"/> of row <paramref name="y"/> of a packed frame (ffmpeg rawvideo: Y,
    /// then U, then V, 8-bit) into <paramref name="bgrRow"/> (a packed BGR row); other pixels are left untouched. Called
    /// concurrently for different rows.
    /// </summary>
    void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);

    /// <summary>
    /// Converts the pixels at <paramref name="rows"/> × <paramref name="cols"/> (each ascending, no repeats) of a packed
    /// frame (as <see cref="RowToBgr"/> reads it) into <paramref name="bgr"/>, packed: row i's column j at
    /// <c>(i * cols.Length + j) * 3</c>. Called concurrently for different rows.
    /// </summary>
    void FrameToBgr(ReadOnlySpan<byte> yuv, int width, int height, ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, Span<byte> bgr);
}
