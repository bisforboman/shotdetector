namespace ShotDetector;

/// <summary>
/// One row of a planar YUV frame to BGR, at some columns (<see cref="IYuv420Converter.RowToBgr"/>, or
/// <see cref="IYuv422Converter.RowToBgr"/>): the pipelines' conversion for the video's layout.
/// </summary>
internal delegate void YuvRowConverter(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow);

/// <summary>
/// The pipelines' conversion for the video's layout: a row at a time (<see cref="Row"/>), and, when the converter does
/// that too, a frame's rows at once (<see cref="Frame"/>, preferred: the same bytes); and, when the converter says so
/// (<see cref="ChromaRows"/>), which chroma rows each row reads, so only the rows read are taken from the decoder.
/// </summary>
internal sealed record YuvConversion(YuvRowConverter Row, IYuv420P10FrameConverter? Frame = null, IYuvChromaRows? ChromaRows = null);
