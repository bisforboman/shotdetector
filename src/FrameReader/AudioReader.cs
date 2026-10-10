using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What an <see cref="AudioReader"/> gives and how it opens the input.</summary>
public sealed record AudioReaderOptions
{
    /// <summary>Output sample rate in Hz; null: the stream's own.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Output channel count (FFmpeg's default layout for it: 1 mono, 2 stereo, ...); null: the stream's own.</summary>
    public int? Channels { get; init; }


    /// <summary>FFmpeg demuxer options, as ffmpeg's input options without the dash.</summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }
}

/// <summary>Decoded, converted audio: valid until the next read from its <see cref="AudioReader"/>.</summary>
public readonly ref struct AudioChunk
{
    internal AudioChunk(ReadOnlySpan<float> samples, int channels, int sampleRate, TimeSpan time)
    {
        Samples = samples;
        (Channels, SampleRate, Time) = (channels, sampleRate, time);
    }

    /// <summary>The samples, float32 interleaved (channel 0, 1, ... for each instant), as ffmpeg's <c>-f f32le</c> writes them.</summary>
    public ReadOnlySpan<float> Samples { get; }

    /// <summary>Samples per channel in this chunk.</summary>
    public int Length => Samples.Length / Channels;

    /// <summary>Channels.</summary>
    public int Channels { get; }

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }

    /// <summary>The time of the chunk's first sample from the input's start (approximate after resampling).</summary>
    public TimeSpan Time { get; }
}

/// <summary>
/// Reads a media file's audio, in this process through FFmpeg's libraries: the first (best) audio stream decoded and
/// converted with swresample to interleaved float32 at a chosen rate and channel count, the samples ffmpeg's command
/// line gives for <c>-vn -ar R -ac C -f f32le</c>. Chunks of whatever size the decoder gives.
/// </summary>
public sealed unsafe class AudioReader : IDisposable
{
    readonly StreamInput? _io;
    AVFormatContext* _fmt;
    AVCodecContext* _dec;
    AVPacket* _pkt;
    AVFrame* _frame;
    SwrContext* _swr;
    readonly int _stream, _outRate, _outChannels;
    readonly AVRational _timeBase;
    float[] _out = [];
    bool _draining, _decoderDone, _flushed;
    long _trimFrom = long.MinValue; // after a seek: input samples before this (in 1/sample_rate units, shifted) are dropped
    long _start;    // where shifted timestamps count from (AV_TIME_BASE): the input's start, or the seek point
    long _next;     // the next frame's position when it has no timestamp

    /// <summary>Opens a file or URL FFmpeg can read and its best audio stream.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">Rate, channels, libraries; null: the stream as it is.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or the input can't be opened or has no audio.</exception>
    public AudioReader(string path, AudioReaderOptions? options = null) : this(path, options, null) { }

    /// <summary>Opens a stream's bytes, read once (headers first).</summary>
    /// <param name="stream">The media's bytes; not disposed.</param>
    /// <param name="options">Rate, channels, libraries; null: the stream as it is.</param>
    public AudioReader(Stream stream, AudioReaderOptions? options = null) : this("pipe:0", options, new StreamInput([], stream)) { }

