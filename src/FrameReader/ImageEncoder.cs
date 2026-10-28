using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>An image file format <see cref="ImageEncoder"/> writes; named as ShotDetector's image export names them.</summary>
public enum ImageFormat
{
    /// <summary>JPEG ("jpg"), FFmpeg's <c>mjpeg</c> encoder.</summary>
    Jpg,

    /// <summary>PNG ("png"), FFmpeg's <c>png</c> encoder.</summary>
    Png,

    /// <summary>WebP ("webp"), FFmpeg's <c>libwebp</c> encoder (lossy by default, or <see cref="ImageEncoderOptions.WebPLossless"/>).</summary>
    WebP,
}

/// <summary>What an <see cref="ImageEncoder"/> writes. Each format's options are ffmpeg's own; another format's are refused.</summary>
public sealed record ImageEncoderOptions
{
    /// <summary>The file format.</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Jpg;

    /// <summary>
    /// JPEG's quantizer scale, ffmpeg's <c>-q:v</c> (qscale): 2 to 31, lower is better quality and a larger file. Null:
    /// ffmpeg's default, its rate control at the default bit rate, started afresh for every image (so each is the file a
    /// one-frame ffmpeg run writes). Not ShotDetector's save-images quality, a 0-100 scale of its own. JPEG only.
    /// </summary>
    public int? JpegQscale { get; init; }

    /// <summary>
    /// PNG's zlib compression level, ffmpeg's <c>-compression_level</c>: 0 (fastest, largest) to 9 (slowest, smallest);
    /// lossless at every level, so speed against size, not quality. Null: zlib's default. PNG only.
    /// </summary>
    public int? PngCompressionLevel { get; init; }

    /// <summary>
    /// WebP's quality, libwebp's <c>-quality</c>: 0 to 100, higher is better quality and a larger file; lossless, how hard
    /// it tries to make the file smaller. Null: libwebp's default, 75. The scale of ShotDetector's save-images WebP quality
    /// (libwebp's, as OpenCV's), whose default is 100. WebP only.
    /// </summary>
    public float? WebPQuality { get; init; }

    /// <summary>WebP without loss, libwebp's <c>-lossless 1</c>; false: lossy (libwebp's default). WebP only.</summary>
    public bool WebPLossless { get; init; }

    /// <summary>
    /// WebP's compression level, ffmpeg's <c>-compression_level</c> (libwebp's method): 0 (fastest, larger) to 6 (slowest,
    /// smallest). Null: ffmpeg's default for libwebp, 4. WebP only.
    /// </summary>
    public int? WebPCompressionLevel { get; init; }
}

/// <summary>
/// How <see cref="ImageEncoder"/> filters a <see cref="DecodedFrame"/> before encoding it: ffmpeg's <c>-vf</c>, as one of a
/// size (<see cref="Width"/> and <see cref="Height"/>, or <see cref="MaxWidth"/>) or a filter of the caller's
/// (<see cref="Filter"/>); none of them: the frame at its own size, as <c>ffmpeg -i X out.jpg</c>.
/// </summary>
public sealed record ImageFilterOptions
{
    /// <summary>Output width in pixels, ffmpeg's <c>-vf scale=Width:Height</c> (its default flags, bicubic); with <see cref="Height"/>.</summary>
    public int? Width { get; init; }

    /// <summary>Output height in pixels; with <see cref="Width"/>.</summary>
    public int? Height { get; init; }

    /// <summary>
    /// The widest output in pixels, ffmpeg's <c>-vf scale='min(MaxWidth,iw)':-2</c>, as <see cref="VideoFrameReaderOptions.MaxWidth"/>:
    /// never upscaled, the height following the aspect ratio rounded to an even number. Not with Width and Height.
    /// </summary>
    public int? MaxWidth { get; init; }

