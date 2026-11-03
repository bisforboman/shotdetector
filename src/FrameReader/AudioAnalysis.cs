using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What <see cref="AudioAnalysis"/> counts as silence: <see cref="SilenceFinder"/>'s (silencedetect's) options.</summary>
public sealed record SilenceOptions
{
    /// <summary>The level a sample must be under, in dBFS (silencedetect's <c>n</c>).</summary>
    public double Level { get; init; } = -60;

    /// <summary>The shortest silence (silencedetect's <c>d</c>).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Which analyses <see cref="AudioAnalysis.Run"/> runs on one decode: each one given its options runs.</summary>
public sealed record AudioAnalysisOptions
{
    /// <summary>Reference tone, as <see cref="FrameReader.ReferenceTone.Find"/>; null: not looked for (<c>new()</c> for its defaults).</summary>
    public ReferenceToneOptions? ReferenceTone { get; init; }

    /// <summary>Waveform peaks, as <see cref="WaveformData.Read"/>; null: none.</summary>
    public WaveformOptions? Waveform { get; init; }

    /// <summary>Loudness, as <see cref="FrameReader.Loudness.Measure"/>: ebur128 over the decoded audio, untouched by the reader's rate and channels.</summary>
    public bool Loudness { get; init; }

    /// <summary>Silences, as a <see cref="SilenceFinder"/> fed the reader's chunks; null: not looked for.</summary>
    public SilenceOptions? Silence { get; init; }

    /// <summary>
    /// The reader's chunks, in order, as <see cref="AudioReader.TryRead"/> gives them, for the caller's own analysis on
    /// the same decode; each is valid only during the call.
    /// </summary>
    public Action<AudioChunk>? Chunks { get; init; }

    /// <summary>Receives the pass's progress (<see cref="MediaProgress"/>): about 10 times a second, and once at the end.</summary>
    public IProgress<MediaProgress>? Progress { get; init; }
}

/// <summary>What <see cref="AudioAnalysis.Run"/> found: each result null when its analysis wasn't asked for.</summary>
/// <param name="ReferenceTone">The tone segments, as <see cref="FrameReader.ReferenceTone.Find"/> returns them.</param>
/// <param name="Waveform">The waveform, as <see cref="WaveformData.Read"/> returns it.</param>
/// <param name="Loudness">The loudness, as <see cref="FrameReader.Loudness.Measure"/> returns it.</param>
/// <param name="Silences">The silences, in samples at <paramref name="SampleRate"/> from where the reader was.</param>
/// <param name="SampleRate">The reader's output rate: for <see cref="SilenceSpan.Start"/> and <see cref="SilenceSpan.End"/>.</param>
public sealed record AudioAnalysisResult(
    IReadOnlyList<ToneSegment>? ReferenceTone,
    WaveformData? Waveform,
    LoudnessResult? Loudness,
    IReadOnlyList<SilenceSpan>? Silences,
    int SampleRate);

/// <summary>
/// Several audio analyses on one decode: reference tone, waveform, loudness and silences (and the caller's own, through
/// <see cref="AudioAnalysisOptions.Chunks"/>) over the rest of one <see cref="AudioReader"/>, each result the one its
/// own call gives on a reader opened with the same options, to the bit. Each decoded frame goes once into the loudness
/// graph (the decoded audio, as <see cref="FrameReader.Loudness.Measure"/> reads it) and once through the reader's
/// converter (its rate and channels, as <see cref="AudioReader.TryRead"/> gives them) to the others. A long programme's
/// audio is then decoded once instead of once per analysis (docs/decisions/2026-11-03-audio-analyses-on-one-decode.md).
/// </summary>
/// <example>
/// <code>
/// using var audio = new AudioReader("programme.mxf");
/// var r = AudioAnalysis.Run(audio, new()
/// {
///     ReferenceTone = new() { SearchDuration = TimeSpan.FromMinutes(3) },
///     Waveform = new() { PixelsPerSecond = 100 },
///     Loudness = true,
///     Silence = new() { Level = -50, Duration = TimeSpan.FromSeconds(1) },
/// });
/// Console.WriteLine($"{r.Loudness!.Value.Integrated:0.0} LUFS, {r.Silences!.Count} silences");
/// </code>
/// </example>
public static class AudioAnalysis
{
    /// <summary>Reads the rest of <paramref name="audio"/> once and runs every analysis <paramref name="options"/> asks for.</summary>
    /// <param name="audio">The audio; read to the end (or as far as the analyses need), not disposed. Seek it first for a range's start.</param>
    /// <param name="options">The analyses; at least one.</param>
    /// <param name="cancellationToken">Stops between decoded frames.</param>
    /// <returns>The results; nothing partial on a failure.</returns>
    /// <exception cref="ArgumentException">No analysis is asked for, or an analysis's options are invalid (as its own call says).</exception>
    /// <exception cref="FrameReaderException">
    /// Decoding or filtering failed, ebur128 isn't in these libraries (before reading), or loudness was asked for and
    /// there were no samples.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static unsafe AudioAnalysisResult Run(AudioReader audio, AudioAnalysisOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        if (options is { ReferenceTone: null, Waveform: null, Loudness: false, Silence: null, Chunks: null })
            throw new ArgumentException("No analysis is asked for.", nameof(options));

