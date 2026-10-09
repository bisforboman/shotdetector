using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>A pixel format <see cref="VideoFrameReader"/> converts frames to.</summary>
public enum FrameFormat
{
    /// <summary>Packed 8-bit blue, green, red (3 bytes a pixel), as OpenCV holds images.</summary>
    Bgr24,

    /// <summary>Packed 8-bit red, green, blue (3 bytes a pixel).</summary>
    Rgb24,

    /// <summary>Packed 8-bit blue, green, red, alpha (4 bytes a pixel; alpha opaque).</summary>
    Bgra32,

    /// <summary>Packed 8-bit red, green, blue, alpha (4 bytes a pixel; alpha opaque).</summary>
    Rgba32,

    /// <summary>8-bit luma only (1 byte a pixel), in the source's range.</summary>
    Gray8,

    /// <summary>8-bit planar YUV 4:2:0: the Y plane, then U, then V (each half the size, rounded up), in the source's colour space and range.</summary>
    Yuv420p,
}

/// <summary>What a <see cref="VideoFrameReader"/> gives and how it decodes.</summary>
public sealed record FrameReaderOptions
{
    /// <summary>Output width in pixels; null (with <see cref="Height"/> null): the frame's own (after any rotation).</summary>
    public int? Width { get; init; }

    /// <summary>Output height in pixels; null (with <see cref="Width"/> null): the frame's own.</summary>
    public int? Height { get; init; }

    /// <summary>The output's pixel format.</summary>
    public FrameFormat Format { get; init; } = FrameFormat.Bgr24;

    /// <summary>How to decode (library folder, threads, deinterlacing, input options); null: the defaults.</summary>
    public FrameDecoderOptions? Decoder { get; init; }
}

