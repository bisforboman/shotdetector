namespace FrameReader;

/// <summary>What a <see cref="LoudnessMeter"/> measured.</summary>
/// <param name="Loudness">
/// The programme's loudness, as <see cref="FrameReader.Loudness.Measure"/> gives it: integrated loudness, range and its
/// ends equal to ebur128's, sample peak equal, true peak by BS.1770-4 Annex 2 (see <see cref="LoudnessMeter"/>).
/// </param>
/// <param name="Channels">Each channel's peaks, in the samples' channel order.</param>
public sealed record LoudnessMeasures(LoudnessResult Loudness, IReadOnlyList<ChannelPeaks> Channels);

/// <summary>One channel's peaks.</summary>
/// <param name="SamplePeak">The largest sample's magnitude in dBFS; negative infinity for digital silence.</param>
/// <param name="TruePeak">The true peak in dBTP (BS.1770-4 Annex 2, 4 times oversampled); negative infinity for digital silence.</param>
/// <param name="FullScale">How many samples reach full scale (a magnitude of 1 or more): clipped, or about to be.</param>
public readonly record struct ChannelPeaks(double SamplePeak, double TruePeak, long FullScale);

/// <summary>
/// EBU R128 loudness of samples the caller decodes, a chunk at a time (a decoder of its own, as in a browser), with
/// FFmpeg's <c>ebur128</c> arithmetic and nothing of FFmpeg: integrated loudness, loudness range and its ends are the
/// same doubles <see cref="Loudness.Measure"/> gives for the same samples, and so is the sample peak. The true peak is
/// BS.1770-4 Annex 2's (a 16-tap windowed sinc per phase, 4 times the rate below 192 kHz), where ebur128 resamples to
/// 192 kHz with swresample: the two agree on programme audio, and on audio with energy near Nyquist ebur128 reads up to
/// about 0.8 dB higher, an overshoot of its resampler (FrameReader's README, "Loudness without FFmpeg"). Holds 3 s of
/// K-weighted squares per channel and two histograms, never the track. Not thread-safe: one caller feeds it.
/// </summary>
/// <remarks>
/// As ebur128: BS.1770-4's K-weighting for the stream's rate; per-sample running sums over 400 ms and 3 s, a block
/// every 100 ms once full; each block counted in a histogram bin 0.01 LU wide from -70 to +10 LUFS (truncated), the
/// integrated loudness from the bins' energies above the relative gate's bin (10 LU below the running mean of the kept
/// blocks), the range's ends the bins of the 10th and 95th percentiles above a gate 20 LU below. With two or more
/// channels the K-weighting adds in the order of ebur128's x86-64 AVX kernel, which differs from its C one in one sum;
/// the histogram hides that unless a block's loudness sits within about 1e-13 LU of a bin's edge.
/// </remarks>
/// <example>
/// <code>
/// var meter = new LoudnessMeter(48000, 2);
/// foreach (var samples in decoded)
///     meter.Add(samples);
/// var measures = meter.Finish();
/// Console.WriteLine($"{measures.Loudness.Integrated:F1} LUFS");
/// </code>
/// </example>
public sealed class LoudnessMeter
{
    const int AbsoluteGate = -70, UpperLimit = 10, Grain = 100;
    const int HistogramSize = ((UpperLimit - AbsoluteGate) * Grain) + 1;
    const double Log2Of10 = 3.32192809488736234787; // M_LOG2_10

    static readonly double[] BinLoudness = new double[HistogramSize];
    internal static readonly double[] BinEnergy = new double[HistogramSize];

    readonly int _channels, _step;
    readonly bool _avxOrder;
    readonly double[] _weights;
    readonly Biquad _pre, _rlb;
    readonly double[] _y, _z;
    readonly Integrator _i400, _i3000;
    readonly double[] _samplePeaks;
    readonly long[] _fullScale;
    readonly TruePeakMeter[] _truePeaks;
    float[] _channel = [];
    long _sampleCount;
    double _integrated = AbsoluteGate, _range, _rangeLow, _rangeHigh;
    bool _finished;

    static LoudnessMeter()
    {
        for (int i = 0; i < HistogramSize; i++)
        {
            BinLoudness[i] = (i / (double)Grain) + AbsoluteGate;
            BinEnergy[i] = Energy(i);
        }
    }