    /// <summary>
    /// A filter chain in ffmpeg's <c>-vf</c> syntax (<c>scale=640:-2:flags=lanczos,setsar=1</c>), instead of the sizes:
    /// one frame out for each frame in (scale, crop, pad, setsar, transpose and the like; not fps or select, which hold
    /// or drop frames). Not with Width, Height or MaxWidth.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>The <c>-vf</c> these options give; null: none.</summary>
    internal string? Graph()
    {
        if (Width is null != Height is null || Width <= 0 || Height <= 0)
            throw new ArgumentException("Width and Height are both set (positive) or both left out.");
        if (MaxWidth is { } max && (max <= 0 || Width is not null))
            throw new ArgumentException("MaxWidth is positive and not set with Width and Height.");
        if (Filter is not null && (string.IsNullOrWhiteSpace(Filter) || Width is not null || MaxWidth is not null))
            throw new ArgumentException("Filter is a filter chain, not set with Width, Height or MaxWidth.");
        return Filter
            ?? (Width is { } w ? string.Create(CultureInfo.InvariantCulture, $"scale={w}:{Height}")
                : MaxWidth is { } m ? string.Create(CultureInfo.InvariantCulture, $"scale='min({m},iw)':-2")
                : null);
    }
}

/// <summary>
/// Encodes frames to image files in this process, through FFmpeg's <c>mjpeg</c>, <c>png</c> or <c>libwebp</c> encoder: the
/// file ffmpeg's command line writes from the same raw frame, with its aspect ratio and colour tags, and the same options
/// (<c>-f rawvideo ... -i frame -frames:v 1 out.jpg</c>; WebP: <c>-c:v libwebp out.webp</c>, as a build with
/// <c>libwebp_anim</c> picks that one by default), byte for byte with the same FFmpeg build (its version is in a JPEG's
/// comment; for WebP, libwebp's too). Frames are converted to the pixel format ffmpeg picks for the encoder, as its command line converts
/// them (the scale filter it inserts). One encoder serves any number of frames, of any size and format; not thread-safe.
/// A <see cref="DecodedFrame"/> (<see cref="FrameDecoder.TakeFrame"/>, <see cref="VideoFrameReader.TryTake"/>) encodes
/// through ffmpeg's own graph for
/// <c>ffmpeg -i X [-vf ...] -frames:v 1 out.jpg</c>: the decoded frame, the <c>-vf</c> of <see cref="ImageFilterOptions"/>,
/// then the conversion to the encoder's format, so its file is that command's byte for byte with the same FFmpeg build
/// (the decoder's options as ffmpeg's: autorotated by default, as ffmpeg is; deinterlaced only as with <c>-vf yadif,...</c>).
/// </summary>
/// <remarks>
/// A JPEG of a frame the reader scaled is close to, not identical with, ffmpeg's one command <c>-vf scale=... out.jpg</c>,
/// which converts to the encoder's full range in the same scaling pass; and ffmpeg's one command converts an RGB frame of a
/// BT.709 source with BT.709's matrix, a raw RGB frame (as here) with BT.601's. A <see cref="FrameFormat.Yuv420p"/> frame of
/// a 4:2:0 source at its own size gives exactly <c>ffmpeg -i X -frames:v 1 out.jpg</c>.
/// </remarks>
public sealed unsafe class ImageEncoder : IDisposable
{
    readonly ImageEncoderOptions _o;
    readonly AVCodec* _codec;
    AVFilterGraph* _graph;
    AVFilterContext* _in, _out;
    Input _graphFor;
    bool _metaOnly;
    AVCodecContext* _enc;
    AVFrame* _frame, _filtered;
    AVPacket* _pkt;
    long _count;

