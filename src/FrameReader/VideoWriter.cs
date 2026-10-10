using System.Globalization;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>An audio track for a <see cref="VideoWriter"/>: what you write with <see cref="VideoWriter.WriteAudio"/>.</summary>
public sealed record AudioTrackOptions
{
    /// <summary>Samples per second of what you write, and of the track.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Channels of what you write.</summary>
    public required int Channels { get; init; }

    /// <summary>Bits per second; null: the encoder's default.</summary>
    public long? BitRate { get; init; }

    /// <summary>FFmpeg's encoder name; null: the container's usual one (AAC for .mp4 and .mkv).</summary>
    public string? Encoder { get; init; }
}

/// <summary>What a <see cref="VideoWriter"/> writes. The options mirror ffmpeg's for <c>-c:v libx264</c>.</summary>
public sealed record VideoWriterOptions
{
    /// <summary>Width of the frames you write, in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the frames you write, in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Frames per second (ffmpeg's <c>-r</c> on the input), e.g. <c>new(25, 1)</c> or <c>new(30000, 1001)</c>.</summary>
    public required Rational FrameRate { get; init; }

    /// <summary>The layout of the frames you write (as <see cref="VideoFrameReader"/> gives them); default BGR.</summary>
    public FrameFormat FrameFormat { get; init; } = FrameFormat.Bgr24;

    /// <summary>FFmpeg's encoder name; null: <c>libx264</c> (in the ShotDetector.Native.Gpl libraries, or any FFmpeg with it).</summary>
    public string? Encoder { get; init; }

    /// <summary>The encoded pixel format (<c>-pix_fmt</c>), e.g. <c>yuv420p</c> for players everywhere; null: the encoder's nearest to the input, as ffmpeg picks it (yuv444p for BGR with x264).</summary>
    public string? PixelFormat { get; init; }

    /// <summary>Constant rate factor (<c>-crf</c>; x264: 0-51, 23 by default); null: the encoder's default or <see cref="BitRate"/>.</summary>
    public double? Crf { get; init; }

    /// <summary>Bits per second (<c>-b:v</c>); null: none.</summary>
    public long? BitRate { get; init; }

    /// <summary>x264's preset (<c>-preset</c>: ultrafast ... veryslow); null: medium.</summary>
    public string? Preset { get; init; }

    /// <summary>The longest gap between keyframes, in frames (<c>-g</c>); null: the encoder's (250 for x264).</summary>
    public int? Gop { get; init; }

    /// <summary>The shortest gap between keyframes, in frames (<c>-keyint_min</c>); null: the encoder's.</summary>
    public int? KeyintMin { get; init; }

    /// <summary>
    /// A keyframe at least every this long (<c>-force_key_frames expr:gte(t,n_forced*N)</c>): the first frame at or after
    /// each multiple. With x264, add <c>scenecut=0</c> to <see cref="X264Params"/> for keyframes there only.
    /// </summary>
    public TimeSpan? KeyframeEvery { get; init; }

    /// <summary>Forced keyframes as IDR frames, where a stream can be cut (<c>-forced-idr 1</c>); default true.</summary>
    public bool ForcedIdr { get; init; } = true;

    /// <summary>x264's own options (<c>-x264-params</c>), e.g. <c>scenecut=0:bframes=2</c>.</summary>
    public string? X264Params { get; init; }

    /// <summary>
    /// Other encoder options by name, as ffmpeg's <c>-OPTION value</c> for the encoder (e.g. <c>tune</c>, <c>profile</c>,
    /// <c>threads</c>: "auto" by default as in ffmpeg; x264 with several threads can differ slightly run to run, so
    /// set 1 on both sides to compare files byte for byte).
    /// </summary>
    public IReadOnlyDictionary<string, string>? EncoderOptions { get; init; }

    /// <summary>An audio track, written with <see cref="VideoWriter.WriteAudio"/>; null: video only.</summary>
    public AudioTrackOptions? Audio { get; init; }

    /// <summary>FFmpeg's muxer name; null: from the file name (.mp4, .mkv, .mov).</summary>
    public string? Format { get; init; }

    /// <summary>Leave out what differs between FFmpeg versions (ffmpeg's <c>-fflags +bitexact -flags +bitexact</c>).</summary>
    public bool Bitexact { get; init; }

    /// <summary>The folder with FFmpeg's libraries; null: next to the app, then the system's.</summary>
    public string? LibraryDirectory { get; init; }
}

