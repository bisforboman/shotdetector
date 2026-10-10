using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>An output file for the writers: the muxer, opened and finished as ffmpeg does it.</summary>
internal static unsafe class Output
{
    internal static AVFormatContext* Create(string path, string? format, bool bitexact)
    {
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmt, null, format, path);
        if (ret < 0 || fmt == null)
            throw new FrameReaderException(FrameReaderError.InvalidInput,
                $"No muxer for \"{path}\"{(format is null ? "" : $" ({format})")} in these FFmpeg libraries: {FFmpegLibraries.ErrorMessage(ret)}");
        if (bitexact)
            fmt->flags |= ffmpeg.AVFMT_FLAG_BITEXACT;
        return fmt;
    }

    /// <summary>Opens the file and writes the header, once every stream is added.</summary>
    internal static void Start(AVFormatContext* fmt, string path)
    {
        if ((fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            Encoder.Check(ffmpeg.avio_open(&fmt->pb, path, ffmpeg.AVIO_FLAG_WRITE), $"create \"{path}\"");
        Encoder.Check(ffmpeg.avformat_write_header(fmt, null), "write the header");
    }

    internal static void Free(ref AVFormatContext* fmt)
    {
        if (fmt == null)
            return;
        if ((fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && fmt->pb != null)
            ffmpeg.avio_closep(&fmt->pb);
        ffmpeg.avformat_free_context(fmt);
        fmt = null;
    }
}

/// <summary>
/// One encoded stream of an output file, as ffmpeg sets it up: frames through a filter graph to the encoder's format
/// (and, for audio, its frame size), the encoder with ffmpeg's settings, its packets to the muxer.
/// </summary>
internal sealed unsafe class Encoder : IDisposable
{
    readonly AVFormatContext* _fmt;
    AVFilterGraph* _graph;
    AVFilterContext* _in, _out;
    AVCodecContext* _enc;
    AVStream* _st;
    AVFrame* _filtered;
    AVPacket* _pkt;
    // Video: the encoder opens on the first filtered frame, from its properties, as ffmpeg's does.
    AVCodec* _codec;
    IReadOnlyDictionary<string, string>? _options;
    bool _bitexact, _threadsSet;

    Encoder(AVFormatContext* fmt) => _fmt = fmt;

    /// <summary>Called once the (video) encoder is open and its stream added, before its first packet.</summary>
    internal Action? Opened { get; set; }

    internal AVCodecContext* Context => _enc;

    /// <summary>The encoder by name, or the container's usual one for the media type, as ffmpeg picks it from the file name.</summary>
    internal static AVCodec* Find(AVFormatContext* fmt, string path, string? name, AVMediaType type, string what)
    {
        AVCodec* codec = name is not null ? ffmpeg.avcodec_find_encoder_by_name(name)
            : ffmpeg.avcodec_find_encoder(ffmpeg.av_guess_codec(fmt->oformat, null, path, null, type));
        if (codec == null || codec->type != type)
            throw new FrameReaderException(FrameReaderError.InvalidInput,
                $"No {what} encoder {(name is null ? $"for \"{Path.GetExtension(path)}\"" : $"\"{name}\"")} in these FFmpeg libraries.");
        return codec;
    }

    /// <summary>Float32 interleaved samples at <paramref name="rate"/> and <paramref name="channels"/> into <paramref name="codec"/>.</summary>
    internal static Encoder Audio(AVFormatContext* fmt, AVCodec* codec, int rate, int channels, long? bitRate, bool bitexact)
    {
        var e = new Encoder(fmt);
        try
        {
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, channels);
            byte* name = stackalloc byte[128];
            ffmpeg.av_channel_layout_describe(&layout, name, 128);
            string layoutName = Marshal.PtrToStringAnsi((nint)name)!;
            var formats = Supported<AVSampleFormat>(codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT).Select(f => ffmpeg.av_get_sample_fmt_name(f)).ToList();
            var rates = Supported<int>(codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE);
            // aformat with the encoder's sample formats and rates (ffmpeg picks the nearest), the samples' layout.
            string filter = "aformat=" + string.Join(":", new[]
            {
                formats.Count > 0 ? "sample_fmts=" + string.Join("|", formats) : null,
                "sample_rates=" + (rates.Count > 0 ? string.Join("|", rates) : rate.ToString(CultureInfo.InvariantCulture)),
                "channel_layouts=" + layoutName,
            }.Where(s => s is not null));
            e.Graph("abuffer", string.Create(CultureInfo.InvariantCulture,
                $"time_base=1/{rate}:sample_rate={rate}:sample_fmt=flt:channel_layout={layoutName}"), filter, "abuffersink");

            e._enc = ffmpeg.avcodec_alloc_context3(codec);
            e._enc->sample_fmt = (AVSampleFormat)ffmpeg.av_buffersink_get_format(e._out);
            e._enc->sample_rate = ffmpeg.av_buffersink_get_sample_rate(e._out);
            AVChannelLayout negotiated;
            Check(ffmpeg.av_buffersink_get_ch_layout(e._out, &negotiated), "channel layout");
            e._enc->ch_layout = negotiated;
            e._enc->time_base = new AVRational { num = 1, den = e._enc->sample_rate };
            if (bitRate is { } b)
                e._enc->bit_rate = b;
            e.Open(codec, bitexact, null);
            if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
                ffmpeg.av_buffersink_set_frame_size(e._out, (uint)e._enc->frame_size);
            return e;
        }
        catch
        {
            e.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Frames in <paramref name="input"/> format at <paramref name="width"/> x <paramref name="height"/> and
    /// <paramref name="rate"/> into <paramref name="codec"/>, converted to <paramref name="pixelFormat"/> (null: the
    /// encoder's formats, ffmpeg picking the nearest) by the scale filter ffmpeg inserts.
    /// </summary>
    internal static Encoder Video(AVFormatContext* fmt, AVCodec* codec, AVPixelFormat input, int width, int height, Rational rate,
        string? pixelFormat, long? bitRate, int? gop, int? keyintMin, IReadOnlyDictionary<string, string> options, bool bitexact)
    {
        var e = new Encoder(fmt);
        try
        {
            var formats = pixelFormat is not null ? [pixelFormat]
                : Supported<AVPixelFormat>(codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT).Select(f => ffmpeg.av_get_pix_fmt_name(f)).ToList();
            // ffmpeg's output filter: the pixel formats, and the colour ranges and spaces the encoder takes (with them, the
            // conversion tags its frames: x264 gets limited range from BGR, as from ffmpeg).
            var ranges = Supported<AVColorRange>(codec, AVCodecConfig.AV_CODEC_CONFIG_COLOR_RANGE).Select(r => ffmpeg.av_color_range_name(r)).ToList();
            var spaces = Supported<AVColorSpace>(codec, AVCodecConfig.AV_CODEC_CONFIG_COLOR_SPACE).Select(c => ffmpeg.av_color_space_name(c)).ToList();
            var parts = new[]
            {
                formats.Count > 0 ? "pix_fmts=" + string.Join("|", formats) : null,
                spaces.Count > 0 ? "color_spaces=" + string.Join("|", spaces) : null,
                ranges.Count > 0 ? "color_ranges=" + string.Join("|", ranges) : null,
            }.Where(x => x is not null).ToList();
            string filter = parts.Count > 0 ? "format=" + string.Join(":", parts) : "null";
            e.Graph("buffer", string.Create(CultureInfo.InvariantCulture,
                $"video_size={width}x{height}:pix_fmt={(int)input}:time_base={rate.Den}/{rate.Num}:frame_rate={rate.Num}/{rate.Den}:pixel_aspect=0/1"),
                filter, "buffersink");

            e._enc = ffmpeg.avcodec_alloc_context3(codec);
            e._enc->framerate = new AVRational { num = rate.Num, den = rate.Den };
            e._enc->time_base = new AVRational { num = rate.Den, den = rate.Num };
            if (bitRate is { } b)
                e._enc->bit_rate = b;
            if (gop is { } g)
                e._enc->gop_size = g;
            if (keyintMin is { } k)
                e._enc->keyint_min = k;
            // Unknown options fail here, not at the first frame.
            foreach (var (key, value) in options)
                Check(ffmpeg.av_opt_set(e._enc, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN) is var r && r == ffmpeg.AVERROR_OPTION_NOT_FOUND
                    ? throw new FrameReaderException(FrameReaderError.InvalidInput, $"The {Marshal.PtrToStringAnsi((nint)codec->name)} encoder has no option \"{key}\".")
                    : r, $"set {key}");
            e._threadsSet = options.ContainsKey("threads");
            e._codec = codec;
            e._options = null; // set on the context above
            e._bitexact = bitexact;
            e._filtered = ffmpeg.av_frame_alloc();
            return e;
        }
        catch
        {
            e.Dispose();
            throw;
        }
    }

    static List<T> Supported<T>(AVCodec* codec, AVCodecConfig config) where T : unmanaged
    {
        var result = new List<T>();
        void* list;
        int count;
        if (ffmpeg.avcodec_get_supported_config(null, codec, config, 0, &list, &count) >= 0 && list != null)
            for (int i = 0; i < count; i++)
                result.Add(((T*)list)[i]);
        return result;
    }

    void Graph(string source, string args, string filter, string sink)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        // One thread: the scale filter's slice threading varied the converted frames run to run (CI, 1 run in 8; the
        // conversion is cheap next to encoding).
        _graph->nb_threads = 1;
        AVFilterContext* src, snk;
        Check(ffmpeg.avfilter_graph_create_filter(&src, ffmpeg.avfilter_get_by_name(source), "in", args, null, _graph), source);
        Check(ffmpeg.avfilter_graph_create_filter(&snk, ffmpeg.avfilter_get_by_name(sink), "out", null, null, _graph), sink);
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = src;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = snk;
            Check(ffmpeg.avfilter_graph_parse_ptr(_graph, filter, &inputs, &outputs, null), "filter graph");
            Check(ffmpeg.avfilter_graph_config(_graph, null), "filter graph");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
        _in = src;
        _out = snk;
    }

    /// <summary>The video encoder opened from the first filtered frame's size, format and colour tags, as ffmpeg's enc_open.</summary>
    void OpenVideo(AVFrame* frame)
    {
        _enc->width = frame->width;
        _enc->height = frame->height;
        _enc->sample_aspect_ratio = frame->sample_aspect_ratio;
        _enc->pix_fmt = (AVPixelFormat)frame->format;
        _enc->color_range = frame->color_range;
        _enc->color_primaries = frame->color_primaries;
        _enc->color_trc = frame->color_trc;
        _enc->colorspace = frame->colorspace;
        _enc->chroma_sample_location = frame->chroma_location;
        // Field order as ffmpeg sets it: from an interlaced frame's flags, else progressive.
        _enc->field_order = (frame->flags & ffmpeg.AV_FRAME_FLAG_INTERLACED) == 0 ? AVFieldOrder.AV_FIELD_PROGRESSIVE
            : (frame->flags & ffmpeg.AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0 ? AVFieldOrder.AV_FIELD_TB : AVFieldOrder.AV_FIELD_BT;
        var codec = _codec;
        _codec = null;
        Open(codec, _bitexact, _options);
        Opened?.Invoke();
    }

    /// <summary>With no frame written: the video encoder opened from what the filter graph negotiated.</summary>
    internal void EnsureOpen()
    {
        if (_codec == null)
            return;
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->width = ffmpeg.av_buffersink_get_w(_out);
            frame->height = ffmpeg.av_buffersink_get_h(_out);
            frame->format = ffmpeg.av_buffersink_get_format(_out);
            frame->sample_aspect_ratio = ffmpeg.av_buffersink_get_sample_aspect_ratio(_out);
            frame->color_range = ffmpeg.av_buffersink_get_color_range(_out);
            frame->colorspace = ffmpeg.av_buffersink_get_colorspace(_out);
            OpenVideo(frame);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>Opens the encoder with ffmpeg's settings and adds its stream (with ffmpeg's "encoder" tag).</summary>
    void Open(AVCodec* codec, bool bitexact, IReadOnlyDictionary<string, string>? options)
    {
        if (bitexact)
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_BITEXACT;
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AVDictionary* dict = null;
        if (!_threadsSet)
            ffmpeg.av_dict_set(&dict, "threads", "auto", 0); // as ffmpeg sets it, unless the caller did
        foreach (var (key, value) in options ?? new Dictionary<string, string>())
            ffmpeg.av_dict_set(&dict, key, value, 0);
        try
        {
            int ret = ffmpeg.avcodec_open2(_enc, codec, &dict);
            Check(ret, $"open the {Marshal.PtrToStringAnsi((nint)codec->name)} encoder");
            AVDictionaryEntry* unused = ffmpeg.av_dict_get(dict, "", null, ffmpeg.AV_DICT_IGNORE_SUFFIX);
            if (unused != null)
                throw new FrameReaderException(FrameReaderError.InvalidInput,
                    $"The {Marshal.PtrToStringAnsi((nint)codec->name)} encoder has no option \"{Marshal.PtrToStringAnsi((nint)unused->key)}\".");
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
        }
        _st = ffmpeg.avformat_new_stream(_fmt, null);
        Check(ffmpeg.avcodec_parameters_from_context(_st->codecpar, _enc), "stream parameters");
        _st->time_base = _enc->time_base;
        if (_enc->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            _st->avg_frame_rate = _enc->framerate;
            _st->sample_aspect_ratio = _enc->sample_aspect_ratio;
        }
        // ffmpeg's stream "encoder" tag (bitexact shortens it to "Lavc NAME"; the MP3 muxer writes it into its LAME
        // header). The container's "Lavf" tag libavformat adds itself, unless bitexact.
        uint v = ffmpeg.avcodec_version();
        string lavc = bitexact ? "Lavc" : string.Create(CultureInfo.InvariantCulture, $"Lavc{v >> 16}.{(v >> 8) & 0xff}.{v & 0xff}");
        ffmpeg.av_dict_set(&_st->metadata, "encoder", $"{lavc} {Marshal.PtrToStringAnsi((nint)codec->name)}", 0);
        if (_filtered == null)
            _filtered = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>A frame in (null: the end), its encoded packets to the muxer.</summary>
    internal void Send(AVFrame* frame, AVPictureType forced = AVPictureType.AV_PICTURE_TYPE_NONE)
    {
        Check(ffmpeg.av_buffersrc_add_frame_flags(_in, frame, 0), "filter");
        while (true)
        {
            ffmpeg.av_frame_unref(_filtered);
            int got = ffmpeg.av_buffersink_get_frame(_out, _filtered);
            if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                return;
            if (got == ffmpeg.AVERROR_EOF)
            {
                EnsureOpen();
                Check(ffmpeg.avcodec_send_frame(_enc, null), "encode");
                WritePackets();
                return;
            }
            Check(got, "filter");
            if (_codec != null)
                OpenVideo(_filtered);
            if (forced != AVPictureType.AV_PICTURE_TYPE_NONE)
                _filtered->pict_type = forced;
            Check(ffmpeg.avcodec_send_frame(_enc, _filtered), "encode");
            WritePackets();
        }
    }

    void WritePackets()
    {
        while (true)
        {
            int got = ffmpeg.avcodec_receive_packet(_enc, _pkt);
            if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN) || got == ffmpeg.AVERROR_EOF)
                return;
            Check(got, "encode");
            ffmpeg.av_packet_rescale_ts(_pkt, _enc->time_base, _st->time_base);
            _pkt->stream_index = _st->index;
            Check(ffmpeg.av_interleaved_write_frame(_fmt, _pkt), "write");
        }
    }

    public void Dispose()
    {
        if (_enc != null) { var e = _enc; ffmpeg.avcodec_free_context(&e); _enc = null; }
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; _in = _out = null; }
        if (_filtered != null) { var f = _filtered; ffmpeg.av_frame_free(&f); _filtered = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
    }

    internal static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
