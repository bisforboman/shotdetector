namespace FrameReader;

/// <summary>How a <see cref="PictureFormat"/>'s chroma planes are subsampled.</summary>
public enum ChromaSubsampling
{
    /// <summary>4:2:0: chroma half the width and half the height (rounded up), as yuv420p, yuv420p10le and most delivery codecs.</summary>
    Yuv420,

    /// <summary>4:2:2: chroma half the width, the full height, as ProRes 422 and DNxHR's 10-bit profiles decode to.</summary>
    Yuv422,

    /// <summary>4:4:4: chroma at the full size.</summary>
    Yuv444,
}

/// <summary>
/// The decoder's picture type, as FFmpeg's <c>pict_type</c> gives it (ffprobe's <c>pict_type</c>), in FFmpeg's order:
/// a cast from <c>AVPictureType</c> keeps its value.
/// </summary>
public enum PictureType
{
    /// <summary>Not known (AV_PICTURE_TYPE_NONE).</summary>
    None,

    /// <summary>Intra.</summary>
    I,

    /// <summary>Predicted.</summary>
    P,

    /// <summary>Bi-directionally predicted.</summary>
    B,

    /// <summary>S(GMC)-VOP, MPEG-4.</summary>
    S,

    /// <summary>Switching intra.</summary>
    SI,

    /// <summary>Switching predicted.</summary>
    SP,

    /// <summary>BI type.</summary>
    BI,
}

/// <summary>
/// What a <see cref="PictureMeter"/> needs to know about the frames it is given: planar YUV at 8 to 16 bits, 4:2:0,
/// 4:2:2 or 4:4:4. Samples over 8 bits are 16-bit little-endian, as FFmpeg's yuv420p10le, yuv422p10le and the like hold
/// them (<see cref="DecodedFrame.GetPlane"/>).
/// </summary>
/// <param name="Width">The decoded frame's width in pixels.</param>
/// <param name="Height">The decoded frame's height in pixels.</param>
/// <param name="FullRange">Whether the samples are full range (0 to 2^depth − 1, yuvj420p, color_range pc), not limited (16 to 235 luma at 8 bits).</param>
/// <param name="TimeBase">The unit of the frames' pts, in seconds.</param>
/// <param name="Rotation">
/// The display rotation in degrees, counterclockwise (the display matrix's angle, ffprobe's rotation,
/// <see cref="VideoStreamInfo.Rotation"/> rounded; a quarter turn, 90 and -270 alike). The planes are given as decoded;
/// cropdetect's lines and the colour bars are read in the upright picture, as ffmpeg's autorotate turns it, and
/// <see cref="PictureMeasures"/>' size is the upright one.
/// </param>
/// <param name="BitDepth">Bits per sample, 8 to 16 (10 for yuv422p10le).</param>
/// <param name="Chroma">The chroma planes' subsampling.</param>
public sealed record PictureFormat(int Width,
    int Height,
    bool FullRange,
    Rational TimeBase,
    int Rotation = 0,
    int BitDepth = 8,
    ChromaSubsampling Chroma = ChromaSubsampling.Yuv420)
{
    /// <summary>
    /// The format of a decoded frame: its size, depth and chroma from its pixel format (planar YUV, yuvj too), full range
    /// from its colour range or a yuvj format.
    /// </summary>
    /// <param name="frame">A frame from <see cref="VideoFrameReader.TryTake"/> or <see cref="FrameDecoder.TakeFrame"/>.</param>
    /// <param name="timeBase">The unit of its pts (<see cref="VideoFrameReader.TimeBase"/>).</param>
    /// <param name="rotation">The stream's display rotation, when the frames are taken as coded (no autorotate).</param>
    /// <returns>The format; null when the frame isn't planar YUV of 8 to 16 bits in 4:2:0, 4:2:2 or 4:4:4.</returns>
    public static PictureFormat? Of(DecodedFrame frame, Rational timeBase, int rotation = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (depth, chroma) = frame.YuvLayout();
        return depth == 0 ? null : new PictureFormat(frame.Width, frame.Height, frame.FullRange, timeBase, rotation, depth, chroma);
    }

    /// <summary>The chroma planes' width: the width halved and rounded up, except in 4:4:4.</summary>
    public int ChromaWidth => Chroma == ChromaSubsampling.Yuv444 ? Width : (Width + 1) / 2;

    /// <summary>The chroma planes' height: the height halved and rounded up in 4:2:0.</summary>
    public int ChromaHeight => Chroma == ChromaSubsampling.Yuv420 ? (Height + 1) / 2 : Height;

    /// <summary>Bytes per sample: 1 at 8 bits, else 2.</summary>
    public int BytesPerSample => BitDepth > 8 ? 2 : 1;
}