    /// <summary>Sets up the encoder (nothing is allocated in FFmpeg until the first frame).</summary>
    /// <param name="options">The format and its options; null: JPEG at ffmpeg's default quality.</param>
    /// <exception cref="ArgumentException">An unknown format, an option out of its range, or another format's option.</exception>
    /// <exception cref="FrameReaderException">The libraries didn't load or lack the encoder (<see cref="Capability.Jpeg"/>, <see cref="Capability.Png"/>, <see cref="Capability.WebP"/>).</exception>
    public ImageEncoder(ImageEncoderOptions? options = null)
    {
        _o = options ?? new ImageEncoderOptions();
        if (_o.Format is not (ImageFormat.Jpg or ImageFormat.Png or ImageFormat.WebP))
            throw new ArgumentException($"Unknown format {_o.Format}.", nameof(options));
        if (_o.JpegQscale is { } q && (_o.Format != ImageFormat.Jpg || q is < 2 or > 31))
            throw new ArgumentException("JpegQscale is JPEG's -q:v, 2 to 31.", nameof(options));
        if (_o.PngCompressionLevel is { } c && (_o.Format != ImageFormat.Png || c is < 0 or > 9))
            throw new ArgumentException("PngCompressionLevel is PNG's -compression_level, 0 to 9.", nameof(options));
        if (_o.WebPQuality is { } wq && (_o.Format != ImageFormat.WebP || !(wq >= 0 && wq <= 100)))
            throw new ArgumentException("WebPQuality is libwebp's -quality, 0 to 100.", nameof(options));
        if (_o.WebPLossless && _o.Format != ImageFormat.WebP)
            throw new ArgumentException("WebPLossless is libwebp's -lossless, for WebP.", nameof(options));
        if (_o.WebPCompressionLevel is { } wc && (_o.Format != ImageFormat.WebP || wc is < 0 or > 6))
            throw new ArgumentException("WebPCompressionLevel is libwebp's -compression_level, 0 to 6.", nameof(options));
        FFmpegLibraries.Load();
        string name = _o.Format switch
        {
            ImageFormat.Jpg => "mjpeg",
            ImageFormat.Png => "png",
            _ => "libwebp",
        };
        _codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (_codec == null)
            throw new FrameReaderException(FrameReaderError.InvalidInput, $"These FFmpeg libraries have no {name} encoder.");
    }

    /// <summary>The frame as an image file, written to <paramref name="output"/>.</summary>
    /// <param name="frame">A frame from a <see cref="VideoFrameReader"/>, with its colour tags and aspect ratio.</param>
    /// <param name="output">Where the file's bytes go; not disposed.</param>
    /// <exception cref="FrameReaderException">Converting or encoding failed.</exception>
    public void Encode(VideoFrame frame, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        fixed (byte* data = frame.Data)
            Encode(Input.Of(frame, data), output);
    }

    /// <summary>The frame as an image file's bytes.</summary>
    /// <param name="frame">A frame from a <see cref="VideoFrameReader"/>, with its colour tags and aspect ratio.</param>
    /// <returns>The file.</returns>
    /// <exception cref="FrameReaderException">Converting or encoding failed.</exception>
    public byte[] Encode(VideoFrame frame)
    {
        var output = new MemoryStream();
        Encode(frame, output);
        return output.ToArray();
    }

    /// <summary>
    /// The decoded frame as an image file, written to <paramref name="output"/>: the file <c>ffmpeg [-ss T] -i X [-vf F]
    /// -frames:v 1 out.jpg</c> writes for that frame, F being the filter's <c>-vf</c>.
    /// </summary>
    /// <param name="frame">A frame from <see cref="FrameDecoder.TakeFrame"/> or a reader's TryTake methods; not disposed.</param>
    /// <param name="output">Where the file's bytes go; not disposed.</param>
    /// <param name="filter">The size or filter; null: the frame's own size.</param>
    /// <exception cref="ArgumentException">Options that don't go together, or a filter chain FFmpeg can't parse.</exception>
    /// <exception cref="FrameReaderException">Filtering or encoding failed, or the filter gave no frame for this one.</exception>
    public void Encode(DecodedFrame frame, Stream output, ImageFilterOptions? filter = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        string? graph;
        try
        {
            graph = filter?.Graph();
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException(e.Message, nameof(filter));
        }

        Encode(Input.Of(frame.Frame, graph), output, frame);
    }

    /// <summary>The decoded frame as an image file's bytes (see <see cref="Encode(DecodedFrame, Stream, ImageFilterOptions?)"/>).</summary>
    /// <param name="frame">A frame from <see cref="FrameDecoder.TakeFrame"/> or a reader's TryTake methods; not disposed.</param>
    /// <param name="filter">The size or filter; null: the frame's own size.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentException">Options that don't go together, or a filter chain FFmpeg can't parse.</exception>
    /// <exception cref="FrameReaderException">Filtering or encoding failed, or the filter gave no frame for this one.</exception>
    public byte[] Encode(DecodedFrame frame, ImageFilterOptions? filter = null)
    {
        var output = new MemoryStream();
        Encode(frame, output, filter);
        return output.ToArray();
    }

