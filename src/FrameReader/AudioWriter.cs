using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What an <see cref="AudioWriter"/> writes.</summary>
public sealed record AudioWriterOptions
{
    /// <summary>Samples per second of what you write, and of the file.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Channels of what you write (FFmpeg's default layout for the count: 1 mono, 2 stereo, ...).</summary>
    public required int Channels { get; init; }

    /// <summary>Bits per second for lossy encoders; null: the encoder's default, as ffmpeg without <c>-b:a</c>.</summary>
    public long? BitRate { get; init; }

    /// <summary>
    /// FFmpeg's encoder name (<c>libmp3lame</c>, <c>aac</c>, <c>pcm_s16le</c>); null: the container's usual one, as ffmpeg
    /// picks it from the file name (.mp3 MP3, .m4a/.mp4/.aac AAC, .wav 16-bit PCM).
    /// </summary>
    public string? Encoder { get; init; }

    /// <summary>FFmpeg's muxer name (<c>mp3</c>, <c>ipod</c>, <c>adts</c>, <c>wav</c>, <c>matroska</c>, ...); null: from the file name.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// Leave out what makes a file differ between FFmpeg versions (the Lavf/Lavc encoder tags, random IDs): ffmpeg's
    /// <c>-fflags +bitexact -flags:a +bitexact</c>. Off by default, so files carry the tags ffmpeg's own output does.
    /// </summary>
    public bool Bitexact { get; init; }

    /// <summary>The folder with FFmpeg's libraries (<see cref="FFmpegLibraries.Load"/>); null: next to the app, then the system's.</summary>
    public string? LibraryDirectory { get; init; }
}

/// <summary>
/// Encodes audio to a file, in this process through FFmpeg's libraries: interleaved float32 samples in, MP3, AAC or
/// WAV out, the file <c>ffmpeg -f f32le -ar R -ac C -i - -c:a ENCODER [-b:a B] OUT</c> writes from the same samples.
/// Pairs with <see cref="AudioReader"/> and <see cref="AudioFilter"/>: read, filter, write.
/// </summary>
/// <remarks>
/// As ffmpeg does, the samples go through a filter graph to the encoder's sample format and frame size, and the encoder
/// gets ffmpeg's settings (threads auto, the container's global header). <see cref="Dispose"/> finishes the file.
/// </remarks>
public sealed unsafe class AudioWriter : IDisposable
{
    readonly int _rate, _channels;
    AVFormatContext* _fmt;
    AVCodecContext* _enc;
    AVStream* _st;
    AVFilterGraph* _graph;
    AVFilterContext* _in, _out;
    AVFrame* _frame, _filtered;
    AVPacket* _pkt;
    long _written;
    bool _finished;

