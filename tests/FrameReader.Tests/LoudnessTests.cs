using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// Loudness against ffmpeg's command line running the same ebur128 graph: integrated loudness and the range equal to
/// the last metadata's three decimals (the filter's figures change only when a 100 ms block completes, which is when
/// metadata is attached), peaks equal to the Summary's one decimal and, on clips of whole 100 ms blocks, to the
/// metadata's three (linear). Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public class LoudnessTests
{
    static readonly string? Libs = TestLibraries.Load();

    static (string Out, string Err) Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return (output, stderr.Result);
    }

    static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    // C's %5.1f, trimmed, as the Summary prints (digital silence's -inf too).
    static string F1(double v) => double.IsNegativeInfinity(v) ? "-inf" : v.ToString("F1", CultureInfo.InvariantCulture);

    [Theory]

    // Stereo, its level stepping every 2 s (a loudness range), whole 100 ms blocks.
    [InlineData("steps.wav", "aevalsrc=(0.05+0.1*mod(floor(t/2)\\,4))*sin(440*2*PI*t)|(0.05+0.1*mod(floor(t/2)\\,4))*sin(660*2*PI*t):s=48000:d=12", "-c:a pcm_s16le", true)]

    // 5.1: surround channels weighted 1.41, the LFE left out.
    [InlineData("surround.wav", "aevalsrc=0.2*sin(300*2*PI*t)|0.2*sin(310*2*PI*t)|0.3*sin(320*2*PI*t)|0.9*sin(40*2*PI*t)|0.25*sin(500*2*PI*t)|0.25*sin(510*2*PI*t):s=48000:d=5:c=5.1", "-c:a pcm_s16le", true)]

    // 44.1 kHz (true peak resampled to 192 kHz from another rate), a peak between samples: 1/4 of the rate.
    [InlineData("quarter.wav", "aevalsrc=0.9*sin(11025*2*PI*t+PI/4):s=44100:d=3", "-c:a pcm_s16le", true)]

    // Mono AAC: the decoder's float planes, as ffmpeg's graph gets them; the length isn't whole blocks.
    [InlineData("speech.m4a", "aevalsrc=0.5*sin(300*2*PI*t)*(lt(t\\,1)+gt(t\\,2.2)):s=48000:d=3.5", "-c:a aac", false)]

    // A clip in the last partial 100 ms (3.42 s of 3.45 s): no metadata after it, but the filter's peaks have it.
    [InlineData("tail.wav", "aevalsrc=0.1*sin(300*2*PI*t)+0.8*between(t\\,3.42\\,3.421):s=48000:d=3.45", "-c:a pcm_s16le", false)]

    // Digital silence: -70 LUFS (the absolute gate), peaks -inf.
    [InlineData("silence.wav", "anullsrc=r=48000:cl=stereo:d=2", "-c:a pcm_s16le", true)]
    public void IsFfmpegsEbur128(string name, string source, string encode, bool wholeBlocks)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-loudness-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", source, .. encode.Split(' '), path]);
            string graph = $"{Loudness.Graph}:metadata=1,ametadata=print:file=-";
            var (metadataText, log) = Ffmpeg("-hide_banner", "-nostats", "-i", path, "-af", graph, "-f", "null", "-");
            var last = new Dictionary<string, string>();
            foreach (var line in metadataText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("frame:", StringComparison.Ordinal))
                    last.Clear();
                else
                    last[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
            }

            string summary = log[log.IndexOf("Summary:", StringComparison.Ordinal)..];
            string Summary(string label, int nth = 0) =>
                Regex.Matches(summary, $@"^\s*{Regex.Escape(label)}:\s+(\S+)", RegexOptions.Multiline)[nth].Groups[1].Value;

            using var audio = new AudioReader(path);
            var r = Loudness.Measure(audio);

            Assert.Equal(last["lavfi.r128.I"], F3(r.Integrated));
            Assert.Equal(last["lavfi.r128.LRA"], F3(r.Range));
            Assert.Equal(last["lavfi.r128.LRA.low"], F3(r.RangeLow));
            Assert.Equal(last["lavfi.r128.LRA.high"], F3(r.RangeHigh));
            Assert.Equal(Summary("I"), F1(r.Integrated));
            Assert.Equal(Summary("LRA"), F1(r.Range));
            Assert.Equal(Summary("Peak", 0), F1(r.SamplePeak));
            Assert.Equal(Summary("Peak", 1), F1(r.TruePeak));
            if (wholeBlocks)
            {
                Assert.Equal(last["lavfi.r128.sample_peak"], F3(Math.Pow(10, r.SamplePeak / 20)));
                Assert.Equal(last["lavfi.r128.true_peak"], F3(Math.Pow(10, r.TruePeak / 20)));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The tail clip's peak is only in the filter's results, not in the last metadata (why Measure reads them).
    [Fact]
    public void PeaksIncludeTheLastPartialBlock()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-loudness-").FullName;
        try
        {
            string path = Path.Combine(dir, "tail.wav");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.1*sin(300*2*PI*t)+0.8*between(t\\,3.42\\,3.421):s=48000:d=3.45", "-c:a", "pcm_s16le", path);
            using var audio = new AudioReader(path);
            var r = Loudness.Measure(audio);
            Assert.Equal(20 * Math.Log10(0.9), r.SamplePeak, 0.01);
            Assert.True(r.TruePeak > -1, $"{r.TruePeak}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResultIsNullWithoutSuchAFilterOrOption()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-loudness-").FullName;
        try
        {
            string path = Path.Combine(dir, "a.wav");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "sine=d=0.5", path);
            using var audio = new AudioReader(path);
            using var filter = new AudioFilter(audio, "volume=0.5,ebur128@qc"); // the instance's name: "ebur128@qc"
            Assert.Null(filter.Result("ebur128", "integrated"));
            while (filter.TryRead(out _))
            {
            }

            Assert.NotNull(filter.Result("ebur128", "integrated"));
            Assert.Equal(filter.Result("ebur128", "true_peak"), filter.Result("ebur128@qc", "true_peak"));
            Assert.Null(filter.Result("ebur128", "no_such_option"));
            Assert.Null(filter.Result("silencedetect", "integrated"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