/// <summary>
/// Encodes frames (and optionally audio) to a video file, in this process: the file ffmpeg writes from the same raw
/// frames with <c>-c:v libx264</c> and the same options, byte for byte with the same FFmpeg build. The counterpart of
/// <see cref="VideoFrameReader"/>.
/// </summary>
/// <remarks>
/// H.264 needs an FFmpeg with x264: the ShotDetector.Native.Gpl.&lt;rid&gt; packages, used instead of
/// ShotDetector.Native, or your own build. x264 is GPL: an app that ships with it falls under the GPL; and H.264 is
/// covered by Via LA's AVC patent pool, whoever distributes the encoder. <see cref="Dispose"/> finishes the file.
/// </remarks>
public sealed unsafe class VideoWriter : IDisposable
{
    readonly VideoWriterOptions _o;
    readonly AVPixelFormat _input;
    readonly int _frameBytes;
    readonly string _path;
    readonly List<float[]> _pendingAudio = []; // written before the first frame opened the file
    AVFormatContext* _fmt;
    Encoder? _video, _audio;
    AVFrame* _frame, _samples;
    long _frames, _written;
    int _forced;
    bool _started, _finished;

    /// <summary>Creates (or overwrites) the file and sets up the encoders.</summary>
    /// <param name="path">The file to write (.mp4, .mkv, .mov).</param>
    /// <param name="options">The frames' size, rate and layout, and how to encode them.</param>
    /// <exception cref="ArgumentException">A size or rate below 1.</exception>
    /// <exception cref="FrameReaderException">The libraries didn't load or lack the encoder (libx264: the GPL libraries) or
    /// muxer, an encoder option is unknown, or the file can't be created.</exception>
    public VideoWriter(string path, VideoWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Width <= 0 || options.Height <= 0 || options.FrameRate.Num <= 0 || options.FrameRate.Den <= 0)
            throw new ArgumentException("Width, Height and FrameRate must be positive.", nameof(options));
        if (options.Audio is { SampleRate: <= 0 } or { Channels: <= 0 })
            throw new ArgumentException("The audio track's SampleRate and Channels must be positive.", nameof(options));
        (_o, _path) = (options, path);
        _input = options.FrameFormat switch
        {
            FrameFormat.Bgr24 => AVPixelFormat.AV_PIX_FMT_BGR24,
            FrameFormat.Rgb24 => AVPixelFormat.AV_PIX_FMT_RGB24,
            FrameFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
            FrameFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
            FrameFormat.Gray8 => AVPixelFormat.AV_PIX_FMT_GRAY8,
            FrameFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
            _ => throw new ArgumentException($"Unknown format {options.FrameFormat}.", nameof(options)),
        };
        try
        {
            FFmpegLibraries.Load(options.LibraryDirectory);
            _frameBytes = ffmpeg.av_image_get_buffer_size(_input, options.Width, options.Height, 1);
            _fmt = Output.Create(path, options.Format, options.Bitexact);
            string encoder = options.Encoder ?? "libx264";
            if (ffmpeg.avcodec_find_encoder_by_name(encoder) == null && options.Encoder is null)
                throw new FrameReaderException(FrameReaderError.InvalidInput,
                    "These FFmpeg libraries have no libx264: H.264 encoding needs the ShotDetector.Native.Gpl.<rid> package " +
                    "(GPL; instead of ShotDetector.Native) or an FFmpeg built with x264, or set Encoder to one these libraries have.");
            var codec = Encoder.Find(_fmt, path, encoder, AVMediaType.AVMEDIA_TYPE_VIDEO, "video");
            _video = Encoder.Video(_fmt, codec, _input, options.Width, options.Height, options.FrameRate, options.PixelFormat,
                options.BitRate, options.Gop, options.KeyintMin, EncoderOptions(options, encoder), options.Bitexact);
            // As ffmpeg: the video encoder opens on the first filtered frame (its colour tags), then the file starts.
            _video.Opened = Start;
            if (options.Audio is { } a)
                Encoder.Find(_fmt, path, a.Encoder, AVMediaType.AVMEDIA_TYPE_AUDIO, "audio"); // fail early
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Free();
            throw;
        }
    }

    /// <summary>Once the video encoder is open: the audio track's stream, the header, the audio written so far.</summary>
    void Start()
    {
        if (_o.Audio is { } a)
        {
            var codec = Encoder.Find(_fmt, _path, a.Encoder, AVMediaType.AVMEDIA_TYPE_AUDIO, "audio");
            _audio = Encoder.Audio(_fmt, codec, a.SampleRate, a.Channels, a.BitRate, _o.Bitexact);
            _samples = ffmpeg.av_frame_alloc();
        }
        Output.Start(_fmt, _path);
        _started = true;
        foreach (var pending in _pendingAudio)
            SendAudio(pending);
        _pendingAudio.Clear();
    }

    /// <summary>The encoder options ffmpeg's command line would pass for these settings.</summary>
    static Dictionary<string, string> EncoderOptions(VideoWriterOptions o, string encoder)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in o.EncoderOptions ?? new Dictionary<string, string>())
            d[k] = v;
        if (o.Preset is { } preset)
            d["preset"] = preset;
        if (o.Crf is { } crf)
            d["crf"] = crf.ToString(CultureInfo.InvariantCulture);
        if (o.X264Params is { } p)
            d["x264-params"] = p;
        if (o.KeyframeEvery is not null && o.ForcedIdr && encoder is "libx264" or "libx264rgb")
            d["forced-idr"] = "1";
        return d;
    }

    /// <summary>Bytes in one frame you write: rows packed, planes one after another (as <see cref="VideoFrame.Data"/>).</summary>
    public int FrameSize => _frameBytes;

    /// <summary>Encodes the next frame.</summary>
    /// <param name="frame">Exactly <see cref="FrameSize"/> bytes in <see cref="VideoWriterOptions.FrameFormat"/>.</param>
    /// <exception cref="ObjectDisposedException">After <see cref="Dispose"/>.</exception>
    /// <exception cref="FrameReaderException">Encoding or writing failed.</exception>
    public void Write(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (frame.Length != _frameBytes)
            throw new ArgumentException($"A frame is {_frameBytes} bytes ({_o.Width}x{_o.Height} {_o.FrameFormat}), not {frame.Length}.", nameof(frame));
        ffmpeg.av_frame_unref(_frame);
        _frame->format = (int)_input;
        _frame->width = _o.Width;
        _frame->height = _o.Height;
        // As ffmpeg's rawvideo input gives frames: rows packed (stride = width), and nothing uninitialised after them, which
        // the conversion's SIMD may read past the last pixel (CI saw files vary run to run with padded rows).
        Encoder.Check(ffmpeg.av_frame_get_buffer(_frame, 1), "frame buffer");
        for (uint i = 0; i < 8 && _frame->buf[i] != null; i++)
            new Span<byte>(_frame->buf[i]->data, (int)_frame->buf[i]->size).Clear();
        var src = new byte_ptrArray4();
        var srcLines = new int_array4();
        fixed (byte* data = frame)
        {
            Encoder.Check(ffmpeg.av_image_fill_arrays(ref src, ref srcLines, data, _input, _o.Width, _o.Height, 1), "frame");
            var dst = new byte_ptrArray4();
            var dstLines = new int_array4();
            dst.UpdateFrom(_frame->data);
            dstLines.UpdateFrom(_frame->linesize);
            ffmpeg.av_image_copy(ref dst, in dstLines, src, srcLines, _input, _o.Width, _o.Height);
        }
        _frame->pts = _frames++;
        // -force_key_frames expr:gte(t,n_forced*N): the first frame at or after each multiple is a keyframe.
        var forced = AVPictureType.AV_PICTURE_TYPE_NONE;
        if (_o.KeyframeEvery is { } every)
        {
            double t = _frame->pts * _o.FrameRate.Den / (double)_o.FrameRate.Num;
            if (t >= _forced * every.TotalSeconds)
            {
                forced = AVPictureType.AV_PICTURE_TYPE_I;
                _forced++;
            }
        }
        _video!.Send(_frame, forced);
    }

    /// <summary>Encodes audio for the track: interleaved float32, any count.</summary>
    /// <param name="samples">A whole number of instants at the track's rate and channels.</param>
    /// <exception cref="InvalidOperationException">There's no audio track (<see cref="VideoWriterOptions.Audio"/>).</exception>
    public void WriteAudio(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var a = _o.Audio ?? throw new InvalidOperationException("This writer has no audio track: set VideoWriterOptions.Audio.");
        if (samples.Length % a.Channels != 0)
            throw new ArgumentException($"A whole number of instants: a multiple of {a.Channels} samples.", nameof(samples));
        if (samples.IsEmpty)
            return;
        if (!_started)
            _pendingAudio.Add(samples.ToArray());
        else
            SendAudio(samples);
    }

    void SendAudio(ReadOnlySpan<float> samples)
    {
        var a = _o.Audio!;
        Samples.Fill(_samples, samples, a.SampleRate, a.Channels, _written);
        _written += _samples->nb_samples;
        _audio!.Send(_samples);
    }

    /// <summary>Flushes the encoders and finishes the file (its trailer); then frees everything.</summary>
    /// <exception cref="FrameReaderException">The last packets or the trailer couldn't be written.</exception>
    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        try
        {
            if (_frame != null)
            {
                _video!.Send(null);
                _audio?.Send(null);
                Encoder.Check(ffmpeg.av_write_trailer(_fmt), "write the trailer");
            }
        }
        finally
        {
            Free();
        }
    }

    void Free()
    {
        _video?.Dispose();
        _audio?.Dispose();
        _video = _audio = null;
        Output.Free(ref _fmt);
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_samples != null) { var f = _samples; ffmpeg.av_frame_free(&f); _samples = null; }
    }
}