    AudioReader(string path, AudioReaderOptions? options, StreamInput? io)
    {
        var o = options ?? new AudioReaderOptions();
        if (o.SampleRate <= 0 || o.Channels <= 0)
            throw new ArgumentException("SampleRate and Channels are positive when set.", nameof(options));
        _io = io;
        try
        {
            FFmpegLibraries.Load();
            _fmt = Demuxer.Open(path, o.InputOptions, io);
            AVCodec* codec = null;
            _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (_stream < 0 || codec == null)
                throw new FrameReaderException(FrameReaderError.InvalidInput, "The input has no audio stream that these FFmpeg libraries decode.");
            var st = _fmt->streams[_stream];
            _timeBase = st->time_base;
            _dec = ffmpeg.avcodec_alloc_context3(codec);
            Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "decoder parameters");
            _dec->pkt_timebase = st->time_base;
            Check(ffmpeg.avcodec_open2(_dec, codec, null), "open decoder");
            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            DecodesFloat = _dec->sample_fmt is AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP
                or AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP;
            _start = _fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _fmt->start_time;
            _outRate = o.SampleRate ?? _dec->sample_rate;
            _outChannels = o.Channels ?? _dec->ch_layout.nb_channels;
            AVChannelLayout outLayout;
            if (o.Channels is { } c)
                ffmpeg.av_channel_layout_default(&outLayout, c);
            else
                Check(ffmpeg.av_channel_layout_copy(&outLayout, &_dec->ch_layout), "channel layout");
            SwrContext* swr = null;
            Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _outRate,
                &_dec->ch_layout, _dec->sample_fmt, _dec->sample_rate, 0, null), "resampler");
            ffmpeg.av_channel_layout_uninit(&outLayout);
            _swr = swr;
            Check(ffmpeg.swr_init(_swr), "resampler");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether the decoder gives floating-point samples (lossy codecs, float PCM) rather than integers.</summary>
    internal bool DecodesFloat { get; }

    /// <summary>Output samples per second.</summary>
    public int SampleRate => _outRate;

    /// <summary>Output channels.</summary>
    public int Channels => _outChannels;

    /// <summary>
    /// Seeks as ffmpeg's <c>-ss</c> does: the next chunk starts at the first sample at or after <paramref name="time"/>
    /// (from the keyframe before it, decoding forward and dropping the samples before, as its audio trim does).
    /// </summary>
    /// <param name="time">From the input's start.</param>
    public void Seek(TimeSpan time)
    {
        long timestamp = time.Ticks / 10;
        if (_fmt->start_time != ffmpeg.AV_NOPTS_VALUE)
            timestamp += _fmt->start_time;
        Check(ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, timestamp, timestamp, 0), "seek");
        ffmpeg.avcodec_flush_buffers(_dec);
        ffmpeg.swr_close(_swr);
        Check(ffmpeg.swr_init(_swr), "resampler");
        _draining = _decoderDone = _flushed = false;
        // ffmpeg shifts the timestamps by -(start + T) and trims what's still below 0, counted in samples.
        _trimFrom = 0;
        _start = timestamp;
        _next = 0;
    }

    /// <summary>Decodes and converts the next chunk; false at the end.</summary>
    /// <param name="chunk">The samples, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding or converting failed.</exception>
    public bool TryRead(out AudioChunk chunk)
    {
        while (true)
        {
            int got;
            TimeSpan time;
            if (TryDecode(out int skip, out _))
            {
                long pts = _frame->best_effort_timestamp;
                got = Convert(_frame->extended_data, _frame->nb_samples, skip, _frame->format, _frame->ch_layout.nb_channels);
                time = pts == ffmpeg.AV_NOPTS_VALUE ? TimeSpan.Zero
                    : TimeSpan.FromSeconds((pts - (_fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : ffmpeg.av_rescale_q(_fmt->start_time, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, _timeBase))) * (_timeBase.num / (double)_timeBase.den));
            }
            else if (!_flushed)
            {
                _flushed = true;
                got = Convert(null, 0, 0, 0, 0); // what the resampler still holds
                time = TimeSpan.Zero;
            }
            else
            {
                chunk = default;
                return false;
            }
            if (got > 0)
            {
                chunk = new AudioChunk(_out.AsSpan(0, got * _outChannels), _outChannels, _outRate, time);
                return true;
            }
        }
    }

    /// <summary>
    /// The next decoded frame, as the decoder gives it (<see cref="Decoded"/>: its own sample format, rate and layout),
    /// trimmed as ffmpeg trims after a seek: whole frames before the seek point dropped, the first <paramref name="skip"/>
    /// samples of the one across it to be dropped. <paramref name="position"/>: the first kept sample's place, in samples
    /// from the input's start (or the seek point), as ffmpeg's shifted timestamps put it.
    /// </summary>
    internal bool TryDecode(out int skip, out long position)
    {
        while (Decode())
        {
            skip = 0;
            long pts = _frame->best_effort_timestamp;
            var rate = new AVRational { num = 1, den = _frame->sample_rate };
            long shifted = pts == ffmpeg.AV_NOPTS_VALUE ? _next
                : ffmpeg.av_rescale_q(pts, _timeBase, rate) - ffmpeg.av_rescale_q(_start, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, rate);
            if (_trimFrom != long.MinValue && pts != ffmpeg.AV_NOPTS_VALUE)
            {
                if (shifted + _frame->nb_samples <= 0)
                    continue;
                skip = (int)Math.Max(0, -shifted);
                _trimFrom = long.MinValue;
            }
            position = shifted + skip;
            _next = shifted + _frame->nb_samples;
            return true;
        }
        skip = 0;
        position = 0;
        return false;
    }

    /// <summary>The frame <see cref="TryDecode"/> gave.</summary>
    internal AVFrame* Decoded => _frame;

    /// <summary>Where <see cref="TryDecode"/>'s positions count from, on <see cref="AudioChunk.Time"/>'s timeline.</summary>
    internal TimeSpan Origin => TimeSpan.FromTicks((_start - (_fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _fmt->start_time)) * 10);

    int Convert(byte** data, int samples, int skip, int format, int channels)
    {
        int room = ffmpeg.swr_get_out_samples(_swr, samples - skip);
        if (room <= 0)
            return 0;
        if (_out.Length < room * _outChannels)
            _out = new float[room * _outChannels];
        byte** input = data;
        byte_ptrArray8 offset = default;
        if (data != null && skip > 0)
        {
            // Skip the first samples: per plane for planar formats, interleaved otherwise.
            bool planar = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)format) != 0;
            int bytes = ffmpeg.av_get_bytes_per_sample((AVSampleFormat)format);
            for (uint i = 0; i < (planar ? (uint)channels : 1u) && i < 8; i++)
                offset[i] = data[i] + (long)skip * bytes * (planar ? 1 : channels);
            input = (byte**)&offset;
        }
        fixed (float* o = _out)
        {
            byte* outPtr = (byte*)o;
            int got = ffmpeg.swr_convert(_swr, &outPtr, room, input, samples - skip);
            Check(got, "resample");
            return got;
        }
    }

    bool Decode()
    {
        while (!_decoderDone)
        {
            int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (ret == 0)
                return true;
            if (ret == ffmpeg.AVERROR_EOF)
            {
                _decoderDone = true;
                break;
            }
            if (_draining)
            {
                _decoderDone = true;
                break;
            }
            if (ffmpeg.av_read_frame(_fmt, _pkt) < 0)
            {
                ffmpeg.avcodec_send_packet(_dec, null);
                _draining = true;
                continue;
            }
            if (_pkt->stream_index == _stream)
                ffmpeg.avcodec_send_packet(_dec, _pkt); // errors (damaged data) only lose that packet
            ffmpeg.av_packet_unref(_pkt);
        }
        return false;
    }

    /// <summary>Frees the decoder, the resampler and the input.</summary>
    public void Dispose()
    {
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        _io?.Dispose();
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
