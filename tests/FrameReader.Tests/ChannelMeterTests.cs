using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// <see cref="ChannelMeter"/> against FFmpeg's filters on the same float samples (one ffmpeg pass writes them raw):
/// each channel's silences are <c>pan=mono|c0=cN,silencedetect</c>'s to the instant, its RMS level astats' (to the
/// 6 decimals it prints), its samples at full scale astats' Peak count where the channel clips both ways, and the first
/// two channels' phase aphasemeter's mean (over frames of 4800, the samples a whole number of them).
/// </summary>
public partial class ChannelMeterTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Theory]

    // Four channels at 48 kHz: a tone with a 2.5 s gap; the tone inverted (out of phase); a sine clipped at ±1 with a
    // silence of its own; digital silence.
    [InlineData("aevalsrc=0.3*sin(440*2*PI*t)*(lt(t\\,2)+gt(t\\,4.5))|-0.3*sin(440*2*PI*t)*(lt(t\\,2)+gt(t\\,4.5))|clip(1.5*sin(200*2*PI*t)\\,-1\\,1)*lt(t\\,5)|0:s=48000:d=7.2", 48000, 4, -60.0, 2.0)]

    // Stereo, in phase with noise on one side, another level and duration.
    [InlineData("aevalsrc=0.2*sin(1000*2*PI*t)|0.2*sin(1000*2*PI*t)+0.01*sin(7777*t)*lt(t\\,1):s=48000:d=3", 48000, 2, -50.0, 0.5)]

    // Mono: no phase.
    [InlineData("aevalsrc=0.5*sin(300*2*PI*t)*gt(mod(t\\,2)\\,1):s=48000:d=6", 48000, 1, -60.0, 0.8)]
    public void IsFfmpegsFilters(string source, int rate, int channels, double level, double duration)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-channels-").FullName;
        try
        {
            string raw = Path.Combine(dir, "samples.f32");
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", source, "-f", "f32le", raw]);
            var samples = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw)).ToArray();
            string[] input = ["-hide_banner", "-nostats", "-f", "f32le", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture), "-i", raw];

            var options = new ChannelMeterOptions { SilenceLevel = level, SilenceDuration = TimeSpan.FromSeconds(duration) };
            var meter = new ChannelMeter(rate, channels, options);
            for (int at = 0; at < samples.Length; at += 997 * channels)
                meter.Add(samples.AsSpan(at, Math.Min(997 * channels, samples.Length - at)));
            var measured = meter.Finish();
            Assert.Equal(samples.Length / channels, measured.Samples);

            string astats = Run([.. input, "-af", "astats", "-f", "null", "-"]);
            var rms = RmsLevel().Matches(astats).Select(m => m.Groups[1].Value).ToList();
            var peaks = PeakCount().Matches(astats).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            for (int c = 0; c < channels; c++)
            {
                var ours = measured.Channels[c];
                string log = Run([.. input, "-af", FormattableString.Invariant($"pan=mono|c0=c{c},silencedetect=n={level}dB:d={duration}"), "-f", "null", "-"]);
                var starts = SilenceStart().Matches(log).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                var ends = SilenceEnd().Matches(log).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                Assert.Equal(starts.Count, ours.Silences.Count);
                for (int i = 0; i < starts.Count; i++)
                {
                    Assert.Equal(starts[i], (double)ours.Silences[i].StartSample / rate, 1e-5);
                    Assert.Equal(i < ends.Count ? ends[i] : (double)measured.Samples / rate, (double)ours.Silences[i].EndSample / rate, 1e-5);
                }

                if (rms[c] == "-inf")
                    Assert.Equal(double.NegativeInfinity, ours.RmsLevel);
                else
                    Assert.Equal(double.Parse(rms[c], CultureInfo.InvariantCulture), ours.RmsLevel, 1e-5);
                if (ours.FullScaleSamples > 0)
                {
                    Assert.Equal(peaks[c], ours.FullScaleSamples);
                    Assert.True(ours.ClippedRuns > 0);
                }
            }

            if (channels >= 2)
            {
                string phase = Run([.. input, "-af", "pan=stereo|c0=c0|c1=c1,asetnsamples=n=4800,aphasemeter=video=0,ametadata=print:key=lavfi.aphasemeter.phase", "-f", "null", "-"]);
                var frames = Phase().Matches(phase).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                Assert.Equal(measured.Samples / 4800, frames.Count);
                Assert.Equal(frames.Average(), measured.Phase!.Value, 1e-4);
            }
            else
            {
                Assert.Null(measured.Phase);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // On samples made here: a run of three at full scale is clipping, two isn't; both channels silent are in phase; the
    // inverted pair is -1.
    [Fact]
    public void CountsRunsAndPhase()
    {
        var meter = new ChannelMeter(1000, 2, new ChannelMeterOptions { ClipRun = 3 });
        float[] samples = [1, -1, 1, -1, 0.5f, -0.5f, 1, -1, 1, -1, 1, -1, 0, 0];
        meter.Add(samples);
        var m = meter.Finish();
        Assert.Equal(7, m.Samples);
        Assert.Equal([1, 1], m.Channels.Select(c => c.ClippedRuns));
        Assert.Equal([5L, 5L], m.Channels.Select(c => c.FullScaleSamples));
        Assert.Equal((6 * -1.0 + 1) / 7, m.Phase!.Value, 1e-6);
        Assert.Same(m, meter.Finish());
        Assert.Throws<InvalidOperationException>(() => meter.Add(new float[2]));
        Assert.Throws<ArgumentException>(() => new ChannelMeter(1000, 2).Add(new float[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMeter(1000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelMeter(1000, 1, new ChannelMeterOptions { ClipRun = 0 }));
        var empty = new ChannelMeter(1000, 2).Finish();
        Assert.Equal(double.NegativeInfinity, empty.Channels[0].RmsLevel);
        Assert.Null(empty.Phase);
    }

    [GeneratedRegex(@"RMS level dB: (-?[\d.]+|-inf)")]
    private static partial Regex RmsLevel();

    [GeneratedRegex(@"\] Peak count: ([\d.]+)")]
    private static partial Regex PeakCount();

    [GeneratedRegex(@"silence_start: (-?[\d.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end: (-?[\d.]+)")]
    private static partial Regex SilenceEnd();

    [GeneratedRegex(@"lavfi\.aphasemeter\.phase=(-?[\d.]+(?:e-?\d+)?)")]
    private static partial Regex Phase();

    static string Run(string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return stderr;
    }
}
