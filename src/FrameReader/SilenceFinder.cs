namespace FrameReader;

/// <summary>A silence <see cref="SilenceFinder"/> found: from its first silent instant up to the first that isn't.</summary>
/// <param name="StartSample">The first silent instant, counted per channel from the first sample given (silencedetect's <c>silence_start</c>).</param>
/// <param name="EndSample">The first instant after it that isn't silent, or the end of the samples (silencedetect's <c>silence_end</c>).</param>
public readonly record struct SilenceSpan(long StartSample, long EndSample)
{
    /// <summary>The span's start in time, at <paramref name="sampleRate"/>, from the first sample given.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <returns>The start, rounded down to a tick.</returns>
    public TimeSpan Start(int sampleRate) => Time(StartSample, sampleRate);

    /// <summary>The span's end in time, at <paramref name="sampleRate"/>, from the first sample given.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <returns>The end, rounded down to a tick.</returns>
    public TimeSpan End(int sampleRate) => Time(EndSample, sampleRate);

    static TimeSpan Time(long sample, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return TimeSpan.FromTicks((long)(sample * (decimal)TimeSpan.TicksPerSecond / sampleRate));
    }
}

/// <summary>
/// Silences in samples the caller decodes, a chunk at a time, as FFmpeg's <c>silencedetect</c> (default mode, not
/// <c>mono</c>) finds them in float samples: the same spans, to the sample. Touches nothing of FFmpeg; holds only a
/// count and the spans found. Not thread-safe: one caller feeds it.
/// </summary>
/// <remarks>
/// silencedetect's own counting, not "every channel silent for the duration": it counts the samples below the level
/// one channel after another (strictly, in single precision: <c>s &lt; n &amp;&amp; s &gt; -n</c>), any sample at or above it
/// starting the count again, and a silence begins once the count reaches the duration's instants times the channels.
/// Its start is then that instant plus one, less the duration; so with several channels a silence can start an instant
/// after the first all-silent one, or last an instant less than the duration, as FFmpeg reports it. A silence still
/// running at the end ends at the end of the samples. silencedetect compares integer samples (s16, s32) in their own
/// format, against the level times the format's maximum truncated; this finder takes floats, which is what FFmpeg's
/// decoders give for AAC, MP3, Opus and float PCM.
/// </remarks>
/// <example>
/// <code>
/// var finder = new SilenceFinder(48000, 2); // -60 dB, 2 s: silencedetect's defaults
/// foreach (var samples in decoded)
///     finder.Add(samples);
/// foreach (var s in finder.Finish())
///     Console.WriteLine($"{s.Start(48000)} to {s.End(48000)}");
/// </code>
/// </example>
public sealed class SilenceFinder
{
    readonly int _channels;
    readonly float _noise;
    readonly long _notify, _instants;
    readonly List<SilenceSpan> _found = [];
    long _position; // samples (not instants) seen
    long _count;
    long _start = -1;
    bool _finished;

    /// <summary>Starts a search.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <param name="channels">Channels in the interleaved samples.</param>
    /// <param name="level">The level a sample must be under, in dBFS (silencedetect's <c>n</c>: -60 dB, an amplitude of 0.001).</param>
    /// <param name="duration">The shortest silence (silencedetect's <c>d</c>, in whole microseconds as FFmpeg keeps it); null: 2 s.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> or <paramref name="channels"/> isn't positive, or <paramref name="duration"/> is negative.</exception>
    public SilenceFinder(int sampleRate, int channels, double level = -60, TimeSpan? duration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var d = duration ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThan(d, TimeSpan.Zero, nameof(duration));
        _channels = channels;

        // The option is a double compared as the samples' type: float.
        _noise = (float)Math.Pow(10, level / 20);

        // av_rescale(microseconds, rate, 1000000): rounded to the nearest instant, halves away from zero.
        long microseconds = d.Ticks / (TimeSpan.TicksPerSecond / 1000000);
        _instants = (long)((((Int128)microseconds * sampleRate) + 500000) / 1000000);
        _notify = _instants * channels;
    }

    /// <summary>Searches the next samples. Chunks may be of any size; only whole instants (all channels) per call.</summary>
    /// <param name="interleaved">Float samples, full scale ±1, interleaved (channel 0, 1, ... for each instant).</param>
    /// <exception cref="ArgumentException"><paramref name="interleaved"/> isn't a whole number of instants.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish"/> was called.</exception>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        ThrowIfFinished();
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException($"The samples aren't a whole number of instants of {_channels} channels.", nameof(interleaved));
        float noise = _noise;
        for (int i = 0; i < interleaved.Length; i++, _position++)
        {
            float s = interleaved[i];
            if (s < noise && s > -noise)
            {
                // silencedetect's update(): counted only until the silence starts.
                if (_start < 0 && ++_count >= _notify)
                    _start = (_position / _channels) + 1 - _instants;
            }
            else
            {
                if (_start >= 0)
                    _found.Add(new SilenceSpan(_start, _position / _channels));
                _count = 0;
                _start = -1;
            }
        }
    }

    /// <summary>Ends the search: a silence still running ends at the end of the samples.</summary>
    /// <returns>The silences, in order, in instants from the first sample given.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public IReadOnlyList<SilenceSpan> Finish()
    {
        ThrowIfFinished();
        _finished = true;
        if (_start >= 0)
            _found.Add(new SilenceSpan(_start, _position / _channels));
        return _found;
    }

    void ThrowIfFinished()
    {
        if (_finished)
            throw new InvalidOperationException("Finish was called: start a new finder.");
    }
}
