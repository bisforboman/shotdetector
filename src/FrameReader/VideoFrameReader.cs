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
public sealed record VideoFrameReaderOptions
{
    /// <summary>Output width in pixels; null (with <see cref="Height"/> null): the frame's own (after any rotation).</summary>
    public int? Width { get; init; }

    /// <summary>Output height in pixels; null (with <see cref="Width"/> null): the frame's own.</summary>
    public int? Height { get; init; }

    /// <summary>The output's pixel format.</summary>
    public FrameFormat FrameFormat { get; init; } = FrameFormat.Bgr24;

    /// <summary>
    /// Frames per second out, through FFmpeg's <c>fps</c> filter (its defaults: nearest-frame rounding, duplicating or
    /// dropping as needed): the frames <c>-vf fps=R,scale=...</c> gives, only those converted. 1 for a picture every
    /// second, 1/10 for one every ten seconds. Null: every frame as decoded. Needs libavfilter with fps.
    /// </summary>
    public Rational? FrameRate { get; init; }

    /// <summary>How to decode (library folder, threads, deinterlacing, input options); null: the defaults.</summary>
    public FrameDecoderOptions? Decoder { get; init; }
}

/// <summary>A decoded, converted frame: valid until the next read from its <see cref="VideoFrameReader"/>.</summary>
public readonly ref struct VideoFrame
{
    internal VideoFrame(int index, long pts, TimeSpan time, int width, int height, FrameFormat format, ReadOnlySpan<byte> data)
    {
        (Index, Pts, Time, Width, Height, FrameFormat) = (index, pts, time, width, height, format);
        Data = data;
    }

    /// <summary>
    /// How many distinct frames the reader had given before this one, since it opened or last seeked: 0, 1, 2, ...
    /// (the same frame given again keeps its number). Frames passed over aren't counted; <see cref="Time"/> says where it is.
    /// </summary>
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
    public FrameFormat FrameFormat { get; }

    /// <summary>
    /// The pixels, packed without padding as ffmpeg's rawvideo output holds them (rows of Width × bytes per pixel; for
    /// <see cref="FrameFormat.Yuv420p"/> the three planes one after another). Copy it to keep it past the next read.
    /// </summary>
    public ReadOnlySpan<byte> Data { get; }
}

/// <summary>Receives a frame read for the time at <paramref name="index"/> in the list given (<see cref="VideoFrameReader.ReadAt"/>).</summary>
/// <param name="index">The time's position in the list.</param>
/// <param name="frame">The frame, valid only during the call.</param>
public delegate void FrameHandler(int index, VideoFrame frame);

/// <summary>
/// Reads a video's frames at a chosen size and pixel format, in this process through FFmpeg's libraries. The pixels
/// are the bytes ffmpeg's command line gives for <c>-vf scale=W:H:flags=bicubic,format=F -f rawvideo</c> (swscale with
/// the frame's colour tags), so results can be checked against it. No allocations per frame: one output buffer, reused.
/// </summary>
public sealed unsafe class VideoFrameReader : IDisposable
{
    readonly FrameDecoder _decoder;
    readonly VideoFrameReaderOptions _options;
    readonly AVPixelFormat _format;
    SwsContext* _sws;
    AVFrame* _in, _out;
    byte[] _packed = [];
    int _index = -1;
    long? _keepFrom; // after a seek: frames whose pts plus this is below 0 are dropped (ffmpeg's trim)
    // FrameRate: buffer -> fps -> buffersink, fed the decoded frames, its output converted.
    AVFilterGraph* _fpsGraph;
    AVFilterContext* _fpsIn, _fpsOut;
    AVFrame* _fpsFrame;
    bool _fpsFlushed;

    /// <summary>Opens a file, image sequence pattern or URL FFmpeg can read.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or the input can't be opened or has no video.</exception>
    public VideoFrameReader(string path, VideoFrameReaderOptions? options = null) : this(options, o => new FrameDecoder(path, o)) { }