    /// <summary>A frame's pixels and what the filter graph is told about them (its buffer source's parameters), and the <c>-vf</c> before the conversion.</summary>
    internal readonly record struct Input(nint Data,
        int Width,
        int Height,
        AVPixelFormat Format,
        AVRational Sar,
        AVColorRange Range,
        AVColorSpace Space,
        AVColorPrimaries Primaries,
        AVColorTransferCharacteristic Transfer,
        string? Filter = null)
    {
        internal static Input Of(VideoFrame frame, byte* data) => new(
            (nint)data,
            frame.Width,
            frame.Height,
            VideoFrameReader.PixelFormat(frame.FrameFormat),
            frame.Tags.Sar.den > 0 ? frame.Tags.Sar : new AVRational { num = 0, den = 1 },
            frame.Tags.Range,
            frame.Tags.Space,
            frame.Tags.Primaries,
            frame.Tags.Transfer);

        // A decoded frame: what ffmpeg's command line sets its buffer source from (ifilter_parameters_from_frame).
        internal static Input Of(AVFrame* frame, string? filter) => new(
            0,
            frame->width,
            frame->height,
            (AVPixelFormat)frame->format,
            frame->sample_aspect_ratio.den > 0 ? frame->sample_aspect_ratio : new AVRational { num = 0, den = 1 },
            frame->color_range,
            frame->colorspace,
            frame->color_primaries,
            frame->color_trc,
            filter);

        // The graph depends on these; primaries and transfer travel on the frames.
        internal bool SameGraph(Input other) => (Width, Height, Format, Sar.num, Sar.den, Range, Space, Filter)
            == (other.Width, other.Height, other.Format, other.Sar.num, other.Sar.den, other.Range, other.Space, other.Filter);
    }

    void Encode(Input input, Stream output, DecodedFrame? decoded = null)
    {
        if (_graph == null || !input.SameGraph(_graphFor))
        {
            BuildGraph(input);
            CloseEncoder(); // a new size or format: the encoder opens again from the graph's output
        }

        if (_frame == null)
        {
            _frame = ffmpeg.av_frame_alloc();
            _filtered = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
        }

        ffmpeg.av_frame_unref(_frame);
        if (decoded is not null)
            PutDecoded(decoded);
        else
            PutRaw(input);
        _frame->pts = _count;
        Check(ffmpeg.av_buffersrc_add_frame_flags(_in, _frame, 0), "filter");
        ffmpeg.av_frame_unref(_filtered);
        int got = ffmpeg.av_buffersink_get_frame(_out, _filtered);
        if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            FreeGraph(); // the filter holds the frame: start afresh next time
            throw new FrameReaderException(FrameReaderError.WriteFailed, "The filter gave no frame for this one (one frame out for each frame in).");
        }

        Check(got, "filter");

