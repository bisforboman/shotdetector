namespace FrameReader;

/// <summary>
/// <see cref="WaveformData"/> from samples the caller decodes, a chunk at a time (a decoder of its own, as in a
/// browser): the same points <see cref="WaveformData.Read"/> gives for the same samples. Holds only the point in
/// progress and the points made (the result), never the track; touches nothing of FFmpeg. Not thread-safe: one caller
/// feeds it.
/// </summary>
/// <example>
/// <code>
/// var builder = new WaveformBuilder(48000, 2, new WaveformOptions { PixelsPerSecond = 50 });
/// foreach (var (samples, time) in decoded)
///     builder.Add(samples, time);
/// var waveform = builder.Finish();
/// </code>
/// </example>
public sealed class WaveformBuilder
{
    const int MaxChannels = 24; // audiowaveform's WaveformBuffer::MAX_CHANNELS

    readonly int _rate, _inChannels, _channels, _perPixel;
    readonly bool _fromFloat;
    readonly int[] _min, _max, _frame;
    readonly List<short> _data = [];
    readonly ProgressReporter _progress;
    int _count;
    bool _finished;

    /// <summary>Starts a waveform.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <param name="channels">Channels in the interleaved samples, at most 24 (audiowaveform's limit).</param>
    /// <param name="options">Points' size and channels, progress; null: 256 samples per point, channels averaged.</param>
    /// <param name="duration">The input's length when known, for progress reports' <see cref="MediaProgress.Duration"/>; null: positions only.</param>
    /// <param name="integerSamples">
    /// Whether the samples were decoded from integers (PCM, FLAC): each is then shifted down to 16 bits as libsndfile
    /// does. False (the default, lossy codecs such as AAC and float PCM): scaled by 32767 and truncated as audiowaveform
    /// does for float sources. <see cref="WaveformData.Read"/> takes this from the decoder.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> or <paramref name="channels"/> isn't positive.</exception>
    /// <exception cref="ArgumentException">Fewer than 2 samples per point, or more than 24 channels.</exception>
    public WaveformBuilder(int sampleRate, int channels, WaveformOptions? options = null, TimeSpan? duration = null, bool integerSamples = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var o = options ?? new WaveformOptions();
        _perPixel = o.PixelsPerSecond is { } pps
            ? (pps > 0 ? sampleRate / pps : throw new ArgumentException("PixelsPerSecond must be positive.", nameof(options)))
            : o.SamplesPerPixel;
        if (_perPixel < 2)
            throw new ArgumentException("A point needs at least 2 samples (audiowaveform's minimum zoom).", nameof(options));
        if (channels > MaxChannels)
            throw new ArgumentException($"audiowaveform handles at most {MaxChannels} channels.", nameof(channels));
        (_rate, _inChannels, _channels, _fromFloat) = (sampleRate, channels, o.SplitChannels ? channels : 1, !integerSamples);
        (_min, _max, _frame) = (new int[_channels], new int[_channels], new int[channels]);
        Array.Fill(_min, short.MaxValue);
        Array.Fill(_max, short.MinValue);
        _progress = new ProgressReporter(o.Progress, duration);
    }

    /// <summary>Adds the next samples. Chunks may be of any size (a point can span several); only whole instants (all channels) per call.</summary>
    /// <param name="interleaved">Float samples, full scale ±1, interleaved (channel 0, 1, ... for each instant).</param>
    /// <param name="time">The first sample's time: progress only (points follow from the samples counted).</param>
    /// <exception cref="ArgumentException"><paramref name="interleaved"/> isn't a whole number of instants.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish"/> was called.</exception>
    public void Add(ReadOnlySpan<float> interleaved, TimeSpan time)
    {
        ThrowIfFinished();
        if (interleaved.Length % _inChannels != 0)
            throw new ArgumentException($"The samples aren't a whole number of instants of {_inChannels} channels.", nameof(interleaved));
        _progress.Report(time);
        int inChannels = _inChannels, count = interleaved.Length / inChannels;
        var frame = _frame;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < inChannels; c++)
                frame[c] = WaveformData.ToShort(interleaved[i * inChannels + c], _fromFloat);
            if (_channels == 1)
            {
                // The channels' mean, truncated toward zero, as audiowaveform mixes them.
                int sum = 0;
                foreach (int s in frame)
                    sum += s;
                int sample = sum / inChannels;
                _min[0] = Math.Min(_min[0], sample);
                _max[0] = Math.Max(_max[0], sample);
            }
            else
            {
                for (int c = 0; c < _channels; c++)
                {
                    _min[c] = Math.Min(_min[c], frame[c]);
                    _max[c] = Math.Max(_max[c], frame[c]);
                }
            }

            if (++_count == _perPixel)
                Flush();
        }
    }

    /// <summary>Ends the waveform (a partial last point counts) and returns it; reports the last progress.</summary>
    /// <returns>The points.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public WaveformData Finish()
    {
        ThrowIfFinished();
        _finished = true;
        if (_count > 0)
            Flush(); // the last, partial point
        _progress.Done();
        return new WaveformData(_rate, _perPixel, _channels, [.. _data]);
    }

    void ThrowIfFinished()
    {
        if (_finished)
            throw new InvalidOperationException("Finish was called: start a new builder.");
    }

    void Flush()
    {
        for (int c = 0; c < _channels; c++)
        {
            _data.Add((short)_min[c]);
            _data.Add((short)_max[c]);
            _min[c] = short.MaxValue;
            _max[c] = short.MinValue;
        }

        _count = 0;
    }
}