        // Each analysis made before reading, so bad options fail at once, as their own calls do.
        var tone = options.ReferenceTone is { } t ? new ReferenceToneFinder(audio.SampleRate, audio.Channels, t, audio.Duration) : null;
        var waveform = options.Waveform is { } w
            ? new WaveformBuilder(audio.SampleRate, audio.Channels, w, audio.Duration, integerSamples: !audio.DecodesFloat)
            : null;
        var silence = options.Silence is { } s ? new SilenceFinder(audio.SampleRate, audio.Channels, s.Level, s.Duration) : null;
        var chunks = options.Chunks;
        if (options.Loudness && ffmpeg.avfilter_get_by_name("ebur128") == null)
            throw new FrameReaderException(FrameReaderError.InvalidInput, "These FFmpeg libraries leave out the ebur128 filter that loudness needs.");
        using var loudness = options.Loudness ? new AudioFilter(audio, FrameReader.Loudness.Graph) : null;
        bool converts = tone is not null || waveform is not null || silence is not null || chunks is not null;

        // Tone with a search duration, alone: decoding stops where its own call stops.
        bool OnlyToneLeft() => tone is { IsComplete: true } && waveform is null && silence is null && chunks is null && loudness is null;
        var progress = new ProgressReporter(options.Progress, audio.Duration);

        void Give(AudioChunk chunk)
        {
            if (tone is { IsComplete: false })
                tone.Add(chunk.Samples, chunk.Time);
            waveform?.Add(chunk.Samples, chunk.Time);
            silence?.Add(chunk.Samples);
            chunks?.Invoke(chunk);
        }

        while (!OnlyToneLeft())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!audio.TryDecode(out int skip, out long position))
            {
                if (converts && audio.ConvertRest(out var rest))
                    Give(rest);
                break;
            }

            var frame = audio.Decoded;
            if (loudness is not null)
            {
                loudness.Add(frame, skip, position);
                while (loudness.Receive(out _))
                {
                }
            }

            if (converts && audio.ConvertDecoded(skip, position, out var chunk))
                Give(chunk);
            progress.Report(audio.Origin + TimeSpan.FromSeconds(position / (double)frame->sample_rate));
        }

        LoudnessResult? measured = null;
        if (loudness is not null)
        {
            loudness.End();
            while (loudness.Receive(out _))
            {
            }

            measured = FrameReader.Loudness.Result(loudness);
        }

        progress.Done();
        return new AudioAnalysisResult(tone?.Finish(), waveform?.Finish(), measured, silence?.Finish(), audio.SampleRate);
    }

    /// <summary>As <see cref="Run"/>, off the calling thread.</summary>
    public static Task<AudioAnalysisResult> RunAsync(AudioReader audio, AudioAnalysisOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(audio, options, cancellationToken), cancellationToken);
}
