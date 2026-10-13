namespace FrameReader;

/// <summary>What <see cref="Video.Transcode"/> writes.</summary>
public sealed record VideoTranscodeOptions
{
    /// <summary>The video encoder (<c>libx264</c> from libraries of your own, <c>libopenh264</c>, <c>mpeg4</c>, ...); null: the container's usual one.</summary>
    public string? Encoder { get; init; }

    /// <summary>The encoded pixel format (<c>yuv420p</c> for players); null: the encoder's nearest to the input's.</summary>
    public string? PixelFormat { get; init; } = "yuv420p";

    /// <summary>libx264's constant rate factor; null: the encoder's default.</summary>
    public double? Crf { get; init; }

    /// <summary>The video bit rate in bits per second; null: the encoder's default.</summary>
    public long? BitRate { get; init; }

    /// <summary>The encoder's preset (libx264's <c>veryfast</c>, ...); null: its default.</summary>
    public string? Preset { get; init; }

    /// <summary>The longest gap between keyframes, in frames (ffmpeg's <c>-g</c>); null: the encoder's default.</summary>
    public int? Gop { get; init; }

    /// <summary>The shortest gap between keyframes, in frames (<c>-keyint_min</c>); null: the encoder's default.</summary>
    public int? KeyintMin { get; init; }

    /// <summary>A keyframe at least this often (ffmpeg's <c>-force_key_frames expr:gte(t,n_forced*T)</c>): DASH segments of this length.</summary>
    public TimeSpan? KeyframeEvery { get; init; }

    /// <summary>Other encoder options by name, as ffmpeg's <c>-OPTION value</c> for the encoder.</summary>
    public IReadOnlyDictionary<string, string>? EncoderOptions { get; init; }

    /// <summary>
    /// Deinterlace with yadif (ffmpeg's <c>-vf yadif</c>): true, false, or null (the default) when the video stream is
    /// flagged interlaced, so an interlaced source doesn't stay interlaced in the output.
    /// </summary>
    public bool? Deinterlace { get; init; }

    /// <summary>Keep the input's audio (its first audio stream, re-encoded); false: video only.</summary>
    public bool Audio { get; init; } = true;

    /// <summary>The audio encoder (<c>aac</c>, ...); null: the container's usual one.</summary>
    public string? AudioEncoder { get; init; }

    /// <summary>The audio bit rate in bits per second; null: the encoder's default.</summary>
    public long? AudioBitRate { get; init; }
}

/// <summary>Whole-file video jobs.</summary>
public static class Video
{
    /// <summary>
    /// Re-encodes <paramref name="input"/>'s video (and its first audio stream) into <paramref name="output"/> in one call,
    /// as <c>ffmpeg -i IN [-vf yadif] -c:v E -pix_fmt yuv420p -g G -force_key_frames ... OUT</c>: for making a source
    /// that isn't ready for DASH (codec, container, pixel format, interlacing) ready for <see cref="Remux.Copy"/>. The
    /// output's frame rate is the input's average rate and its frames are upright (the display rotation applied). H.264
    /// needs an encoder your libraries have (<c>libx264</c> or <c>libopenh264</c>): ShotDetector.Native has neither,
    /// for licensing; its <c>mpeg4</c> or <c>mpeg2video</c> work for tests.
    /// </summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The output file; its extension picks the container.</param>
    /// <param name="options">The encoders and their settings.</param>
    /// <param name="cancellationToken">Stops the transcode.</param>
    /// <exception cref="FrameReaderException">InvalidInput: no video stream, or an encoder or muxer the libraries lack.</exception>
    public static void Transcode(string input, string output, VideoTranscodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new VideoTranscodeOptions();
        var info = MediaProbe.Probe(input);
        var stream = info.Video ?? throw new FrameReaderException(FrameReaderError.InvalidInput, $"\"{input}\" has no video stream.");
        bool deinterlace = o.Deinterlace ?? stream.FieldOrder is not (FieldOrder.Progressive or FieldOrder.Unknown);
        var rate = stream.AverageFrameRate.Num > 0 ? stream.AverageFrameRate : stream.FrameRate;

        // Constant-rate input: its frames as decoded. Variable: converted to the average rate (fps), as the encoder
        // needs a constant one.
        bool variable = stream.AverageFrameRate.Num > 0 && stream.FrameRate.Num > 0 && stream.AverageFrameRate.Value != stream.FrameRate.Value;
        using var frames = new VideoFrameReader(input, new VideoFrameReaderOptions
        {
            FrameFormat = FrameFormat.Yuv420p,
            FrameRate = variable ? rate : null,
            Decoder = new FrameDecoderOptions { Deinterlace = deinterlace },
        });
        using var audio = o.Audio && info.HasAudio ? new AudioReader(input) : null;
        VideoWriter? writer = null;
        try
        {
            AudioChunk pending = default;
            bool hasPending = false, audioDone = audio is null;
            while (frames.TryRead(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer ??= new VideoWriter(output, new VideoWriterOptions
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    FrameRate = rate,
                    FrameFormat = FrameFormat.Yuv420p,
                    Encoder = o.Encoder,
                    PixelFormat = o.PixelFormat,
                    Crf = o.Crf,
                    BitRate = o.BitRate,
                    Preset = o.Preset,
                    Gop = o.Gop,
                    KeyintMin = o.KeyintMin,
                    KeyframeEvery = o.KeyframeEvery,
                    EncoderOptions = o.EncoderOptions,
                    Audio = audio is null ? null : new AudioTrackOptions
                    {
                        SampleRate = audio.SampleRate,
                        Channels = audio.Channels,
                        Encoder = o.AudioEncoder,
                        BitRate = o.AudioBitRate,
                    },
                });
                writer.Write(frame.Data);

                // The audio up to this frame's time, so the muxer interleaves without holding a film's worth.
                while (!audioDone)
                {
                    if (!hasPending)
                    {
                        if (!audio!.TryRead(out pending))
                        {
                            audioDone = true;
                            break;
                        }

                        hasPending = true;
                    }

                    if (pending.Time > frame.Time)
                        break;
                    writer.WriteAudio(pending.Samples);
                    hasPending = false;
                }
            }

            if (writer is null)
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"\"{input}\" has no video frames.");
            if (hasPending)
                writer.WriteAudio(pending.Samples);
            while (!audioDone && audio!.TryRead(out var rest))
                writer.WriteAudio(rest.Samples);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>As <see cref="Transcode"/>, off the calling thread.</summary>
    public static Task TranscodeAsync(string input, string output, VideoTranscodeOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Transcode(input, output, options, cancellationToken), cancellationToken);
}
