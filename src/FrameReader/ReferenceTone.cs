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
/// reported. Music fails the 90% (harmonics, other notes) or the steady level (decay, expression).
/// </summary>
public static class ReferenceTone
{
    const double WindowSeconds = 0.02, MinPurity = 0.9, MinLevel = -40, LevelTolerance = 1;

    /// <summary>Reads the rest of <paramref name="audio"/> (at its rate and channels) and returns its tone segments.</summary>
    /// <param name="audio">The samples; read to the end, not disposed.</param>
    /// <param name="options">Frequency, minimum length, longest break; null: 1 kHz, 5 s, 1 s.</param>
    /// <exception cref="FrameReaderException">Decoding failed.</exception>
    public static IReadOnlyList<ToneSegment> Find(AudioReader audio, ReferenceToneOptions? options = null)
    {
        var o = options ?? new ReferenceToneOptions();
        int rate = audio.SampleRate, channels = audio.Channels;
        int n = Math.Max(1, (int)Math.Round(rate * WindowSeconds));
        var w = new double[n];
        double sumW = 0, sumW2 = 0;
        for (int i = 0; i < n; i++)
        {
            w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n); // Hann
            (sumW, sumW2) = (sumW + w[i], sumW2 + w[i] * w[i]);
        }
        // A sine at the frequency: |X|^2 / windowed energy = sumW^2 / (2 sumW2); purity scales that to 1.
        double purityScale = 2 * sumW2 / (sumW * sumW);
        double coeff = 2 * Math.Cos(2 * Math.PI * o.Frequency / rate);
        long gapWindows = (long)(o.MaxGap.TotalSeconds / WindowSeconds), minWindows = (long)Math.Ceiling(o.MinDuration.TotalSeconds / WindowSeconds);
        double windowLength = (double)n / rate;

        var s1 = new double[channels];
        var s2 = new double[channels];
        var energy = new double[channels];
        var power = new double[channels];
        var segments = new List<ToneSegment>();
        TimeSpan? origin = null;
        long window = 0;
        int at = 0;
        long start = -1, last = -1;
        double level = 0;

        TimeSpan Time(long index) => origin!.Value + TimeSpan.FromSeconds(index * windowLength);
        void Close()
        {
            if (start >= 0 && last + 1 - start >= minWindows)
                segments.Add(new ToneSegment(Time(start), Time(last + 1), level));
            start = -1;
        }

        while (audio.TryRead(out var chunk))
        {
            origin ??= chunk.Time;
            var samples = chunk.Samples;
            for (int i = 0; i < chunk.Length; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double x = samples[i * channels + c], y = x * w[at];
                    double s = y + coeff * s1[c] - s2[c];
                    (s2[c], s1[c]) = (s1[c], s);
                    energy[c] += y * y;
                    power[c] += x * x;
                }
                if (++at < n)
                    continue;

                // The window's tone level: the loudest channel that is mostly the frequency.
                double? tone = null;
                for (int c = 0; c < channels; c++)
                {
                    double goertzel = s1[c] * s1[c] + s2[c] * s2[c] - coeff * s1[c] * s2[c];
                    double db = 10 * Math.Log10(2 * power[c] / n + 1e-30);
                    if (energy[c] > 0 && goertzel / energy[c] * purityScale >= MinPurity && db >= MinLevel && db > (tone ?? double.MinValue))
                        tone = db;
                    s1[c] = s2[c] = energy[c] = power[c] = 0;
                }
                at = 0;

                if (tone is { } t)
                {
                    if (start >= 0 && Math.Abs(t - level) <= LevelTolerance)
                        last = window;
                    // Off level: a break while within MaxGap, unless the run is one (perhaps partial) window so far.
                    else if (start < 0 || last == start || window - last - 1 > gapWindows)
                    {
                        Close();
                        (start, last, level) = (window, window, t);
                    }
                }
                else if (start >= 0 && window - last > gapWindows)
                    Close();
                window++;
            }
        }
        Close();
        return segments;
    }
}