    /// <summary>Opens a stream's bytes, read once (headers first: mkv, webm, ts, mov or a faststart mp4).</summary>
    /// <param name="stream">The media's bytes; not disposed.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    public VideoFrameReader(Stream stream, VideoFrameReaderOptions? options = null) : this(options, o => new FrameDecoder(stream, o)) { }

    VideoFrameReader(VideoFrameReaderOptions? options, Func<FrameDecoderOptions?, FrameDecoder> open)
    {
        _options = options ?? new VideoFrameReaderOptions();
        if (_options.Width is null != _options.Height is null || _options.Width <= 0 || _options.Height <= 0)
            throw new ArgumentException("Width and Height are both set (positive) or both left out.", nameof(options));
        _format = _options.FrameFormat switch
        {
            FrameFormat.Bgr24 => AVPixelFormat.AV_PIX_FMT_BGR24,
            FrameFormat.Rgb24 => AVPixelFormat.AV_PIX_FMT_RGB24,
            FrameFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
            FrameFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
            FrameFormat.Gray8 => AVPixelFormat.AV_PIX_FMT_GRAY8,
            FrameFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
            _ => throw new ArgumentException($"Unknown format {_options.FrameFormat}.", nameof(options)),
        };
        if (_options.FrameRate is { } r && (r.Num <= 0 || r.Den <= 0))
            throw new ArgumentException("FrameRate is positive.", nameof(options));
        _decoder = open(_options.Decoder);
    }

    /// <summary>The stream's time base in seconds per <see cref="VideoFrame.Pts"/> unit.</summary>
    public Rational TimeBase => _decoder.TimeBase;

