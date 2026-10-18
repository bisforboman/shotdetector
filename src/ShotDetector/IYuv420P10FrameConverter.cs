namespace ShotDetector;

/// <summary>
/// An <see cref="IYuv420P10Converter"/> that converts all of a frame's pixels the resize reads in one call, so the work
/// rows share (swscale filters each chroma row horizontally once, for every output row that reads it) is done once per
/// frame instead of once per row. The pipeline uses it when the converter <see cref="IYuv420P10Converter.ForVideo"/>
/// returns implements it too; the result must be the same bytes <see cref="IYuv420P10Converter.RowToBgr"/> gives.
/// ShotDetector.FastYuv's converters (SwscaleYuv420's ForVideo) implement it.
/// </summary>
public interface IYuv420P10FrameConverter : IYuv420P10Converter
{
    /// <summary>
    /// Converts the pixels at <paramref name="rows"/> × <paramref name="cols"/> (each ascending, no repeats) of a packed
    /// yuv420p10le frame (as <see cref="IYuv420P10Converter.RowToBgr"/> reads it) into <paramref name="bgr"/>, packed:
    /// row i's column j at <c>(i * cols.Length + j) * 3</c>, rows.Length × cols.Length × 3 bytes. Called concurrently for
    /// different rows.
    /// </summary>
    void FrameToBgr(ReadOnlySpan<byte> yuv, int width, int height, ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, Span<byte> bgr);
}