/// <summary>A decoded, converted frame: valid until the next read from its <see cref="VideoFrameReader"/>.</summary>
public readonly ref struct VideoFrame
{
    internal VideoFrame(int index, long pts, TimeSpan time, int width, int height, FrameFormat format, ReadOnlySpan<byte> data)
    {
        (Index, Pts, Time, Width, Height, Format) = (index, pts, time, width, height, format);
        Data = data;
    }

    /// <summary>The frame's number in decoding output order, from 0.</summary>
    public int Index { get; }

    /// <summary>Its best-effort timestamp in the stream's time base (<see cref="VideoFrameReader.TimeBase"/>), as decoded.</summary>
    public long Pts { get; }

    /// <summary>Its time from the input's start, as ffmpeg's command line gives it (shifted by the container's start time).</summary>
    public TimeSpan Time { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel format of <see cref="Data"/>.</summary>
    public FrameFormat Format { get; }

    /// <summary>
    /// The pixels, packed without padding as ffmpeg's rawvideo output holds them (rows of Width × bytes per pixel; for
    /// <see cref="FrameFormat.Yuv420p"/> the three planes one after another). Copy it to keep it past the next read.
    /// </summary>
    public ReadOnlySpan<byte> Data { get; }
}

/// <summary>
/// Reads a video's frames at a chosen size and pixel format, in this process through FFmpeg's libraries. The pixels
/// are the bytes ffmpeg's command line gives for <c>-vf scale=W:H:flags=bicubic,format=F -f rawvideo</c> (swscale with
/// the frame's colour tags), so results can be checked against it. No allocations per frame: one output buffer, reused.
/// </summary>
public sealed unsafe class VideoFrameReader : IDisposable
{
    readonly FrameDecoder _decoder;
    readonly FrameReaderOptions _options;
    readonly AVPixelFormat _format;
    SwsContext* _sws;
    AVFrame* _in, _out;
    byte[] _packed = [];
    int _index = -1;
    long? _keepFrom; // after a seek: frames whose pts plus this is below 0 are dropped (ffmpeg's trim)

    /// <summary>Opens a file, image sequence pattern or URL FFmpeg can read.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or the input can't be opened or has no video.</exception>
    public VideoFrameReader(string path, FrameReaderOptions? options = null) : this(options, o => new FrameDecoder(path, o)) { }

    /// <summary>Opens a stream's bytes, read once (headers first: mkv, webm, ts, mov or a faststart mp4).</summary>
    /// <param name="stream">The media's bytes; not disposed.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    public VideoFrameReader(Stream stream, FrameReaderOptions? options = null) : this(options, o => new FrameDecoder(stream, o)) { }

    VideoFrameReader(FrameReaderOptions? options, Func<FrameDecoderOptions?, FrameDecoder> open)
    {
        _options = options ?? new FrameReaderOptions();
        if (_options.Width is null != _options.Height is null || _options.Width <= 0 || _options.Height <= 0)
            throw new ArgumentException("Width and Height are both set (positive) or both left out.", nameof(options));
        _format = _options.Format switch
        {
            FrameFormat.Bgr24 => AVPixelFormat.AV_PIX_FMT_BGR24,
            FrameFormat.Rgb24 => AVPixelFormat.AV_PIX_FMT_RGB24,
            FrameFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
            FrameFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
            FrameFormat.Gray8 => AVPixelFormat.AV_PIX_FMT_GRAY8,
            FrameFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
            _ => throw new ArgumentException($"Unknown format {_options.Format}.", nameof(options)),
        };
        _decoder = open(_options.Decoder);
    }

    /// <summary>The stream's time base in seconds per <see cref="VideoFrame.Pts"/> unit.</summary>
    public double TimeBase => _decoder.TimeBase;

    /// <summary>
    /// Seeks so the next <see cref="TryRead"/> gives the frame ffmpeg's <c>-ss</c> gives for <paramref name="time"/>:
    /// the first frame at or after it (from the keyframe before it, decoding forward and dropping the frames before,
    /// as ffmpeg's accurate seek does). <see cref="VideoFrame.Index"/> counts again from 0. With deinterlacing, only
    /// before the first read.
    /// </summary>
    /// <param name="time">From the input's start (as <see cref="VideoFrame.Time"/>).</param>
    public void Seek(TimeSpan time)
    {
        _keepFrom = _decoder.SeekLikeFfmpeg(time);
        _index = -1;
        _current = false;
    }

    /// <summary>The frame at <paramref name="time"/> (<see cref="Seek"/>, then <see cref="TryRead"/>): a thumbnail.</summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <returns>False when there's no frame at or after the time.</returns>
    public bool TryReadAt(TimeSpan time, out VideoFrame frame)
    {
        Seek(time);
        return TryRead(out frame);
    }

    /// <summary>Decodes and converts the next frame; false at the end.</summary>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding or converting failed.</exception>
    public bool TryRead(out VideoFrame frame)
    {
        bool got;
        while ((got = _decoder.Next()) && _keepFrom is { } keep && _decoder.Pts + keep < 0)
        {
        }
        _current = got;
        if (!got)
        {
            frame = default;
            return false;
        }
        _index++;
        Convert(out frame);
        return true;
    }

    /// <summary>
    /// Reads forward to the first frame at or after <paramref name="time"/> (the frame <see cref="TryReadAt"/> and
    /// ffmpeg's <c>-ss</c> give), without seeking and without converting the frames on the way: a frame every second of a
    /// film is one pass that decodes everything and converts only what it gives. When the frame last read is already at
    /// or after the time, that frame again. Times behind it don't go back (use <see cref="Seek"/>).
    /// </summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <returns>False when the video ends first.</returns>
    public bool TryReadForwardTo(TimeSpan time, out VideoFrame frame)
    {
        long keep = _decoder.TrimOffset(time);
        if (!(_current && _decoder.Pts + keep >= 0))
        {
            bool got;
            while ((got = _decoder.Next()) && (_decoder.Pts + keep < 0 || _keepFrom is { } k && _decoder.Pts + k < 0))
                _index++; // passed by, not converted
            _current = got;
            if (!got)
            {
                frame = default;
                return false;
            }
            _index++;
        }
        Convert(out frame);
        return true;
    }

    bool _current; // the decoder holds a frame read (not past the end)

    void Convert(out VideoFrame frame)
    {
        var src = _decoder.Frame;
        _decoder.EnsureRows(0, src->height);
        int width = _options.Width ?? src->width, height = _options.Height ?? src->height;
        if (_out == null)
        {
            _out = ffmpeg.av_frame_alloc();
            _in = ffmpeg.av_frame_alloc();
            _sws = ffmpeg.sws_alloc_context();
            Check(ffmpeg.av_opt_set(_sws, "sws_flags", "bicubic", 0), "scaler");
        }
        if (_out->width != width || _out->height != height)
        {
            ffmpeg.av_frame_unref(_out);
            (_out->format, _out->width, _out->height) = ((int)_format, width, height);
            Check(ffmpeg.av_frame_get_buffer(_out, 1), "frame buffer"); // rows packed, planes one after another
        }
        // As the scale filter does: the input read as progressive (its interl=0), and the output's tags those the link
        // negotiates (YUV and gray keep the input's matrix and range; RGB is RGB).
        // Its in_chroma_loc (default unspecified) replaces the frame's chroma location, so chroma isn't re-sited.
        Check(ffmpeg.av_frame_ref(_in, src), "frame");
        _in->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        _in->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        (_out->chroma_location, _out->color_primaries, _out->color_trc) = (_in->chroma_location, _in->color_primaries, _in->color_trc);
        bool rgb = _options.Format is not (FrameFormat.Gray8 or FrameFormat.Yuv420p);
        _out->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : src->colorspace;
        _out->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : src->color_range;
        int ret = ffmpeg.sws_scale_frame(_sws, _out, _in);
        ffmpeg.av_frame_unref(_in);
        Check(ret, "convert");
        long pts = _decoder.Pts;
        int size = ffmpeg.av_image_get_buffer_size(_format, width, height, 1);
        ReadOnlySpan<byte> data;
        if (_options.Format != FrameFormat.Yuv420p)
            data = new ReadOnlySpan<byte>(_out->data[0], size); // one plane, rows packed (align 1)
        else
        {
            // The planes packed one after another, as rawvideo writes them (the frame's buffer may pad between them).
            if (_packed.Length != size)
                _packed = new byte[size];
            var planes = new byte_ptrArray4();
            var strides = new int_array4();
            for (uint i = 0; i < 4; i++)
            {
                planes[i] = _out->data[i];
                strides[i] = _out->linesize[i];
            }
            fixed (byte* p = _packed)
                Check(ffmpeg.av_image_copy_to_buffer(p, size, in planes, in strides, _format, width, height, 1), "pack");
            data = _packed;
        }
        frame = new VideoFrame(_index, pts, TimeSpan.FromSeconds((pts + _decoder.StartOffset) * _decoder.TimeBase), width, height,
            _options.Format, data);
    }

    /// <summary>Frees the decoder, the scaler and the output buffer.</summary>
    public void Dispose()
    {
        if (_sws != null) { var s = _sws; ffmpeg.sws_free_context(&s); _sws = null; }
        if (_in != null) { var f = _in; ffmpeg.av_frame_free(&f); _in = null; }
        if (_out != null) { var f = _out; ffmpeg.av_frame_free(&f); _out = null; }
        _decoder.Dispose();
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