    /// <summary>
    /// Seeks so the next <see cref="TryRead"/> gives the frame ffmpeg's <c>-ss</c> gives for <paramref name="time"/>:
    /// the first frame at or after it (from the keyframe before it, decoding forward and dropping the frames before,
    /// as ffmpeg's accurate seek does). <see cref="VideoFrame.Index"/> counts again from 0. With deinterlacing, only
    /// before the first read. In open GOPs the B-frames just after the keyframe it lands on decode without their reference,
    /// as with ffmpeg; <see cref="TryReadForwardTo"/> gives the true frames.
    /// </summary>
    /// <param name="time">From the input's start (as <see cref="VideoFrame.Time"/>).</param>
    public void Seek(TimeSpan time)
    {
        if (_options.FrameRate is not null)
            throw new InvalidOperationException("Seek isn't available with FrameRate.");
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

    /// <summary>
    /// The frames at <paramref name="times"/> (ascending), as <see cref="TryReadForwardTo"/> gives them, read by
    /// <paramref name="parallelism"/> readers at once, each taking a contiguous share of the times (and of the decoder
    /// threads): the same frames in less wall time when there are spare cores. <paramref name="handler"/> is called from
    /// several threads at once; in time order within each share.
    /// </summary>
    /// <param name="path">A file (a Stream can't be read by several readers).</param>
    /// <param name="times">From the input's start, ascending.</param>
    /// <param name="options">Size, format and decoding; not <see cref="VideoFrameReaderOptions.FrameRate"/>.</param>
    /// <param name="parallelism">Readers at once; null: half the cores, at most 4.</param>
    /// <param name="handler">Called for each frame found, with its time's index.</param>
    /// <param name="cancellationToken">Stops the readers between frames (<see cref="OperationCanceledException"/>).</param>
    /// <returns>How many times had a frame (the rest are past the end).</returns>
    public static int ReadAt(string path, IReadOnlyList<TimeSpan> times, VideoFrameReaderOptions? options, int? parallelism, FrameHandler handler,
        CancellationToken cancellationToken = default)
    {
        options ??= new VideoFrameReaderOptions();
        if (options.FrameRate is not null)
            throw new ArgumentException("ReadAt reads frames at times; FrameRate isn't available.", nameof(options));
        for (int i = 1; i < times.Count; i++)
            if (times[i] < times[i - 1])
                throw new ArgumentException("The times are in ascending order.", nameof(times));
        int cores = Environment.ProcessorCount;
        int workers = Math.Clamp(parallelism ?? Math.Min(4, cores / 2), 1, Math.Max(1, times.Count));
        var decoder = options.Decoder ?? new FrameDecoderOptions();
        if (workers > 1 && decoder.Threads == 0)
            options = options with { Decoder = decoder with { Threads = Math.Max(1, cores / workers) } };
        int found = 0;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, w =>
        {
            int from = w * times.Count / workers, to = (w + 1) * times.Count / workers;
            using var reader = new VideoFrameReader(path, options);
            for (int i = from; i < to; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.TryReadForwardTo(times[i], out var frame))
                    break;
                handler(i, frame);
                Interlocked.Increment(ref found);
            }
        });
        return found;
    }

    /// <summary>Decodes and converts the next frame; false at the end.</summary>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding or converting failed.</exception>
    public bool TryRead(out VideoFrame frame)
    {
        if (_options.FrameRate is { } rate)
            return TryReadFps(rate, out frame);
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
    /// Reads forward to the first frame at or after <paramref name="time"/> (the frame a full decode has there, as
    /// ffmpeg's output-side <c>-ss</c> gives it), converting only that frame. When the time is past the next keyframe it
    /// jumps there instead of decoding the frames in between (from the keyframe before, so open GOPs decode right): a
    /// picture every second is one pass, a picture every few seconds or more decodes a fraction of the video. When the
    /// frame last read is already at or after the time, that frame again. Times behind it don't go back (use
    /// <see cref="Seek"/>).
    /// </summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <returns>False when the video ends first.</returns>
    public bool TryReadForwardTo(TimeSpan time, out VideoFrame frame)
    {
        if (_options.FrameRate is not null)
            throw new InvalidOperationException("TryReadForwardTo isn't available with FrameRate.");
        long keep = _decoder.TrimOffset(time);
        if (!(_current && _decoder.Pts + keep >= 0))
        {
            // Far ahead (the target's keyframe, per the index, at least a second past where decoding is): jump there. Frames that come out
            // before the first keyframe after the jump may be open-GOP B-frames decoded without their reference: if
            // one of them is the frame wanted, jump again from the keyframe before.
            long target = -keep;
            bool jumped = false;
            long key = 0;
            if (_decoder.CanSeek && _decoder.KeyframeAtOrBefore(target) is { } found
                && found > (_current ? _decoder.Pts : 0) + (long)(1 / _decoder.TimeBase.Value))
            {
                key = found;
                // To the target, not the index entry: some containers index by dts and seek by pts, where the entry's dts
                // would land a keyframe early. The container finds the keyframe at or before the target.
                _decoder.Seek(target);
                (_keepFrom, jumped) = (null, true);
            }
            bool got, beforeKeyframe = jumped;
            while (true)
            {
                got = _decoder.Next();
                if (!got)
                    break;
                beforeKeyframe &= !_decoder.IsKeyframe;
                if (_decoder.Pts + keep < 0 || _keepFrom is { } k && _decoder.Pts + k < 0)
                    continue;
                if (beforeKeyframe)
                {
                    // Possibly damaged: from the keyframe before instead (no further jumps).
                    _decoder.Seek(Math.Max(0, key - 1));
                    beforeKeyframe = false;
                    continue;
                }
                break;
            }
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

    /// <summary>The fps filter's next frame: decoded frames go in (shifted as ffmpeg shifts them) until one comes out.</summary>
    bool TryReadFps(Rational rate, out VideoFrame frame)
    {
        while (true)
        {
            if (_fpsOut != null)
            {
                ffmpeg.av_frame_unref(_fpsFrame);
                int got = ffmpeg.av_buffersink_get_frame(_fpsOut, _fpsFrame);
                if (got == 0)
                {
                    _index++;
                    long pts = _fpsFrame->pts; // in 1/rate
                    Convert(_fpsFrame, pts, TimeSpan.FromSeconds(pts * rate.Den / (double)rate.Num), out frame);
                    return true;
                }
                if (got == ffmpeg.AVERROR_EOF)
                    break;
                if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(got, "fps");
            }
            if (_fpsFlushed)
                break;
            if (!_decoder.Next())
            {
                if (_fpsIn == null)
                    break;
                Check(ffmpeg.av_buffersrc_add_frame_flags(_fpsIn, null, 0), "fps");
                _fpsFlushed = true;
                continue;
            }
            var src = _decoder.Frame;
            _decoder.EnsureRows(0, src->height);
            if (_fpsIn == null)
                BuildFps(src, rate);
            var feed = ffmpeg.av_frame_alloc();
            try
            {
                Check(ffmpeg.av_frame_ref(feed, src), "fps");
                feed->pts = _decoder.Pts + _decoder.StartOffset; // ffmpeg's command line shifts by the start time (no -copyts)
                Check(ffmpeg.av_buffersrc_add_frame_flags(_fpsIn, feed, 0), "fps");
            }
            finally
            {
                ffmpeg.av_frame_free(&feed);
            }
        }
        frame = default;
        return false;
    }

    /// <summary>buffer (the decoded frames' size, format, aspect and the stream's time base) -> fps -> buffersink.</summary>
    void BuildFps(AVFrame* first, Rational rate)
    {
        if (ffmpeg.avfilter_get_by_name("fps") == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "FrameRate needs FFmpeg's fps filter, which these libraries leave out.");
        _fpsGraph = ffmpeg.avfilter_graph_alloc();
        _fpsFrame = ffmpeg.av_frame_alloc();
        var sar = first->sample_aspect_ratio.num > 0 ? first->sample_aspect_ratio : new AVRational { num = 0, den = 1 };
        var tb = _decoder.StreamTimeBase;
        string args = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"video_size={first->width}x{first->height}:pix_fmt={first->format}:time_base={tb.num}/{tb.den}:pixel_aspect={sar.num}/{sar.den}");
        AVFilterContext* source, fps, sink;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_graph_create_filter(&fps, ffmpeg.avfilter_get_by_name("fps"), "fps", $"fps={rate.Num}/{rate.Den}", null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_link(source, 0, fps, 0), "fps");
        Check(ffmpeg.avfilter_link(fps, 0, sink, 0), "fps");
        Check(ffmpeg.avfilter_graph_config(_fpsGraph, null), "fps");
        _fpsIn = source;
        _fpsOut = sink;
    }

    void Convert(out VideoFrame frame)
    {
        _decoder.EnsureRows(0, _decoder.Frame->height);
        long pts = _decoder.Pts;
        Convert(_decoder.Frame, pts, TimeSpan.FromSeconds((pts + _decoder.StartOffset) * _decoder.TimeBase.Value), out frame);
    }

    void Convert(AVFrame* src, long pts, TimeSpan time, out VideoFrame frame)
    {
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
        bool rgb = _options.FrameFormat is not (FrameFormat.Gray8 or FrameFormat.Yuv420p);
        _out->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : src->colorspace;
        _out->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : src->color_range;
        int ret = ffmpeg.sws_scale_frame(_sws, _out, _in);
        ffmpeg.av_frame_unref(_in);
        Check(ret, "convert");
        int size = ffmpeg.av_image_get_buffer_size(_format, width, height, 1);
        ReadOnlySpan<byte> data;
        if (_options.FrameFormat != FrameFormat.Yuv420p)
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
        frame = new VideoFrame(_index, pts, time, width, height, _options.FrameFormat, data);
    }

    /// <summary>Frees the decoder, the scaler and the output buffer.</summary>
    public void Dispose()
    {
        if (_sws != null) { var s = _sws; ffmpeg.sws_free_context(&s); _sws = null; }
        if (_in != null) { var f = _in; ffmpeg.av_frame_free(&f); _in = null; }
        if (_out != null) { var f = _out; ffmpeg.av_frame_free(&f); _out = null; }
        if (_fpsFrame != null) { var f = _fpsFrame; ffmpeg.av_frame_free(&f); _fpsFrame = null; }
        if (_fpsGraph != null) { var g = _fpsGraph; ffmpeg.avfilter_graph_free(&g); _fpsGraph = null; _fpsIn = _fpsOut = null; }
        _decoder.Dispose();
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
