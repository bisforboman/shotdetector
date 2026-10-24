namespace FrameReader;

/// <summary>
/// <see cref="ReferenceTone"/>'s search on samples from the caller, a chunk at a time (a decoder of its own, as in a
/// browser): the same segments <see cref="ReferenceTone.Find"/> gives for the same samples. Holds only the 20 ms window
/// in progress and the segments found, never the track; touches nothing of FFmpeg. Not thread-safe: one caller feeds it.
/// </summary>
/// <example>
/// <code>
/// var finder = new ReferenceToneFinder(48000, 2, new ReferenceToneOptions { SearchDuration = TimeSpan.FromMinutes(3) });
/// foreach (var (samples, time) in decoded)
///     if (!finder.Add(samples, time))
///         break; // the search duration is read: stop decoding
/// var segments = finder.Finish();
/// </code>
/// </example>
public sealed class ReferenceToneFinder
{
    const double WindowSeconds = 0.02, MinPurity = 0.9, MinLevel = -40, LevelTolerance = 1;

    readonly int _channels, _n;
    readonly double[] _w, _s1, _s2, _energy, _power;
    readonly double _purityScale, _coeff, _windowLength;
    readonly long _gapWindows, _minWindows, _maxWindows;
    readonly ProgressReporter _progress;
    readonly List<ToneSegment> _segments = [];
    TimeSpan? _origin;
    long _window, _start = -1, _last = -1;
    int _at;
    double _level;
    bool _finished;

    /// <summary>Starts a search.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <param name="channels">Channels in the interleaved samples.</param>
    /// <param name="options">Frequency, minimum length, longest break, search duration, progress; null: 1 kHz, 5 s, 1 s.</param>
    /// <param name="duration">
    /// The input's length when known: progress reports carry the shorter of it and
    /// <see cref="ReferenceToneOptions.SearchDuration"/> as their <see cref="MediaProgress.Duration"/>. Null: the search
    /// duration, or none (positions only, <see cref="MediaProgress.Fraction"/> null).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> or <paramref name="channels"/> isn't positive.</exception>
    /// <exception cref="ArgumentException"><see cref="ReferenceToneOptions.SearchDuration"/> isn't positive.</exception>
    public ReferenceToneFinder(int sampleRate, int channels, ReferenceToneOptions? options = null, TimeSpan? duration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var o = options ?? new ReferenceToneOptions();
        if (o.SearchDuration <= TimeSpan.Zero)
            throw new ArgumentException("SearchDuration is positive when set.", nameof(options));
        _channels = channels;
        int n = _n = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        _w = new double[n];
        double sumW = 0, sumW2 = 0;
        for (int i = 0; i < n; i++)
        {
            _w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n); // Hann
            (sumW, sumW2) = (sumW + _w[i], sumW2 + _w[i] * _w[i]);
        }

        // A sine at the frequency: |X|^2 / windowed energy = sumW^2 / (2 sumW2); purity scales that to 1.
        _purityScale = 2 * sumW2 / (sumW * sumW);
        _coeff = 2 * Math.Cos(2 * Math.PI * o.Frequency / sampleRate);
        (_gapWindows, _minWindows) = ((long)(o.MaxGap.TotalSeconds / WindowSeconds), (long)Math.Ceiling(o.MinDuration.TotalSeconds / WindowSeconds));
        _windowLength = (double)n / sampleRate;
        _maxWindows = o.SearchDuration is { } d ? (long)Math.Ceiling(d.TotalSeconds / _windowLength) : long.MaxValue;
        (_s1, _s2, _energy, _power) = (new double[channels], new double[channels], new double[channels], new double[channels]);

        // How much is read: the search, or the input when it's shorter (issue #273).
        var searched = o.SearchDuration is { } limit && duration is { } whole && whole < limit ? whole : o.SearchDuration ?? duration;
        _progress = new ProgressReporter(o.Progress, searched);
    }

    /// <summary>
    /// Whether the search duration has been read: <see cref="Add"/> takes no more samples, and the caller can stop
    /// decoding. Never true without <see cref="ReferenceToneOptions.SearchDuration"/>.
    /// </summary>
    public bool IsComplete => _window >= _maxWindows;

    /// <summary>
    /// Searches the next samples. Chunks may be of any size (a window can span several); only whole instants (all
    /// channels) per call.
    /// </summary>
    /// <param name="interleaved">Float samples, full scale ±1, interleaved (channel 0, 1, ... for each instant).</param>
    /// <param name="time">
    /// The first sample's time. The first call's is where segments' times count from; later ones only move progress
    /// (positions follow from the samples counted, so jittery packet times don't matter).
    /// </param>
    /// <returns>False once <see cref="IsComplete"/> (samples past the search duration are ignored): stop decoding.</returns>
    /// <exception cref="ArgumentException"><paramref name="interleaved"/> isn't a whole number of instants.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish"/> was called.</exception>
    public bool Add(ReadOnlySpan<float> interleaved, TimeSpan time)
    {
        ThrowIfFinished();
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException($"The samples aren't a whole number of instants of {_channels} channels.", nameof(interleaved));
        _origin ??= time;
        _progress.Report(time - _origin.Value);
        int channels = _channels, count = interleaved.Length / channels;
        for (int i = 0; i < count && _window < _maxWindows; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                double x = interleaved[i * channels + c], y = x * _w[_at];
                double s = y + _coeff * _s1[c] - _s2[c];
                (_s2[c], _s1[c]) = (_s1[c], s);
                _energy[c] += y * y;
                _power[c] += x * x;
            }

            if (++_at < _n)
                continue;

            // The window's tone level: the loudest channel that is mostly the frequency.
            double? tone = null;
            for (int c = 0; c < channels; c++)
            {
                double goertzel = _s1[c] * _s1[c] + _s2[c] * _s2[c] - _coeff * _s1[c] * _s2[c];
                double db = 10 * Math.Log10(2 * _power[c] / _n + 1e-30);
                if (_energy[c] > 0 && goertzel / _energy[c] * _purityScale >= MinPurity && db >= MinLevel && db > (tone ?? double.MinValue))
                    tone = db;
                _s1[c] = _s2[c] = _energy[c] = _power[c] = 0;
            }

            _at = 0;

            if (tone is { } t)
            {
                if (_start >= 0 && Math.Abs(t - _level) <= LevelTolerance)
                {
                    _last = _window;
                }

                // Off level: a break while within MaxGap, unless the run is one (perhaps partial) window so far.
                else if (_start < 0 || _last == _start || _window - _last - 1 > _gapWindows)
                {
                    Close();
                    (_start, _last, _level) = (_window, _window, t);
                }
            }
            else if (_start >= 0 && _window - _last > _gapWindows)
            {
                Close();
            }

            _window++;
        }

        return !IsComplete;
    }

    /// <summary>
    /// Ends the search (a segment still running ends at the last whole window) and returns the segments; reports the
    /// last progress.
    /// </summary>
    /// <returns>The tone segments, in order; times from the first <see cref="Add"/>'s.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public IReadOnlyList<ToneSegment> Finish()
    {
        ThrowIfFinished();
        _finished = true;
        Close();
        _progress.Done();
        return _segments;
    }

    void ThrowIfFinished()
    {
        if (_finished)
            throw new InvalidOperationException("Finish was called: start a new finder.");
    }

    TimeSpan Time(long index) => _origin!.Value + TimeSpan.FromSeconds(index * _windowLength);

    void Close()
    {
        if (_start >= 0 && _last + 1 - _start >= _minWindows)
            _segments.Add(new ToneSegment(Time(_start), Time(_last + 1), _level));
        _start = -1;
    }
}