    /// <summary>Starts a measurement.</summary>
    /// <param name="sampleRate">The samples' rate in Hz.</param>
    /// <param name="channels">Channels in the interleaved samples.</param>
    /// <param name="weights">Each channel's weight; null: <see cref="ChannelWeights.Default"/> for the count (FFmpeg's default layout).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> is under 3 Hz, or <paramref name="channels"/> isn't positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="weights"/> doesn't have one weight per channel.</exception>
    public LoudnessMeter(int sampleRate, int channels, ChannelWeights? weights = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 3); // ebur128's minimum
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        weights ??= ChannelWeights.Default(channels);
        if (weights.Weights.Count != channels)
            throw new ArgumentException($"There are {weights.Weights.Count} weights for {channels} channels.", nameof(weights));
        _channels = channels;
        _weights = [.. weights.Weights];
        _avxOrder = channels >= 2;

        // ebur128's "unofficial reversed parametrization" of BS.1770-4's 48 kHz filters, for any rate.
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double vh = Math.Pow(10.0, g / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + (k / q) + (k * k);
        _pre = new Biquad((vh + (vb * k / q) + (k * k)) / a0, 2.0 * ((k * k) - vh) / a0, (vh - (vb * k / q) + (k * k)) / a0, 2.0 * ((k * k) - 1.0) / a0, (1.0 - (k / q) + (k * k)) / a0);
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / sampleRate);
        _rlb = new Biquad(1.0, -2.0, 1.0, 2.0 * ((k * k) - 1.0) / (1.0 + (k / q) + (k * k)), (1.0 - (k / q) + (k * k)) / (1.0 + (k / q) + (k * k)));