        // ffmpeg's rate control (no qscale) carries its state from frame to frame: a fresh encoder for each image
        // writes what a one-frame ffmpeg run writes. With a qscale every frame is encoded alone anyway.
        if (_o.Format == ImageFormat.Jpg && _o.JpegQscale is null)
            CloseEncoder();
        if (_enc == null)
            OpenEncoder(_filtered, _metaOnly ? decoded?.BitsPerRawSample ?? 0 : 0);
        if (_o.JpegQscale is { } q)
            _filtered->quality = q * ffmpeg.FF_QP2LAMBDA; // as ffmpeg passes -q:v: on every frame, which mjpeg reads
        _filtered->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        _filtered->pts = _count++; // increasing, as encoders expect of one stream
        Check(ffmpeg.avcodec_send_frame(_enc, _filtered), "encode");
        ffmpeg.av_packet_unref(_pkt);
        Check(ffmpeg.avcodec_receive_packet(_enc, _pkt), "encode"); // one thread: the packet comes at once, and is the whole file
        output.Write(new ReadOnlySpan<byte>(_pkt->data, _pkt->size));
        ffmpeg.av_packet_unref(_pkt);
    }

    /// <summary>A reference to the decoded frame, as ffmpeg's command line sends it into its graph.</summary>
    void PutDecoded(DecodedFrame decoded)
    {
        Check(ffmpeg.av_frame_ref(_frame, decoded.Frame), "frame");
        _frame->time_base = new AVRational { num = 1, den = 25 };
    }

    /// <summary>The pixels into a frame of FFmpeg's own (a copy: the graph may keep it while converting).</summary>
    void PutRaw(Input input)
    {
        (_frame->format, _frame->width, _frame->height) = ((int)input.Format, input.Width, input.Height);
        Check(ffmpeg.av_frame_get_buffer(_frame, 0), "frame buffer");
        var planes = default(byte_ptrArray4);
        var strides = default(int_array4);
        Check(ffmpeg.av_image_fill_arrays(ref planes, ref strides, (byte*)input.Data, input.Format, input.Width, input.Height, 1), "frame");
        var dst = default(byte_ptrArray4);
        var dstStrides = default(int_array4);
        for (uint i = 0; i < 4; i++)
        {
            dst[i] = _frame->data[i];
            dstStrides[i] = _frame->linesize[i];
        }

        ffmpeg.av_image_copy(ref dst, dstStrides, planes, strides, input.Format, input.Width, input.Height);
        _frame->sample_aspect_ratio = input.Sar;
        _frame->color_range = input.Range;
        _frame->colorspace = input.Space;
        _frame->color_primaries = input.Primaries;
        _frame->color_trc = input.Transfer;
    }

    /// <summary>
    /// buffer (the frame's size, format, aspect and colour) -> the -vf, if any -> format (the encoder's pixel formats, colour
    /// spaces and ranges, as ffmpeg's command line constrains its output) -> buffersink; lavfi inserts the scale filter that
    /// converts.
    /// </summary>
    void BuildGraph(Input input)
    {
        FreeGraph();
        var formats = Supported<AVPixelFormat>(AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT).Select(f => ffmpeg.av_get_pix_fmt_name(f)).ToList();
        var ranges = Supported<AVColorRange>(AVCodecConfig.AV_CODEC_CONFIG_COLOR_RANGE).Select(r => ffmpeg.av_color_range_name(r)).ToList();
        var spaces = Supported<AVColorSpace>(AVCodecConfig.AV_CODEC_CONFIG_COLOR_SPACE).Select(c => ffmpeg.av_color_space_name(c)).ToList();
        string filter = "format=" + string.Join(":", new[]
        {
            formats.Count > 0 ? "pix_fmts=" + string.Join("|", formats) : null,
            spaces.Count > 0 ? "color_spaces=" + string.Join("|", spaces) : null,
            ranges.Count > 0 ? "color_ranges=" + string.Join("|", ranges) : null,
        }.Where(x => x is not null));
        if (input.Filter is not null)
            filter = input.Filter + "," + filter;
        string args = string.Create(CultureInfo.InvariantCulture,
            $"video_size={input.Width}x{input.Height}:pix_fmt={(int)input.Format}:time_base=1/25:pixel_aspect={input.Sar.num}/{input.Sar.den}:colorspace={(int)input.Space}:range={(int)input.Range}");

        _graph = ffmpeg.avfilter_graph_alloc();
        _graph->nb_threads = 1; // as VideoWriter's: scale's slice threads made conversions vary
        AVFilterContext* src, snk;
        Check(ffmpeg.avfilter_graph_create_filter(&src, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, _graph), "buffer");
        Check(ffmpeg.avfilter_graph_create_filter(&snk, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "buffersink");
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = src;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = snk;
            int parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, filter, &inputs, &outputs, null);
            if (parsed < 0 && input.Filter is not null)
            {
                FreeGraph();
                throw new ArgumentException($"FFmpeg can't parse the filter \"{input.Filter}\": {FFmpegLibraries.ErrorMessage(parsed)}", "filter");
            }

            Check(parsed, "filter graph");
            Check(ffmpeg.avfilter_graph_config(_graph, null), "filter graph");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }

        _in = src;
        _out = snk;
        _graphFor = input;

        // ffmpeg hands its encoder the decoder's bits per raw sample only when the graph leaves the pixels alone
        // (graph_is_meta: metadata-only filters, the sink and the buffer source; a scale inserted to convert is neither).
        _metaOnly = true;
        for (uint i = 0; i < _graph->nb_filters; i++)
        {
            var f = _graph->filters[i];
            if ((f->filter->flags & ffmpeg.AVFILTER_FLAG_METADATA_ONLY) == 0 && f->nb_outputs != 0 && f != src)
                _metaOnly = false;
        }
    }

    /// <summary>The encoder opened from the filtered frame, as ffmpeg's enc_open sets it up.</summary>
    void OpenEncoder(AVFrame* frame, int bitsPerRawSample)
    {
        _enc = ffmpeg.avcodec_alloc_context3(_codec);
        _enc->width = frame->width;
        _enc->height = frame->height;
        _enc->sample_aspect_ratio = frame->sample_aspect_ratio;
        _enc->pix_fmt = (AVPixelFormat)frame->format;
        _enc->color_range = frame->color_range;
        _enc->color_primaries = frame->color_primaries;
        _enc->color_trc = frame->color_trc;
        _enc->colorspace = frame->colorspace;
        _enc->chroma_sample_location = frame->chroma_location;
        _enc->bits_per_raw_sample = Math.Min(bitsPerRawSample, ffmpeg.av_pix_fmt_desc_get(_enc->pix_fmt)->comp[0].depth);
        _enc->field_order = AVFieldOrder.AV_FIELD_PROGRESSIVE;
        _enc->time_base = new AVRational { num = 1, den = 25 };
        _enc->framerate = new AVRational { num = 25, den = 1 };
        if (_o.JpegQscale is { } q)
        {
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            _enc->global_quality = q * ffmpeg.FF_QP2LAMBDA;
        }

        if (_o.PngCompressionLevel is { } c)
            _enc->compression_level = c;
        if (_o.WebPCompressionLevel is { } wc)
            _enc->compression_level = wc;
        if (_o.WebPQuality is { } wq)
            Check(ffmpeg.av_opt_set_double(_enc->priv_data, "quality", wq, 0), "quality");
        if (_o.WebPLossless)
            Check(ffmpeg.av_opt_set_int(_enc->priv_data, "lossless", 1, 0), "lossless");

        // One thread: ffmpeg's "auto" gives the same bytes (checked from 1 to 16 threads), and frame threads would hold
        // the packet back until the next frame or the end.
        _enc->thread_count = 1;
        Check(ffmpeg.avcodec_open2(_enc, _codec, null), $"open the {Marshal.PtrToStringAnsi((nint)_codec->name)} encoder");
    }

    List<T> Supported<T>(AVCodecConfig config)
        where T : unmanaged
    {
        var result = new List<T>();
        void* list;
        int count;
        if (ffmpeg.avcodec_get_supported_config(null, _codec, config, 0, &list, &count) >= 0 && list != null)
        {
            for (int i = 0; i < count; i++)
                result.Add(((T*)list)[i]);
        }

        return result;
    }

    void CloseEncoder()
    {
        if (_enc != null)
        {
            var e = _enc;
            ffmpeg.avcodec_free_context(&e);
            _enc = null;
        }
    }

    void FreeGraph()
    {
        if (_graph != null)
        {
            var g = _graph;
            ffmpeg.avfilter_graph_free(&g);
            _graph = null;
            _in = _out = null;
        }
    }

    /// <summary>Frees the encoder, the filter graph and the frames.</summary>
    public void Dispose()
    {
        CloseEncoder();
        FreeGraph();
        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_filtered != null)
        {
            var f = _filtered;
            ffmpeg.av_frame_free(&f);
            _filtered = null;
        }

        if (_pkt != null)
        {
            var p = _pkt;
            ffmpeg.av_packet_free(&p);
            _pkt = null;
        }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.WriteFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
