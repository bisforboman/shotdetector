namespace FrameReader;

/// <summary>What <see cref="ChannelMeter"/> counts as silent and as clipped.</summary>
public sealed record ChannelMeterOptions
{
    /// <summary>The level a channel's sample must be under to be silent, in dBFS (silencedetect's n). -60 dB by default.</summary>
    public double SilenceLevel { get; init; } = -60;

    /// <summary>A channel's shortest silence (silencedetect's d). 2 s by default.</summary>
    public TimeSpan SilenceDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How many samples in a row at full scale are a clipped run. 3 by default.</summary>
    public int ClipRun { get; init; } = 3;
}

/// <summary>
/// One channel's measures (<see cref="ChannelMeter"/>): its silences, its RMS level, its samples at full scale and the
/// runs of them.
/// </summary>
/// <param name="Silences">The channel's silences alone, as silencedetect finds them in it (<c>pan=mono|c0=cN,silencedetect</c>), in samples from the first.</param>
/// <param name="RmsLevel">The RMS level in dBFS, as astats' RMS level; negative infinity for digital silence or no samples.</param>
/// <param name="FullScaleSamples">The samples at full scale (<see cref="ChannelMeter.FullScale"/> or beyond, either way).</param>
/// <param name="ClippedRuns">The runs of <see cref="ChannelMeterOptions.ClipRun"/> or more samples in a row at full scale.</param>
public sealed record ChannelStats(IReadOnlyList<SilenceSpan> Silences, double RmsLevel, long FullScaleSamples, int ClippedRuns);

/// <summary>What <see cref="ChannelMeter"/> measured: each channel's, and the first two's phase.</summary>
/// <param name="Samples">The instants measured (samples per channel).</param>
/// <param name="Channels">Each channel's measures, in the samples' channel order.</param>
/// <param name="Phase">
/// The first two channels' mean phase correlation, aphasemeter's (the mean over the instants of 2·L·R / (L² + R²), 1
/// where both are 0): 1 in phase, 0 unrelated, -1 inverted; null for one channel or no samples.
/// </param>
public sealed record ChannelMeasures(long Samples, IReadOnlyList<ChannelStats> Channels, double? Phase);

/// <summary>
/// A track's channels one by one, from samples the caller decodes (interleaved floats, full scale ±1), a chunk at a time,
/// with nothing of FFmpeg: each channel's silences (<see cref="SilenceFinder"/> on that channel alone: silencedetect's),
/// its RMS level (astats'), its samples at full scale and the runs of them (clipping), and the first two channels' phase
/// correlation (aphasemeter's arithmetic, in single precision as it does it). For a delivery's channel faults: a dead or
/// silent channel, a channel out of phase, clipping, dual mono. Not thread-safe: one caller feeds it.
/// </summary>
public sealed class ChannelMeter
{
    /// <summary>A sample at or beyond this magnitude is at full scale: a 16-bit sample's largest (32767/32768), and up.</summary>
    public const float FullScale = 32767f / 32768f;

    readonly int _channels, _clipRun;
    readonly SilenceFinder[] _silences;
    readonly double[] _squares;
    readonly int[] _run, _clipped;
    readonly long[] _full;
    readonly float[] _one = new float[8192];
    double _phase;
    long _samples;
    ChannelMeasures? _finished;

    /// <summary>A meter of <paramref name="channels"/> interleaved channels at <paramref name="sampleRate"/> Hz.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A rate or channel count that isn't positive, or a clip run under 1.</exception>
    public ChannelMeter(int sampleRate, int channels, ChannelMeterOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var o = options ?? new ChannelMeterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(o.ClipRun, 1, nameof(options));
        (_channels, _clipRun) = (channels, o.ClipRun);
        _silences = [.. Enumerable.Range(0, channels).Select(_ => new SilenceFinder(sampleRate, 1, o.SilenceLevel, o.SilenceDuration))];
        _squares = new double[channels];
        _run = new int[channels];
        _clipped = new int[channels];
        _full = new long[channels];
    }

    /// <summary>Adds interleaved samples: whole instants, every channel each.</summary>
    /// <exception cref="ArgumentException">Not a whole number of instants.</exception>
    /// <exception cref="InvalidOperationException">After <see cref="Finish"/>.</exception>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        if (_finished is not null)
            throw new InvalidOperationException("The meter has finished.");
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException("Not a whole number of instants.", nameof(interleaved));
        int instants = interleaved.Length / _channels;
        for (int c = 0; c < _channels; c++)
        {
            for (int start = 0; start < instants; start += _one.Length)
            {
                int count = Math.Min(_one.Length, instants - start);
                for (int i = 0; i < count; i++)
                    _one[i] = interleaved[((start + i) * _channels) + c];
                _silences[c].Add(_one.AsSpan(0, count));
            }
        }

        for (int i = 0; i < instants; i++)
        {
            var instant = interleaved.Slice(i * _channels, _channels);
            for (int c = 0; c < _channels; c++)
            {
                float x = instant[c];
                _squares[c] += (double)x * x;
                if (Math.Abs(x) >= FullScale)
                {
                    _full[c]++;
                    if (++_run[c] == _clipRun)
                        _clipped[c]++;
                }
                else
                {
                    _run[c] = 0;
                }
            }

            if (_channels >= 2)
            {
                // aphasemeter's own arithmetic, in float as it does it; 0/0 (both silent) is in phase.
                float l = instant[0], r = instant[1];
                float f = l * r / ((l * l) + (r * r)) * 2;
                _phase += float.IsNaN(f) ? 1 : f;
            }
        }

        _samples += instants;
    }

    /// <summary>What was measured. No samples can be added after.</summary>
    public ChannelMeasures Finish()
    {
        if (_finished is not null)
            return _finished;
        var channels = new ChannelStats[_channels];
        for (int c = 0; c < _channels; c++)
        {
            double rms = _samples == 0 || _squares[c] == 0 ? double.NegativeInfinity : 10 * Math.Log10(_squares[c] / _samples);
            channels[c] = new ChannelStats(_silences[c].Finish(), rms, _full[c], _clipped[c]);
        }

        return _finished = new ChannelMeasures(_samples, channels, _channels >= 2 && _samples > 0 ? _phase / _samples : null);
    }
}