        _y = new double[channels * 3];
        _z = new double[channels * 3];
        _i400 = new Integrator(sampleRate * 2 / 5, channels);
        _i3000 = new Integrator(sampleRate * 3, channels);
        _step = sampleRate / 10;
        _samplePeaks = new double[channels];
        _fullScale = new long[channels];
        _truePeaks = new TruePeakMeter[channels];
        for (int c = 0; c < channels; c++)
            _truePeaks[c] = new TruePeakMeter(sampleRate);
    }

    /// <summary>Adds the next samples. Chunks may be of any size; only whole instants (all channels) per call.</summary>
    /// <param name="interleaved">Float samples, full scale ±1, interleaved (channel 0, 1, ... for each instant).</param>
    /// <exception cref="ArgumentException"><paramref name="interleaved"/> isn't a whole number of instants.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish"/> was called.</exception>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        if (_finished)
            throw new InvalidOperationException("The meter is finished.");
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException($"The samples aren't a whole number of instants of {_channels} channels.", nameof(interleaved));
        int count = interleaved.Length / _channels;
        if (_channel.Length < count)
            _channel = new float[count];
        for (int c = 0; c < _channels; c++)
        {
            double peak = _samplePeaks[c];
            long full = 0;
            for (int i = 0; i < count; i++)
            {
                float s = interleaved[(i * _channels) + c];
                _channel[i] = s;
                double a = Math.Abs((double)s);
                peak = peak > a ? peak : a; // FFMAX
                if (a >= 1)
                    full++;
            }

            _samplePeaks[c] = peak;
            _fullScale[c] += full;
            _truePeaks[c].Add(_channel.AsSpan(0, count));
        }

        for (int i = 0; i < count; i++)
        {
            int bin400 = _i400.Next(), bin3000 = _i3000.Next();
            Filter(interleaved.Slice(i * _channels, _channels), bin400 * _channels, bin3000 * _channels);
            if (++_sampleCount == _step)
            {
                _sampleCount = 0;
                EndBlock();
            }
        }
    }

    /// <summary>The measures of everything added. Once: <see cref="Add"/> throws after it.</summary>
    /// <returns>The loudness (-70 LUFS when no block passes the absolute gate, ebur128's start) and each channel's peaks.</returns>
    /// <exception cref="InvalidOperationException">Called twice.</exception>
    public LoudnessMeasures Finish()
    {
        if (_finished)
            throw new InvalidOperationException("The meter is finished.");
        _finished = true;
        var channels = new ChannelPeaks[_channels];
        double samplePeak = 0, truePeak = 0;
        for (int c = 0; c < _channels; c++)
        {
            _truePeaks[c].Finish();
            channels[c] = new ChannelPeaks(Dbfs(_samplePeaks[c]), Dbfs(_truePeaks[c].Peak), _fullScale[c]);
            samplePeak = samplePeak > _samplePeaks[c] ? samplePeak : _samplePeaks[c];
            truePeak = Math.Max(truePeak, _truePeaks[c].Peak);
        }

        return new LoudnessMeasures(new LoudnessResult(_integrated, _range, _rangeLow, _rangeHigh, Dbfs(samplePeak), Dbfs(truePeak)), channels);
    }

    /// <summary>
    /// ebur128's ENERGY: ff_exp10, the host libm's exp2 of M_LOG2_10 × x. Computed here correctly rounded (double-double,
    /// the same on every platform). glibc's exp2 differs by the build: its generic x86-64 build is one ulp off correct
    /// rounding in 11 of the 8001 bins, its x86-64-v3 build (Ubuntu 26.04's runners) not in the first of them, so no table
    /// matches every host; correct rounding is the one that doesn't depend on it (decision 2026-10-31-frame-reader-measures.md).
    /// </summary>
    static double Energy(int bin) => Exp2(Log2Of10 * ((BinLoudness[bin] + 0.691) / 10.0));

    /// <summary>
    /// 2^x correctly rounded for |x| up to about 1000: 2^n × e^(f ln 2) with f = x - n in [-0.5, 0.5], the exponential
    /// by its Taylor series in double-double arithmetic (about 104 bits), so only + and × (Dekker's exact products).
    /// </summary>
    internal static double Exp2(double x)
    {
        double n = Math.Round(x);
        double f = x - n; // exact
        const double Ln2Hi = 0.6931471805599453, Ln2Lo = 2.3190468138462996e-17;
        var (pHi, pLo) = TwoProduct(f, Ln2Hi);
        var (tHi, tLo) = TwoSum(pHi, pLo + (f * Ln2Lo));

        // e^t = 1 + t + t²/2! + ...: term_k = term_(k-1) × t / k, summed from the smallest up.
        Span<double> hi = stackalloc double[28], lo = stackalloc double[28];
        (hi[0], lo[0]) = (1, 0);
        int k = 1;
        for (; k < hi.Length; k++)
        {
            var (mHi, mLo) = ProductDD(hi[k - 1], lo[k - 1], tHi, tLo);
            (hi[k], lo[k]) = QuotientDD(mHi, mLo, k);
            if (Math.Abs(hi[k]) < 1e-40)
                break;
        }

        double sHi = 0, sLo = 0;
        for (int i = Math.Min(k, hi.Length - 1); i >= 0; i--)
            (sHi, sLo) = SumDD(sHi, sLo, hi[i], lo[i]);
        return Math.ScaleB(sHi + sLo, (int)n);
    }

    static (double Hi, double Lo) TwoSum(double a, double b)
    {
        double s = a + b, v = s - a;
        return (s, (a - (s - v)) + (b - v));
    }

    static (double Hi, double Lo) TwoProduct(double a, double b)
    {
        const double Split = 134217729.0; // 2^27 + 1
        double p = a * b;
        double ca = Split * a, ah = ca - (ca - a), al = a - ah;
        double cb = Split * b, bh = cb - (cb - b), bl = b - bh;
        return (p, (((ah * bh) - p) + (ah * bl) + (al * bh)) + (al * bl));
    }

    static (double Hi, double Lo) SumDD(double aHi, double aLo, double bHi, double bLo)
    {
        var (s, e) = TwoSum(aHi, bHi);
        var (t, f) = TwoSum(aLo, bLo);
        (s, e) = TwoSum(s, e + t);
        return TwoSum(s, e + f);
    }

    static (double Hi, double Lo) ProductDD(double aHi, double aLo, double bHi, double bLo)
    {
        var (p, e) = TwoProduct(aHi, bHi);
        return TwoSum(p, e + (aHi * bLo) + (aLo * bHi));
    }

    static (double Hi, double Lo) QuotientDD(double aHi, double aLo, int k)
    {
        double q = aHi / k;
        var (p, e) = TwoProduct(q, k);
        double r = ((aHi - p) - e + aLo) / k;
        return TwoSum(q, r);
    }

    static double Lufs(double energy) => -0.691 + (10 * Math.Log10(energy));

    static double Dbfs(double amplitude) => 20 * Math.Log10(amplitude);

    // ebur128's HIST_POS, clipped: the bin's index, truncated toward zero.
    static int Bin(double loudness) => Math.Clamp((int)((loudness - AbsoluteGate) * Grain), 0, HistogramSize - 1);

    /// <summary>One instant through both filters, each channel's square added to the running sums (ebur128's filter_channels).</summary>
    void Filter(ReadOnlySpan<float> instant, int cache400, int cache3000)
    {
        var (pre, rlb) = (_pre, _rlb);
        for (int ch = 0; ch < _channels; ch++)
        {
            double x = instant[ch];
            var y = _y.AsSpan(ch * 3, 3);
            var z = _z.AsSpan(ch * 3, 3);
            if (_avxOrder)
            {
                // ff_ebur128_filter_channels_avx: Y[1] = b1 * X + (Y[2] - a1 * Y[0]).
                double t = y[0] = (pre.B0 * x) + y[1];
                y[1] = (pre.B1 * x) + (y[2] - (pre.A1 * t));
                y[2] = (pre.B2 * x) - (pre.A2 * t);
                double u = z[0] = (rlb.B0 * t) + z[1];
                z[1] = (rlb.B1 * t) + (z[2] - (rlb.A1 * u));
                z[2] = (rlb.B2 * t) - (rlb.A2 * u);
            }
            else
            {
                // ff_ebur128_filter_channels_c: Y[1] = b1 * X + Y[2] - a1 * Y[0].
                double t = y[0] = (pre.B0 * x) + y[1];
                y[1] = (pre.B1 * x) + y[2] - (pre.A1 * t);
                y[2] = (pre.B2 * x) - (pre.A2 * t);
                double u = z[0] = (rlb.B0 * t) + z[1];
                z[1] = (rlb.B1 * t) + z[2] - (rlb.A1 * u);
                z[2] = (rlb.B2 * t) - (rlb.A2 * u);
            }

            double bin = z[0] * z[0];
            _i400.Sum[ch] += bin - _i400.Cache[cache400 + ch];
            _i3000.Sum[ch] += bin - _i3000.Cache[cache3000 + ch];
            _i400.Cache[cache400 + ch] = _i3000.Cache[cache3000 + ch] = bin;
        }
    }

    /// <summary>Every 100 ms: the 400 ms and 3 s blocks' loudness, the integrated loudness and the range so far.</summary>
    void EndBlock()
    {
        double power400 = _i400.Power(_weights), power3000 = _i3000.Power(_weights);
        double loudness400 = Lufs(power400), loudness3000 = Lufs(power3000);
        if (loudness400 >= AbsoluteGate)
        {
            int gate = _i400.Gate(power400, loudness400, -10);
            double sum = 0;
            ulong n = 0;
            for (int i = gate; i < HistogramSize; i++)
            {
                uint count = _i400.Histogram[i];
                n += count;
                sum += count * BinEnergy[i];
            }

            if (n != 0)
                _integrated = Lufs(sum / n);
        }

        if (loudness3000 >= AbsoluteGate)
        {
            int gate = _i3000.Gate(power3000, loudness3000, -20);
            var h = _i3000.Histogram;
            ulong powers = 0;
            for (int i = gate; i < HistogramSize; i++)
                powers += h[i];
            if (powers != 0)
            {
                ulong n = 0, wanted = (ulong)((10 * powers * 0.01) + 0.5);
                for (int i = gate; i < HistogramSize; i++)
                {
                    n += h[i];
                    if (n >= wanted)
                    {
                        _rangeLow = BinLoudness[i];
                        break;
                    }
                }

                n = powers;
                wanted = (ulong)((95 * powers * 0.01) + 0.5);
                for (int i = HistogramSize - 1; i >= 0; i--)
                {
                    n -= Math.Min(n, h[i]);
                    if (n < wanted)
                    {
                        _rangeHigh = BinLoudness[i];
                        break;
                    }
                }

                _range = _rangeHigh - _rangeLow;
            }
        }
    }

    readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    /// <summary>ebur128's integrator: the last 400 ms or 3 s of squares per channel, their running sums, a histogram.</summary>
    sealed class Integrator(int size, int channels)
    {
        public readonly double[] Cache = new double[size * channels];
        public readonly double[] Sum = new double[channels];
        public readonly uint[] Histogram = new uint[HistogramSize];
        int _position;
        bool _filled;
        double _keptPowers;
        int _kept;

        /// <summary>The cache slot of the next instant; full once the cache wraps.</summary>
        public int Next()
        {
            int at = _position++;
            if (_position == size)
            {
                _filled = true;
                _position = 0;
            }

            return at;
        }

        /// <summary>The block's mean weighted power, 1e-12 until the cache is full.</summary>
        public double Power(double[] weights)
        {
            double power = 1e-12;
            if (_filled)
            {
                for (int ch = 0; ch < Sum.Length; ch++)
                    power += weights[ch] * Sum[ch];
                power /= size;
            }

            return power;
        }

        /// <summary>ebur128's gate_update: counts the block, gives the relative gate's bin.</summary>
        public int Gate(double power, double loudness, int below)
        {
            Histogram[Bin(loudness)]++;
            _keptPowers += power;
            _kept++;
            double mean = _keptPowers / _kept;
            if (mean == 0)
                mean = 1e-12;
            return Bin(Lufs(mean) + below);
        }
    }
}
