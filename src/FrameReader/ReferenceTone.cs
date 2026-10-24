namespace FrameReader;

/// <summary>What <see cref="ReferenceTone.Find"/> counts as reference tone.</summary>
public sealed record ReferenceToneOptions
{
    /// <summary>The tone's frequency in Hz; about ±15 Hz of drift still counts.</summary>
    public double Frequency { get; init; } = 1000;

    /// <summary>The shortest run reported: line-up tone lasts 30-60 s, beeps and held notes far less.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest break inside one segment: line-up conventions (EBU stereo line-up, GLITS) cut a channel for a
    /// fraction of a second every few seconds, and a break in all channels up to this long is bridged too.
    /// </summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How much audio to search from where the reader is, then stop decoding (line-up tone is at the start: 180 s keeps
    /// a feature film's search cheap); null: to the end. A segment still running there ends there.
    /// </summary>
    public TimeSpan? SearchDuration { get; init; }

    /// <summary>Receives progress (<see cref="MediaProgress"/>): about 10 times a second, and once at the end.</summary>
    public IProgress<MediaProgress>? Progress { get; init; }
}

/// <summary>A run of reference tone.</summary>
/// <param name="Start">Its first sample's time, to 20 ms.</param>
/// <param name="End">Just after its last sample, to 20 ms.</param>
/// <param name="Level">Its level in dBFS, a full-scale sine being 0 (EBU line-up is -18, SMPTE -20).</param>
public readonly record struct ToneSegment(TimeSpan Start, TimeSpan End, double Level);

/// <summary>
/// Finds reference tone (line-up or alignment tone, the steady 1 kHz sine before or after programme material) in
/// audio. In 20 ms windows, a channel is tone when at least 90% of its energy is at the frequency and it's louder than
/// -40 dBFS; a window is tone when any channel is (so one channel cut for line-up identification still counts).
/// Consecutive tone windows at a steady level (±1 dB) form a segment, bridging breaks up to
/// <see cref="ReferenceToneOptions.MaxGap"/>; segments of at least <see cref="ReferenceToneOptions.MinDuration"/> are
/// reported. Music fails the 90% (harmonics, other notes) or the steady level (decay, expression). For samples decoded
/// elsewhere (no FFmpeg), <see cref="ReferenceToneFinder"/> runs the same search a chunk at a time.
/// </summary>
public static class ReferenceTone
{
    /// <summary>
    /// Reads the rest of <paramref name="audio"/> (at its rate and channels), or <see cref="ReferenceToneOptions.SearchDuration"/>
    /// of it, and returns its tone segments.
    /// </summary>
    /// <param name="audio">The samples; read to the end (or the search duration), not disposed.</param>
    /// <param name="options">Frequency, minimum length, longest break; null: 1 kHz, 5 s, 1 s.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <exception cref="FrameReaderException">Decoding failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<ToneSegment> Find(AudioReader audio, ReferenceToneOptions? options = null, CancellationToken cancellationToken = default)
    {
        var finder = new ReferenceToneFinder(audio.SampleRate, audio.Channels, options, audio.Duration);
        while (!finder.IsComplete && audio.TryRead(out var chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            finder.Add(chunk.Samples, chunk.Time);
        }

        return finder.Finish();
    }

    /// <summary>As <see cref="Find"/>, off the calling thread.</summary>
    public static Task<IReadOnlyList<ToneSegment>> FindAsync(AudioReader audio, ReferenceToneOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(audio, options, cancellationToken), cancellationToken);
}
