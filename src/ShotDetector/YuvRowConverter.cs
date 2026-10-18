namespace ShotDetector;

/// <summary>
/// One row of a planar YUV frame to BGR, at some columns (<see cref="IYuv420Converter.RowToBgr"/>, or
/// <see cref="IYuv422Converter.RowToBgr"/>): the pipelines' conversion for the video's layout.
/// </summary>
internal delegate void YuvRowConverter(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);
