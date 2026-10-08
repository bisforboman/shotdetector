namespace ShotDetector;

/// <summary>
/// Deinterlaces single rows with yadif's arithmetic (FFmpeg's yadif filter, its default mode), so in-process
/// deinterlacing (<see cref="DeinterlaceMode"/>) computes only the rows the resize reads instead of whole frames.
/// When one is given (<see cref="DetectionOptions.Yadif"/>), it is used in-process; without one, FFmpeg's yadif
/// deinterlaces whole frames. 8-bit results are identical either way. For 9 to 16 bits, FFmpeg 8.1's x86 SIMD yadif
/// gives a different result on every run (its C code doesn't); this follows the C code, so it is deterministic and equal
/// to FFmpeg's yadif with SIMD off (-cpuflags 0). The ShotDetector.FastYuv package (LGPL) provides one: <c>Yadif</c>.
/// </summary>
public interface IYadif
{
    /// <summary>
    /// Writes interpolated row <paramref name="y"/> of one plane to <paramref name="dst"/> (each pointer is that plane's
    /// row 0): from rows y±1 of <paramref name="cur"/>, <paramref name="prev"/> and <paramref name="next"/>, and y±2 of
    /// <paramref name="prev"/> and <paramref name="cur"/> (yadif's mode 0, where the temporal pair is prev and cur),
    /// mirrored at the plane's top and bottom as yadif does. Called concurrently for different rows.
    /// </summary>
    /// <param name="dst">The output plane.</param>
    /// <param name="prev">The previous frame's plane (the current one's for the first frame).</param>
    /// <param name="cur">The current frame's plane.</param>
    /// <param name="next">The next frame's plane (the current one's for the last frame).</param>
    /// <param name="srcStride">Bytes per row of prev, cur and next.</param>
    /// <param name="dstStride">Bytes per row of dst.</param>
    /// <param name="width">Samples per row.</param>
    /// <param name="height">Rows in the plane.</param>
    /// <param name="y">The row to write.</param>
    /// <param name="bytesPerSample">1 for 8-bit planes, 2 for 9- to 16-bit ones.</param>
    void FilterRow(nint dst, nint prev, nint cur, nint next, int srcStride, int dstStride, int width, int height, int y, int bytesPerSample);
}
