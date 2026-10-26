using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What <see cref="Audio.Convert"/> does to the audio, and how it writes it.</summary>
public sealed record AudioConvertOptions
{
    /// <summary>An ffmpeg audio filter graph (<c>-af</c>), e.g. <c>equalizer=f=1000:t=q:w=8:g=-40</c>; null: none.</summary>
    public string? Filter { get; init; }

    /// <summary>FFmpeg's encoder name (<c>libmp3lame</c>, <c>aac</c>, <c>pcm_s16le</c>); null: the container's usual one, from the file name.</summary>
    public string? Encoder { get; init; }

    /// <summary>Bits per second for lossy encoders (<c>-b:a</c>); null: the encoder's default.</summary>
    public long? BitRate { get; init; }

    /// <summary>FFmpeg's muxer name; null: from the file name.</summary>
    public string? Container { get; init; }

    /// <summary>ffmpeg's <c>-fflags +bitexact -flags:a +bitexact</c>: no version tags or random IDs.</summary>
    public bool Bitexact { get; init; }

    /// <summary>FFmpeg demuxer options, as ffmpeg's input options without the dash; a name nothing takes is an <see cref="ArgumentException"/>.</summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }

    /// <summary>Receives progress (<see cref="MediaProgress"/>): about 10 times a second, and once at the end.</summary>
    public IProgress<MediaProgress>? Progress { get; init; }
}

/// <summary>Whole-file audio jobs, as ffmpeg's command line does them.</summary>
public static unsafe class Audio
{
    /// <summary>
    /// The file <c>ffmpeg -i INPUT -vn [-af FILTER] [-c:a ENCODER] [-b:a RATE] OUTPUT</c> writes: the input's best audio
    /// stream decoded in its own sample format, through the filter graph, converted to the nearest sample format, rate and
    /// channel layout the encoder takes (as ffmpeg inserts it; a downmix from integer samples is normalized, from float
    /// samples it isn't, as there), encoded, with the input's metadata and chapters.
    /// </summary>
    /// <param name="input">A file or URL FFmpeg can read.</param>
    /// <param name="output">The file to write; its name picks the container and encoder unless the options do.</param>
    /// <param name="options">Filter, encoder, bit rate; null: plain conversion.</param>
    /// <param name="cancellationToken">Stops between frames, and interrupts opening the input and reads where FFmpeg waits (a
    /// network read) (<see cref="OperationCanceledException"/>; the output is left unfinished).</param>
    /// <exception cref="FrameReaderException">The input can't be read or has no audio
    /// (<see cref="FrameReaderError.InvalidInput"/>), the libraries lack the encoder, a filter or the muxer, or writing failed.</exception>
    public static void Convert(string input, string output, AudioConvertOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new AudioConvertOptions();
        using var reader = new AudioReader(input, new AudioReaderOptions { InputOptions = o.InputOptions, CancellationToken = cancellationToken });
        AVFormatContext* fmt = null;
        Encoder? encoder = null;
        AVFrame* feed = ffmpeg.av_frame_alloc();
        var progress = new ProgressReporter(o.Progress, reader.Duration);
        try
        {
            while (reader.TryDecode(out int skip, out long position))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = reader.Decoded;
                progress.Report(TimeSpan.FromSeconds(position / (double)frame->sample_rate));
                if (skip >= frame->nb_samples)
                    continue;
                if (encoder is null)
                {
                    fmt = Output.Create(output, o.Container, o.Bitexact);
                    var codec = FrameReader.Encoder.Find(fmt, output, o.Encoder, AVMediaType.AVMEDIA_TYPE_AUDIO, "audio");
                    encoder = FrameReader.Encoder.AudioFrom(fmt, codec, frame, o.Filter, o.BitRate, o.Bitexact);

                    // ffmpeg's default mapping copies the input's metadata and chapters, and the stream's tags.
                    Remux.CopyMetadataAndChapters(reader.Format, fmt);
                    ffmpeg.av_dict_copy(&encoder.Stream->metadata, reader.Format->streams[reader.StreamIndex]->metadata, ffmpeg.AV_DICT_DONT_OVERWRITE);
                    Output.Start(fmt, output);
                }

                Prepare(feed, frame, skip, position);
                encoder.Send(feed);
            }

            if (encoder is null)
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"\"{input}\" has no audio to convert.");
            encoder.Send(null);
            FrameReader.Encoder.Check(ffmpeg.av_write_trailer(fmt), "write the trailer");
            progress.Done();
        }
        finally
        {
            encoder?.Dispose();
            if (fmt != null)
                Output.Free(ref fmt);
            ffmpeg.av_frame_free(&feed);
        }
    }

    /// <summary>As <see cref="Convert"/>, off the calling thread.</summary>
    public static Task ConvertAsync(string input, string output, AudioConvertOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Convert(input, output, options, cancellationToken), cancellationToken);

    /// <summary>A decoded frame without its first <paramref name="skip"/> samples, at its position (as <see cref="AudioFilter"/> feeds it).</summary>
    static void Prepare(AVFrame* feed, AVFrame* frame, int skip, long position)
    {
        ffmpeg.av_frame_unref(feed);
        if (skip == 0)
        {
            FrameReader.Encoder.Check(ffmpeg.av_frame_ref(feed, frame), "filter");
        }
        else
        {
            feed->format = frame->format;
            feed->sample_rate = frame->sample_rate;
            feed->nb_samples = frame->nb_samples - skip;
            FrameReader.Encoder.Check(ffmpeg.av_channel_layout_copy(&feed->ch_layout, &frame->ch_layout), "filter");
            FrameReader.Encoder.Check(ffmpeg.av_frame_get_buffer(feed, 0), "filter");
            FrameReader.Encoder.Check(ffmpeg.av_samples_copy(feed->extended_data,
                frame->extended_data,
                0,
                skip,
                feed->nb_samples,
                frame->ch_layout.nb_channels,
                (AVSampleFormat)frame->format),
                "filter");
        }

        feed->pts = position;
    }
}