    /// <summary>Creates (or overwrites) the file and sets up the encoder.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="options">The samples' rate and channels, and how to encode them.</param>
    /// <exception cref="ArgumentException">A rate or channel count below 1.</exception>
    /// <exception cref="FrameReaderException">The libraries didn't load, lack the encoder or muxer, or the file can't be created.</exception>
    public AudioWriter(string path, AudioWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SampleRate <= 0 || options.Channels <= 0)
            throw new ArgumentException("SampleRate and Channels must be positive.", nameof(options));
        (_rate, _channels) = (options.SampleRate, options.Channels);
        try
        {
            FFmpegLibraries.Load(options.LibraryDirectory);
            Open(path, options);
        }
        catch
        {
            Free();
            throw;
        }
    }

    void Open(string path, AudioWriterOptions o)
    {
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmt, null, o.Format, path);
        if (ret < 0 || fmt == null)
            throw new FrameReaderException(FrameReaderError.InvalidInput,
                $"No muxer for \"{path}\"{(o.Format is null ? "" : $" ({o.Format})")} in these FFmpeg libraries: {FFmpegLibraries.ErrorMessage(ret)}");
        _fmt = fmt;
        if (o.Bitexact)
            _fmt->flags |= ffmpeg.AVFMT_FLAG_BITEXACT;

        AVCodec* codec = o.Encoder is { } name ? ffmpeg.avcodec_find_encoder_by_name(name)
            : ffmpeg.avcodec_find_encoder(ffmpeg.av_guess_codec(_fmt->oformat, null, path, null, AVMediaType.AVMEDIA_TYPE_AUDIO));
        if (codec == null || codec->type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            throw new FrameReaderException(FrameReaderError.InvalidInput,
                $"No audio encoder {(o.Encoder is null ? $"for \"{Path.GetExtension(path)}\"" : $"\"{o.Encoder}\"")} in these FFmpeg libraries.");

        BuildGraph(codec);

        _enc = ffmpeg.avcodec_alloc_context3(codec);
        _enc->sample_fmt = (AVSampleFormat)ffmpeg.av_buffersink_get_format(_out);
        _enc->sample_rate = ffmpeg.av_buffersink_get_sample_rate(_out);
        AVChannelLayout layout;
        Check(ffmpeg.av_buffersink_get_ch_layout(_out, &layout), "channel layout");
        _enc->ch_layout = layout;
        _enc->time_base = new AVRational { num = 1, den = _enc->sample_rate };
        if (o.BitRate is { } bitRate)
            _enc->bit_rate = bitRate;
        if (o.Bitexact)
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_BITEXACT;
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        AVDictionary* encOptions = null;
        ffmpeg.av_dict_set(&encOptions, "threads", "auto", 0); // as ffmpeg sets it
        try
        {
            Check(ffmpeg.avcodec_open2(_enc, codec, &encOptions), $"open the {Marshal.PtrToStringAnsi((nint)codec->name)} encoder");
        }
        finally
        {
            ffmpeg.av_dict_free(&encOptions);
        }
        if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) == 0)
            ffmpeg.av_buffersink_set_frame_size(_out, (uint)_enc->frame_size);

        _st = ffmpeg.avformat_new_stream(_fmt, null);
        Check(ffmpeg.avcodec_parameters_from_context(_st->codecpar, _enc), "stream parameters");
        _st->time_base = _enc->time_base;
        // The stream's "encoder" tag, as ffmpeg sets it (bitexact shortens it to "Lavc NAME"; the MP3 muxer writes it into
        // its LAME header). The container's "Lavf" tag libavformat adds itself, unless bitexact.
        ffmpeg.av_dict_set(&_st->metadata, "encoder",
            $"{(o.Bitexact ? "Lavc" : Ident("Lavc", ffmpeg.avcodec_version()))} {Marshal.PtrToStringAnsi((nint)codec->name)}", 0);
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            Check(ffmpeg.avio_open(&_fmt->pb, path, ffmpeg.AVIO_FLAG_WRITE), $"create \"{path}\"");
        Check(ffmpeg.avformat_write_header(_fmt, null), "write the header");
        _frame = ffmpeg.av_frame_alloc();
        _filtered = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

    static string Ident(string lib, uint version) =>
        string.Create(CultureInfo.InvariantCulture, $"{lib}{version >> 16}.{(version >> 8) & 0xff}.{version & 0xff}");

    /// <summary>
    /// abuffer (float32 interleaved, the samples' rate and layout) -> aformat (the encoder's sample formats, rates and
    /// layouts, as ffmpeg's output filter lists them) -> abuffersink.
    /// </summary>
    void BuildGraph(AVCodec* codec)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, _channels);
        byte* name = stackalloc byte[128];
        ffmpeg.av_channel_layout_describe(&layout, name, 128);
        string layoutName = Marshal.PtrToStringAnsi((nint)name)!;
        string args = string.Create(CultureInfo.InvariantCulture,
            $"time_base=1/{_rate}:sample_rate={_rate}:sample_fmt=flt:channel_layout={layoutName}");

        var formats = new List<string>();
        void* list;
        int count;
        if (ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &list, &count) >= 0 && list != null)
            for (int i = 0; i < count; i++)
                formats.Add(ffmpeg.av_get_sample_fmt_name(((AVSampleFormat*)list)[i]));
        var rates = new List<int>();
        if (ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE, 0, &list, &count) >= 0 && list != null)
            for (int i = 0; i < count; i++)
                rates.Add(((int*)list)[i]);
        string format = "aformat=" + string.Join(":", new[]
        {
            formats.Count > 0 ? "sample_fmts=" + string.Join("|", formats) : null,
            // The encoder's rates when it lists them (ffmpeg picks the nearest); else the samples' own.
            "sample_rates=" + (rates.Count > 0 ? string.Join("|", rates) : _rate.ToString(CultureInfo.InvariantCulture)),
            "channel_layouts=" + layoutName,
        }.Where(s => s is not null));

        AVFilterContext* source, sink;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "abuffer");
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "abuffersink");
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            Check(ffmpeg.avfilter_graph_parse_ptr(_graph, format, &inputs, &outputs, null), "filter graph");
            Check(ffmpeg.avfilter_graph_config(_graph, null), "filter graph");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
        _in = source;
        _out = sink;
    }

    /// <summary>Encodes samples: interleaved float32 (channel 0, 1, ... for each instant), any count.</summary>
    /// <param name="samples">A whole number of instants (a multiple of the channel count).</param>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    /// <exception cref="FrameReaderException">Encoding or writing failed.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (samples.Length % _channels != 0)
            throw new ArgumentException($"A whole number of instants: a multiple of {_channels} samples.", nameof(samples));
        if (samples.IsEmpty)
            return;
        ffmpeg.av_frame_unref(_frame);
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->sample_rate = _rate;
        _frame->nb_samples = samples.Length / _channels;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, _channels);
        Check(ffmpeg.av_frame_get_buffer(_frame, 0), "frame buffer");
        samples.CopyTo(new Span<float>(_frame->data[0], samples.Length));
        _frame->pts = _written;
        _written += _frame->nb_samples;
        Check(ffmpeg.av_buffersrc_add_frame_flags(_in, _frame, 0), "filter");
        Drain();
    }

    /// <summary>Filtered frames to the encoder, its packets to the file.</summary>
    void Drain()
    {
        while (true)
        {
            ffmpeg.av_frame_unref(_filtered);
            int got = ffmpeg.av_buffersink_get_frame(_out, _filtered);
            if (got == ffmpeg.AVERROR(ffmpeg.EAGAIN) || got == ffmpeg.AVERROR_EOF)
                return;
            Check(got, "filter");
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

    /// <summary>Flushes the encoder and finishes the file (its trailer); then frees everything.</summary>
    /// <exception cref="FrameReaderException">The last packets or the trailer couldn't be written.</exception>
    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        try
        {
            if (_pkt != null)
            {
                Check(ffmpeg.av_buffersrc_add_frame_flags(_in, null, 0), "filter");
                Drain();
                Check(ffmpeg.avcodec_send_frame(_enc, null), "encode");
                WritePackets();
                Check(ffmpeg.av_write_trailer(_fmt), "write the trailer");
            }
        }
        finally
        {
            Free();
        }
    }

    void Free()
    {
        if (_fmt != null)
        {
            if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _fmt->pb != null)
                ffmpeg.avio_closep(&_fmt->pb);
            ffmpeg.avformat_free_context(_fmt);
            _fmt = null;
        }
        if (_enc != null) { var e = _enc; ffmpeg.avcodec_free_context(&e); _enc = null; }
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; _in = _out = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_filtered != null) { var f = _filtered; ffmpeg.av_frame_free(&f); _filtered = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
