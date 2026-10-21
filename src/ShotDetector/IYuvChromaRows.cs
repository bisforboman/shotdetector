namespace ShotDetector;

/// <summary>
/// Implemented by a converter (what <see cref="IYuv420Converter.ForColor"/>, <see cref="IYuv422Converter.ForColor"/> or
/// <see cref="IYuv420P10Converter.ForVideo"/> returns) that says which chroma rows converting a row reads. Decoding
/// in-process, the pipeline then takes only the rows the resize converts and the chroma rows under them from the decoder
/// (with <see cref="DetectionOptions.Yadif"/>, only those rows are deinterlaced); without it, whole planes. Converting a
/// row must read no other rows: the others hold whatever an earlier frame left there.
/// The ShotDetector.FastYuv package's converters implement it.
/// </summary>
public interface IYuvChromaRows
{
    /// <summary>
    /// The chroma rows (the same in U and V) that converting luma row <paramref name="y"/> of a <paramref name="width"/>
    /// × <paramref name="height"/> frame reads: the first, and how many from it. <paramref name="chromaHeight"/> is the
    /// chroma planes' height: half the height rounded up for 4:2:0, the height for 4:2:2.
    /// </summary>
    (int First, int Count) ChromaRows(int width, int height, int chromaHeight, int y);
}
