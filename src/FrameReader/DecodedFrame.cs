using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A decoded frame that stays valid after the decoder moves on (<see cref="FrameDecoder.TakeFrame"/>,
/// <see cref="VideoFrameReader.TryTake"/>): a reference to the frame's buffers, not a copy, so another thread can convert it (<see cref="BgrConverter.Convert(DecodedFrame, out int)"/>)
/// while the decoder decodes the next. Each one held keeps a decoded frame's memory until disposed.
/// Advanced: part of the low-level layer ShotDetector's exact pipelines use.
/// </summary>
public sealed unsafe class DecodedFrame : IDisposable
{
    AVFrame* _frame;

    internal DecodedFrame(AVFrame* frame, long pts, TimeSpan? time, int bitsPerRawSample = 0)
    {
        _frame = frame;
        Pts = pts;
        Time = time;
        BitsPerRawSample = bitsPerRawSample;
    }

    /// <summary>
    /// The frame's presentation timestamp, as <see cref="FrameDecoder.Pts"/> gave it (from a reader with
    /// <see cref="VideoFrameReaderOptions.FrameRate"/>, the fps filter's, in 1/FrameRate units, as <see cref="VideoFrame.Pts"/>).
    /// </summary>
    public long Pts { get; }

    /// <summary>
    /// Its time from the input's start, as ffmpeg's command line gives it (as <see cref="VideoFrame.Time"/>); null when the
    /// frame has no timestamp.
    /// </summary>
    public TimeSpan? Time { get; }

    /// <summary>The frame's width in pixels (after rotation).</summary>
    public int Width => Frame->width;

    /// <summary>The frame's height in pixels (after rotation).</summary>
    public int Height => Frame->height;

    /// <summary>The frame's FFmpeg pixel format name (yuv420p, ...).</summary>
    public string PixelFormat => ffmpeg.av_get_pix_fmt_name((AVPixelFormat)Frame->format) ?? "unknown";

    /// <summary>The decoder's picture type for this frame (FFmpeg's <c>pict_type</c>: I, P, B ...).</summary>
    public PictureType PictureType => (PictureType)Frame->pict_type;

    /// <summary>Whether the samples are full range: the frame's colour range is pc (JPEG), or its format a yuvj one.</summary>
    internal bool FullRange => Frame->color_range == AVColorRange.AVCOL_RANGE_JPEG || PixelFormat.StartsWith("yuvj", StringComparison.Ordinal);

    /// <summary>The frame's depth and chroma subsampling when it is planar YUV of 8 to 16 bits (little-endian over 8) in 4:2:0, 4:2:2 or 4:4:4; depth 0 otherwise.</summary>
    internal (int Depth, ChromaSubsampling Chroma) YuvLayout()
    {
        var d = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)Frame->format);
        bool yuv = d != null && (d->flags & ffmpeg.AV_PIX_FMT_FLAG_PLANAR) != 0 && (d->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0
            && (d->flags & ffmpeg.AV_PIX_FMT_FLAG_BE) == 0 && d->nb_components >= 3;
        if (!yuv)
            return (0, default);
        int depth = d->comp[0].depth;
        ChromaSubsampling? chroma = (d->log2_chroma_w, d->log2_chroma_h) switch
        {
            (1, 1) => ChromaSubsampling.Yuv420,
            (1, 0) => ChromaSubsampling.Yuv422,
            (0, 0) => ChromaSubsampling.Yuv444,
            _ => null,
        };
        return depth is >= 8 and <= 16 && chroma is { } c && d->comp[1].plane == 1 && d->comp[2].plane == 2 ? (depth, c) : (0, default);
    }

    /// <summary>The decoder's bits per raw sample, which ffmpeg's command line hands its encoder (capped at the encoder's format's depth).</summary>
    internal int BitsPerRawSample { get; }

    /// <summary>The frame, as FFmpeg holds it.</summary>
    internal AVFrame* Frame => _frame != null ? _frame : throw new ObjectDisposedException(nameof(DecodedFrame));

    /// <summary>One plane of the frame (0: luma or the packed pixels; 1, 2: chroma; 3: alpha), its rows <paramref name="stride"/> bytes apart.</summary>
    /// <param name="plane">The plane's index.</param>
    /// <param name="stride">Bytes from one row to the next.</param>
    public ReadOnlySpan<byte> GetPlane(int plane, out int stride) => FrameDecoder.Plane(Frame, plane, out stride);

    /// <summary>Another reference to the same buffers (no copy), disposed on its own.</summary>
    internal DecodedFrame Reference() => Reference(Pts, Time);

    /// <summary>Another reference to the frame, timed <paramref name="pts"/> and <paramref name="time"/>.</summary>
    internal DecodedFrame Reference(long pts, TimeSpan? time)
    {
        var clone = ffmpeg.av_frame_clone(Frame);
        if (clone == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "Can't reference the frame.");
        return new DecodedFrame(clone, pts, time, BitsPerRawSample);
    }

    /// <summary>Lets go of the frame's buffers.</summary>
    public void Dispose()
    {
        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
    }
}
