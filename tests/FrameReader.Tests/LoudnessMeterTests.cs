using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// LoudnessMeter against FFmpeg's ebur128 on the same samples: each clip is read once through
/// <c>AudioFilter(reader, "ebur128=peak=true+sample")</c>, whose output chunks (the samples ebur128 saw) feed the meter.
/// Integrated loudness, range, its ends and sample peak equal the filter's results at full precision. True peak is
/// BS.1770-4 Annex 2's, not ebur128's (docs/decisions/2026-10-31-frame-reader-measures.md): within 0.05 dB of an ideal
/// reconstruction (tools/true_peak_reference.py on the same clips, the numbers below) and, on audio without energy near
/// Nyquist, within 0.1 dB of ebur128's. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public class LoudnessMeterTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    [Theory]

    // Columns: name, lavfi source (float WAV), the ideal true peak (dBTP), whether ebur128's true peak is within 0.1 dB.
    [InlineData("sine1k", "aevalsrc=0.0125*sin(1000*2*PI*t)|0.0125*sin(1000*2*PI*t):s=48000:d=5", -38.062, true)]
    [InlineData("pink", "anoisesrc=c=pink:r=48000:a=0.3:d=10:s=1,pan=stereo|c0=c0|c1=c0", -11.797, true)]

    // Pink noise with 5 s level steps (a loudness range), 44.1 kHz mono.
    [InlineData("steps", "anoisesrc=c=pink:r=44100:a=0.5:d=20:s=2,volume=0.2+0.3*mod(floor(t/5)\\,3):eval=frame", -9.797, true)]

    // 96 kHz (oversampled 4 times too) and 32 kHz.
    [InlineData("pink96", "anoisesrc=c=pink:r=96000:a=0.5:d=6:s=4", -6.988, true)]
    [InlineData("brown", "anoisesrc=c=brown:r=32000:a=0.5:d=6:s=5", -7.582, true)]

    // White noise: energy up to Nyquist, where ebur128's resampler overshoots.
    [InlineData("white", "anoisesrc=c=white:r=48000:a=0.4:d=4:s=6,pan=stereo|c0=c0|c1=0.5*c0", -3.380, false)]

    // Shorter than one 400 ms block: -70 LUFS, peaks all the same.
    [InlineData("short", "anoisesrc=c=white:r=48000:a=0.3:d=0.35:s=7", -6.459, false)]

    // A partial last 100 ms block, a click in it.
    [InlineData("tail", "aevalsrc=0.1*sin(300*2*PI*t)+0.8*between(t\\,3.42\\,3.421):s=48000:d=3.45", 0.018, true)]

    // Sines at a quarter of the rate, peaks between the samples, abrupt edges.
    [InlineData("hf48", "aevalsrc=0.9*sin(12000*2*PI*t+PI/4):s=48000:d=2", -0.803, false)]
    [InlineData("hf44", "aevalsrc=0.9*sin(11025*2*PI*t+PI/4):s=44100:d=3", -0.803, false)]

    // 96 kHz noise up to 20 kHz: oversampled 4 times, as below 96 kHz (twice reads 0.06 dB low here).
    [InlineData("white96", "anoisesrc=c=white:r=96000:a=0.5:d=4:s=2,lowpass=f=20000:p=2,lowpass=f=20000:p=2", -5.077, false)]

    // 5.1 and 7.1: the LFE weighted 0, the surround channels 1.41 (the WAVs carry the layouts, FFmpeg's defaults).
    [InlineData("surround", "aevalsrc=0.2*sin(300*2*PI*t)|0.2*sin(310*2*PI*t)|0.3*sin(320*2*PI*t)|0.9*sin(40*2*PI*t)|0.25*sin(500*2*PI*t)|0.25*sin(510*2*PI*t):s=48000:d=5:c=5.1", -0.915, true)]
    [InlineData("seven", "aevalsrc=0.1*sin(300*2*PI*t)|0.1*sin(310*2*PI*t)|0.2*sin(320*2*PI*t)|0.9*sin(40*2*PI*t)|0.15*sin(500*2*PI*t)|0.15*sin(510*2*PI*t)|0.2*sin(700*2*PI*t)|0.2*sin(710*2*PI*t):s=48000:d=5:c=7.1", -0.915, true)]

    // Digital silence: -70 LUFS, peaks negative infinity.
    [InlineData("silence", "anullsrc=r=48000:cl=stereo:d=2", double.NegativeInfinity, true)]
    public void IsEbur128s(string name, string source, double idealTruePeak, bool truePeakAgrees)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-meter-").FullName;
        try
        {
            string path = Path.Combine(dir, name + ".wav");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", source, "-c:a", "pcm_f32le", path);
            var (ffmpeg, measures) = Both(path, 4800);
            var r = measures.Loudness;
            Assert.Equal(ffmpeg.Integrated, r.Integrated);
            Assert.Equal(ffmpeg.Range, r.Range);
            Assert.Equal(ffmpeg.RangeLow, r.RangeLow);
            Assert.Equal(ffmpeg.RangeHigh, r.RangeHigh);
            Assert.Equal(ffmpeg.SamplePeak, r.SamplePeak);
            if (double.IsNegativeInfinity(idealTruePeak))
            {
                Assert.Equal(double.NegativeInfinity, r.TruePeak);
            }
            else
            {
                Assert.InRange(r.TruePeak, idealTruePeak - 0.05, idealTruePeak + 0.05);
                if (truePeakAgrees)
                    Assert.InRange(r.TruePeak, ffmpeg.TruePeak - 0.1, ffmpeg.TruePeak + 0.1);
            }

            Assert.Equal(r.SamplePeak, measures.Channels.Max(c => c.SamplePeak));
            Assert.Equal(r.TruePeak, measures.Channels.Max(c => c.TruePeak));
            Assert.All(measures.Channels, c => Assert.True(c.TruePeak >= c.SamplePeak));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Chunks of any size give the same doubles (a decoder's packets: AAC's 1024, odd sizes, one sample at a time).
    [Fact]
    public void ChunkSizesDontMatter()
    {
        var samples = Noise(48000, 2, 5);
        var whole = Measure(samples, 48000, 2, samples.Length / 2);
        foreach (int chunk in (int[])[1, 7, 1024, 4801])
        {
            var (loudness, channels) = Measure(samples, 48000, 2, chunk);
            Assert.Equal(whole.Item1, loudness);
            Assert.Equal(whole.Item2, channels);
        }
    }

    // Per-channel peaks and the count of samples at full scale, each channel its own.
    [Fact]
    public void ChannelPeaksAndFullScale()
    {
        float[] samples = new float[48000 * 2];
        for (int i = 0; i < 48000; i++)
        {
            samples[i * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
            samples[(i * 2) + 1] = i % 1000 == 0 ? (i % 2000 == 0 ? 1f : -1.25f) : 0.1f;
        }

        var meter = new LoudnessMeter(48000, 2);
        meter.Add(samples);
        var m = meter.Finish();
        Assert.Equal(0, m.Channels[0].FullScale);
        Assert.Equal(48, m.Channels[1].FullScale);
        Assert.Equal(20 * Math.Log10(0.5), m.Channels[0].SamplePeak, 4);
        Assert.Equal(20 * Math.Log10(1.25), m.Channels[1].SamplePeak);
        Assert.Equal(m.Channels[1].SamplePeak, m.Loudness.SamplePeak);
    }

    // Weights decide the sum: an LFE-only signal is silence to ebur128, a surround channel 1.41 times a front one.
    [Fact]
    public void WeightsFollowTheLayout()
    {
        Assert.Equal([1, 1, 1, 0, 1.41, 1.41], ChannelWeights.Default(6).Weights);
        Assert.Equal([1, 1, 1, 0, 1.41, 1.41, 1.41, 1.41], ChannelWeights.Default(8).Weights);
        Assert.Equal([1, 1, 1, 0, 1.41, 1.41], ChannelWeights.ForLayout("5.1(side)")!.Weights);
        Assert.Equal([1, 1, 1, 0, 1.41, 1.41, 1, 1, 1.41, 1.41], ChannelWeights.ForLayout("5.1.4")!.Weights);
        Assert.Equal([1, 1, 0], ChannelWeights.Default(3).Weights);
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 1, 1], ChannelWeights.Default(9).Weights);
        Assert.Equal(24, ChannelWeights.Default(24).Weights.Count);
        Assert.Equal(2, ChannelWeights.Default(24).Weights.Count(w => w == 0));
        Assert.Null(ChannelWeights.ForLayout("no such layout"));

        var lfe = new float[6 * 48000 * 3];
        for (int i = 0; i < lfe.Length / 6; i++)
            lfe[(i * 6) + 3] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        var silent = new LoudnessMeter(48000, 6);
        silent.Add(lfe);
        Assert.Equal(-70, silent.Finish().Loudness.Integrated);
        var counted = new LoudnessMeter(48000, 6, new ChannelWeights([1, 1, 1, 1, 1, 1]));
        counted.Add(lfe);
        Assert.True(counted.Finish().Loudness.Integrated > -10);
    }

    [Fact]
    public void ArgumentsAndOrder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessMeter(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessMeter(48000, 0));
        Assert.Throws<ArgumentException>(() => new LoudnessMeter(48000, 2, new ChannelWeights([1.0])));
        Assert.Throws<ArgumentException>(() => new ChannelWeights([]));
        Assert.Throws<ArgumentException>(() => new ChannelWeights([1, -1]));
        var meter = new LoudnessMeter(48000, 2);
        Assert.Throws<ArgumentException>(() => meter.Add(new float[3]));
        var empty = meter.Finish();
        Assert.Equal(new LoudnessResult(-70, 0, 0, 0, double.NegativeInfinity, double.NegativeInfinity), empty.Loudness);
        Assert.Throws<InvalidOperationException>(() => meter.Add(new float[2]));
        Assert.Throws<InvalidOperationException>(() => meter.Finish());
    }

    // The histogram's energies are FFmpeg's ff_exp10 (glibc's exp2) of each bin's loudness, bit for bit: x86-64's, the
    // reference (ARM's glibc rounds its own way).
    [Fact]
    public void BinEnergiesAreExp2s()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        for (int i = 0; i < LoudnessMeter.BinEnergy.Length; i++)
        {
            double loudness = (i / 100.0) + -70;
            Assert.Equal(Exp2(3.32192809488736234787 * ((loudness + 0.691) / 10.0)), LoudnessMeter.BinEnergy[i]);
        }
    }

    [DllImport("libm.so.6", EntryPoint = "exp2")]
    static extern double Exp2(double x);

    static (LoudnessResult Ffmpeg, LoudnessMeasures Meter) Both(string path, int? chunk = null)
    {
        using var audio = new AudioReader(path);
        using var filter = new AudioFilter(audio, Loudness.Graph);
        LoudnessMeter? meter = null;
        while (filter.TryRead(out var c))
        {
            meter ??= new LoudnessMeter(c.SampleRate, c.Channels);
            var s = c.Samples;
            int step = (chunk ?? s.Length) * c.Channels;
            for (int at = 0; at < s.Length; at += step)
                meter.Add(s.Slice(at, Math.Min(step, s.Length - at)));
        }

        double Get(string option) => filter.Result("ebur128", option)!.Value;
        var ffmpeg = new LoudnessResult(Get("integrated"), Get("range"), Get("lra_low"), Get("lra_high"), Get("sample_peak"), Get("true_peak"));
        return (ffmpeg, meter!.Finish());
    }

    static float[] Noise(int rate, int channels, int seconds)
    {
        var random = new Random(646);
        var samples = new float[rate * channels * seconds];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)((random.NextDouble() - 0.5) * (0.2 + (0.6 * (i / channels / rate % 2))));
        return samples;
    }

    static (LoudnessResult, ChannelPeaks[]) Measure(float[] samples, int rate, int channels, int chunk)
    {
        var meter = new LoudnessMeter(rate, channels);
        for (int at = 0; at < samples.Length; at += chunk * channels)
            meter.Add(samples.AsSpan(at, Math.Min(chunk * channels, samples.Length - at)));
        var m = meter.Finish();
        return (m.Loudness, [.. m.Channels]);
    }
}
