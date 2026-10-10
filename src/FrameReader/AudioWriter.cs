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
    public string? Container { get; init; }

    /// <summary>
    /// Leave out what makes a file differ between FFmpeg versions (the Lavf/Lavc encoder tags, random IDs): ffmpeg's
    /// <c>-fflags +bitexact -flags:a +bitexact</c>. Off by default, so files carry the tags ffmpeg's own output does.
    /// </summary>
    public bool Bitexact { get; init; }
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
    Encoder? _encoder;
    AVFrame* _frame;
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
            FFmpegLibraries.Load();
            _fmt = Output.Create(path, options.Container, options.Bitexact);
            var codec = Encoder.Find(_fmt, path, options.Encoder, AVMediaType.AVMEDIA_TYPE_AUDIO, "audio");
            _encoder = Encoder.Audio(_fmt, codec, _rate, _channels, options.BitRate, options.Bitexact);
            Output.Start(_fmt, path);
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Free();
            throw;
        }
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
        Samples.Fill(_frame, samples, _rate, _channels, _written);
        _written += _frame->nb_samples;
        _encoder!.Send(_frame);
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
            if (_frame != null)
            {
                _encoder!.Send(null);
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
        _encoder?.Dispose();
        _encoder = null;
        Output.Free(ref _fmt);
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
    }
}

/// <summary>Float32 interleaved samples into an AVFrame.</summary>
internal static unsafe class Samples
{
    internal static void Fill(AVFrame* frame, ReadOnlySpan<float> samples, int rate, int channels, long pts)
    {
        ffmpeg.av_frame_unref(frame);
        frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        frame->sample_rate = rate;
        frame->nb_samples = samples.Length / channels;
        ffmpeg.av_channel_layout_default(&frame->ch_layout, channels);
        Encoder.Check(ffmpeg.av_frame_get_buffer(frame, 0), "frame buffer");
        samples.CopyTo(new Span<float>(frame->data[0], samples.Length));
        frame->pts = pts;
    }
}
