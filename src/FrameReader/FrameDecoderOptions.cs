namespace FrameReader;

/// <summary>How a <see cref="FrameDecoder"/> opens and decodes its input.</summary>
public sealed record FrameDecoderOptions
{
    /// <summary>The folder with FFmpeg's libraries (<see cref="FFmpegLibraries.Load"/>); null: next to the app, then the system's.</summary>
    public string? LibraryDirectory { get; init; }

    /// <summary>Decoder threads; 0: FFmpeg's choice.</summary>
    public int Threads { get; init; }

    /// <summary>Deinterlace with yadif (its defaults, as ffmpeg's <c>-vf yadif</c>): FFmpeg's filter on whole frames (needs libavfilter), or <see cref="RowDeinterlacer"/>.</summary>
    public bool Deinterlace { get; init; }

    /// <summary>With <see cref="Deinterlace"/>: computes only the rows asked for, with yadif's arithmetic, instead of FFmpeg's filter.</summary>
    public IRowDeinterlacer? RowDeinterlacer { get; init; }

    /// <summary>FFmpeg demuxer options, as ffmpeg's input options without the dash (an image sequence's <c>framerate</c>).</summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }

    /// <summary>
    /// Decode on the GPU when possible (Windows: D3D11VA), copying each frame back: about half the CPU, but usually
    /// slower in wall time (each frame waits for the GPU and its copy: 20 s instead of 5.5 s for a 12-minute 1280x534
    /// film on the desktop tested). Worth it when CPU is the limit (many videos at once, a weak CPU). Only 8-bit 4:2:0
    /// video; the frames come back in the software decoder's own layout, so everything after is the same. Whether they
    /// equal software decoding depends on the GPU's decoder (identical on the hardware tested); off by default.
    /// Falls back to software decoding when there's no GPU decoder for the stream (<see cref="FrameDecoder.UsesHardware"/>).
    /// </summary>
    public bool HardwareDecoding { get; init; }

    /// <summary>
    /// Decode only keyframes (ffmpeg's <c>-skip_frame nokey</c>): many times faster, but approximate. The frames are
    /// the keyframes alone, so a frame "at" a time is the nearest keyframe after it, seconds off in a typical film.
    /// For contact sheets and previews, not for exact frames.
    /// </summary>
    public bool KeyframesOnly { get; init; }
}

/// <summary>
/// Deinterlaces single rows with yadif's arithmetic (FFmpeg's yadif filter, mode 0), so a <see cref="FrameDecoder"/>
/// computes only the rows that are read. Called concurrently for different rows.
/// </summary>
public interface IRowDeinterlacer
{
    /// <summary>
    /// Writes interpolated row <paramref name="y"/> of one plane to <paramref name="dst"/> (each pointer is that plane's
    /// row 0), from rows y±1 of <paramref name="cur"/>, <paramref name="prev"/> and <paramref name="next"/> and y±2 of
    /// prev and cur, mirrored at the plane's top and bottom as yadif does.
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
