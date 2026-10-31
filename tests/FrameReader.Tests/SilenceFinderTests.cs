using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// <see cref="SilenceFinder"/> against FFmpeg 8.1's silencedetect on the same float samples: one ffmpeg pass runs
/// <c>aformat=flt,silencedetect</c> and writes the samples the filter saw (it passes them on unchanged), which the finder
/// then reads in chunks of odd sizes. Every span's start and end must be the instant ffmpeg's log names (it prints
/// microseconds, finer than a sample at these rates). Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public partial class SilenceFinderTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Theory]

    // Stereo 44.1 kHz: a tone, 2.3 s below -60 dB (one channel at -66 dB), the tone again; then a silence to the end.
    [InlineData("aevalsrc=0.5*sin(440*2*PI*t)*(lt(t\\,1)+between(t\\,3.3337\\,5))|0.0005*sin(90*t)+0.5*sin(660*2*PI*t)*between(t\\,3.3337\\,5):s=44100:d=7.5", "silencedetect", 44100, 2)]

    // The right silent from instant 1000, the left from 1001, the right loud again for one instant at 1000 + 2 s:
    // silencedetect counts samples one channel after another, so its count (from the right's sample at 1000) reaches
    // the duration's at the left's sample at 97000, and the silence it reports, 1001 to 97000, is an instant short of 2 s.
    [InlineData("aevalsrc=0.5*lt(n\\,1001)|0.5*lt(n\\,1000)+0.5*eq(n\\,97000):s=48000:d=3", "silencedetect", 48000, 2)]

    // A stretch exactly at the level (0.001 as a float, as the filter's option is compared): not silent.
    [InlineData("aevalsrc=0.5*lt(t\\,0.5)+0.001*between(t\\,1\\,1.2):s=48000:d=4", "silencedetect", 48000, 1)]

    // 5.1, noise just under and just over the level, a short duration and another level.
    [InlineData("aevalsrc=0.03*sin(1000*t)*gt(mod(t\\,1.5)\\,0.6)|0.02*sin(1200*t)*gt(mod(t\\,1.5)\\,0.6)|0.0008|0.0009*sin(50*t)|0.0011*gt(mod(t\\,1.5)\\,0.6)|0:s=48000:d=6:c=5.1", "silencedetect=n=-50dB:d=0.4", 48000, 6)]

    // Mono, d=0: a silence of one sample counts, and starts one instant late (silencedetect's start arithmetic).
    [InlineData("aevalsrc=0.5*sin(300*2*PI*t)*gt(mod(n\\,4000)\\,1):s=32000:d=1", "silencedetect=d=0", 32000, 1)]

    // The duration rounded to the nearest instant (0.35 s at 22.05 kHz: 7717.5, 7718), runs either side of it.
    [InlineData("aevalsrc=0.2*(lt(n\\,100)+between(n\\,7817\\,7820)+between(n\\,15538\\,15540)):s=22050:d=1.2", "silencedetect=d=0.35", 22050, 1)]
    public void IsFfmpegsSilencedetect(string source, string filter, int rate, int channels)
    {
        if (Libs is null)
            return;
        var (samples, log) = Run(["-f", "lavfi", "-i", source], filter);
        var expected = Spans(log, rate);

        // Chunks of several sizes, one of them not a divisor of anything here.
        foreach (int chunk in new[] { 1, 997, 4096, int.MaxValue / channels })
        {
            var finder = new SilenceFinder(rate, channels, Level(filter), Duration(filter));
            for (int at = 0; at < samples.Length; at += chunk * channels)
                finder.Add(samples.AsSpan(at, Math.Min(chunk * channels, samples.Length - at)));
            Assert.Equal(expected, finder.Finish());
        }
    }

    // An AAC clip: the decoder's floats, as the web's ManagedCodecs path and FFmpeg's both give them.
    [Fact]
    public void IsFfmpegsSilencedetectOnDecodedAac()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-silence-").FullName;
        try
        {
            string path = Path.Combine(dir, "a.m4a");
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.4*sin(500*2*PI*t)*(lt(t\\,1)+gt(t\\,3.5))|0.3*sin(700*2*PI*t)*(lt(t\\,1.2)+gt(t\\,3.5)):s=48000:d=5", "-c:a", "aac", path]);
            var (samples, log) = Run(["-i", path], "silencedetect=n=-70dB:d=1.5");
            var finder = new SilenceFinder(48000, 2, -70, TimeSpan.FromSeconds(1.5));
            finder.Add(samples);
            var found = finder.Finish();
            Assert.Single(found);
            Assert.Equal(Spans(log, 48000), found);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpansHaveTimes()
    {
        var s = new SilenceSpan(48000, 144000);
        Assert.Equal(TimeSpan.FromSeconds(1), s.Start(48000));
        Assert.Equal(TimeSpan.FromSeconds(3), s.End(48000));
        Assert.Equal(TimeSpan.FromTicks(10000000L / 3), new SilenceSpan(1, 1).Start(3)); // rounded down to a tick
    }

    [Fact]
    public void RefusesPartInstantsAndUseAfterFinish()
    {
        var finder = new SilenceFinder(48000, 2);
        Assert.Throws<ArgumentException>(() => finder.Add(new float[3]));
        finder.Add(new float[4]);
        Assert.Empty(finder.Finish());
        Assert.Throws<InvalidOperationException>(() => finder.Add(new float[2]));
        Assert.Throws<InvalidOperationException>(() => finder.Finish());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SilenceFinder(48000, 2, duration: TimeSpan.FromSeconds(-1)));
    }

    static double Level(string filter) =>
        filter.Contains("n=", StringComparison.Ordinal) ? double.Parse(N().Match(filter).Groups[1].Value, CultureInfo.InvariantCulture) : -60;

    static TimeSpan? Duration(string filter) =>
        filter.Contains("d=", StringComparison.Ordinal) ? TimeSpan.FromSeconds(double.Parse(D().Match(filter).Groups[1].Value, CultureInfo.InvariantCulture)) : null;

    /// <summary>The spans ffmpeg logged, in instants: each printed time (microseconds) rounded to the rate's instant.</summary>
    static List<SilenceSpan> Spans(string log, int rate)
    {
        long Instant(string seconds) => (long)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * rate);
        var starts = Start().Matches(log).Select(m => Instant(m.Groups[1].Value)).ToList();
        var ends = End().Matches(log).Select(m => Instant(m.Groups[1].Value)).ToList();
        Assert.Equal(starts.Count, ends.Count);
        return [.. starts.Zip(ends, (s, e) => new SilenceSpan(s, e))];
    }

    /// <summary>The input through <c>aformat=flt,<paramref name="filter"/></c>: the float samples silencedetect saw, and the log.</summary>
    static (float[] Samples, string Log) Run(string[] input, string filter)
    {
        string dir = Directory.CreateTempSubdirectory("framereader-silence-").FullName;
        try
        {
            string raw = Path.Combine(dir, "out.f32");
            string log = Ffmpeg(["-hide_banner", "-nostats", "-y", .. input, "-af", $"aformat=sample_fmts=flt,{filter}", "-f", "f32le", raw]);
            return (MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw)).ToArray(), log);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static string Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string log = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, log);
        return log;
    }

    [GeneratedRegex(@"silence_start: (-?[0-9.e+-]+)")]
    private static partial Regex Start();

    [GeneratedRegex(@"silence_end: (-?[0-9.e+-]+)")]
    private static partial Regex End();

    [GeneratedRegex(@"n=(-?[0-9.]+)dB")]
    private static partial Regex N();

    [GeneratedRegex(@"d=([0-9.]+)")]
    private static partial Regex D();
}
