using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using ShotDetector;

/// <summary>The shotdetect CLI, run as a process (built with the tests, like ClipMaker).</summary>
public class CliTests(Clips clips) : IClassFixture<Clips>
{
    static (int ExitCode, string Out, string Err) Run(params string[] args)
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string tests = bin.Parent!.Parent!.Parent!.Parent!.FullName;
        string cli = Path.Combine(Path.GetDirectoryName(tests)!, "src", "ShotDetector.Cli", "bin", bin.Parent.Name, bin.Name, "shotdetect.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(cli);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.Result);
    }

    // A warning goes to stderr as soon as it is known (--parallel on a short clip: before the first frame, whichever
    // decoder reads it), so before the summary line printed after detection, and only once.
    [Fact]
    public void WarningsArePrintedOnceAsTheyHappen()
    {
        var (exitCode, output, err) = Run("-i", clips.ThreeShots, "--parallel");
        Assert.True(exitCode == 0, err);
        var warning = Assert.Single(Regex.Matches(err, "^Warning: .*$", RegexOptions.Multiline));
        Assert.StartsWith("Warning: read sequentially, not in chunks: ", warning.Value);
        int summary = err.IndexOf(" frames, processed at ", StringComparison.Ordinal);
        Assert.True(summary > warning.Index, err);
        Assert.DoesNotContain("Warning", output, StringComparison.Ordinal);
        Assert.Contains("Scene", output, StringComparison.Ordinal);
    }

    // --version prints the package's version (the library's: both take ShotDetectorVersion), without the "+commit".
    [Fact]
    public void VersionPrintsTheVersion()
    {
        var (exitCode, output, err) = Run("--version");
        Assert.True(exitCode == 0, err);
        string expected = typeof(ShotDetection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(expected, output.TrimEnd());
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", output.TrimEnd());
        Assert.Empty(err);
    }

    // --loudness: FFmpeg's ebur128 against the limits; exit 3 when integrated loudness is off the target by more than
    // the tolerance (either side) or the true peak is above the maximum (docs/decisions/2026-10-31-loudness-verdict.md).
    // Needs SHOTDETECTOR_FFMPEG_LIBS (in-process only, like --audio-filter).
    [Fact]
    public void LoudnessExitsThreeOutOfRange()
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetect-loudness-").FullName;
        try
        {
            string wav = Path.Combine(dir, "tone.wav");
            using (var ffmpeg = Process.Start("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.1*sin(440*2*PI*t)|0.08*sin(660*2*PI*t):s=48000:d=4", "-c:a", "pcm_s16le", wav]))
                ffmpeg.WaitForExit();
            (int ExitCode, System.Text.Json.JsonElement Json) Loudness(params string[] limits)
            {
                var (exitCode, output, err) = Run(["-i", wav, "--ffmpeg-dir", libs, "--loudness-json", .. limits]);
                Assert.True(exitCode is 0 or 3, err);
                return (exitCode, System.Text.Json.JsonDocument.Parse(output).RootElement);
            }

            static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (_, measured) = Loudness();
            double integrated = measured.GetProperty("integrated").GetDouble(), truePeak = measured.GetProperty("truePeak").GetDouble();
            Assert.InRange(integrated, -30, -15);
            string[] peakOk = ["--true-peak-max", N(truePeak + 0.1)];

            Assert.Equal(0, Loudness([.. peakOk, "--loudness-target", N(integrated + 0.4)]).ExitCode);
            Assert.Equal(0, Loudness([.. peakOk, "--loudness-target", N(integrated - 0.4)]).ExitCode);
            var (high, json) = Loudness([.. peakOk, "--loudness-target", N(integrated + 0.6)]);
            Assert.Equal(3, high);
            Assert.False(json.GetProperty("integratedPass").GetBoolean());
            Assert.True(json.GetProperty("truePeakPass").GetBoolean());
            Assert.Equal(3, Loudness([.. peakOk, "--loudness-target", N(integrated - 0.6)]).ExitCode);
            Assert.Equal(0, Loudness([.. peakOk, "--loudness-target", N(integrated + 0.9), "--loudness-tolerance", "1"]).ExitCode);
            var (peak, peakJson) = Loudness("--true-peak-max", N(truePeak - 0.1), "--loudness-target", N(integrated));
            Assert.Equal(3, peak);
            Assert.False(peakJson.GetProperty("truePeakPass").GetBoolean());

            var (exitCode, text, err) = Run("-i", wav, "--ffmpeg-dir", libs, "--loudness", "--loudness-target", N(integrated + 5));
            Assert.True(exitCode == 3, err);
            Assert.Contains("Integrated loudness: ", text, StringComparison.Ordinal);
            Assert.Contains("Loudness: out of range", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // --save-shotchanges writes Timeline.ShotChanges (Subtitle Edit's format; ShotChangesTests reads it as SE does).
    [Fact]
    public void SaveShotChangesWritesTheCutsInSeconds()
    {
        string dir = Directory.CreateTempSubdirectory("shotdetect-shotchanges-").FullName;
        try
        {
            string path = Path.Combine(dir, "three.shotchanges");
            var (exitCode, _, err) = Run("-i", clips.ThreeShots, "-d", "content", "-q", "--save-shotchanges", path);
            Assert.True(exitCode == 0, err);
            Assert.Equal("2.000\n4.000", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // --save-youtube-chapters writes Timeline.YouTubeChapters; fewer than three chapters is an error, with no file.
    [Fact]
    public void SaveYouTubeChaptersWritesALineAChapter()
    {
        string dir = Directory.CreateTempSubdirectory("shotdetect-chapters-").FullName;
        try
        {
            string path = Path.Combine(dir, "chapters.txt");
            var (exitCode, _, err) = Run("-i", YouTubeChaptersTests.LongClip(dir), "-d", "content", "-q", "--save-youtube-chapters", path);
            Assert.True(exitCode == 0, err);
            Assert.Equal("0:00 Chapter 1\n0:12 Chapter 2\n0:24 Chapter 3\n", File.ReadAllText(path));

            string few = Path.Combine(dir, "few.txt");
            (exitCode, _, err) = Run("-i", clips.ThreeShots, "-d", "content", "-q", "--save-youtube-chapters", few, "--chapter-min-length", "10");
            Assert.Equal(1, exitCode);
            Assert.Contains("at least three chapters", err, StringComparison.Ordinal);
            Assert.False(File.Exists(few));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // --contact-sheet: a PNG or PDF by its extension (ContactSheetTests checks the sheets); another extension is a usage
    // error before detecting.
    [Fact]
    public void ContactSheetWritesAPngOrAPdf()
    {
        string dir = Directory.CreateTempSubdirectory("shotdetect-sheet-").FullName;
        try
        {
            string png = Path.Combine(dir, "sheet.png"), pdf = Path.Combine(dir, "sheet.pdf");
            var (exitCode, _, err) = Run("-i", clips.ThreeShots, "-d", "content", "-q", "--contact-sheet", png, "--num-images", "2", "--contact-sheet-width", "100");
            Assert.True(exitCode == 0, err);
            var image = ContactSheet.DecodePng(File.ReadAllBytes(png));
            Assert.Equal((8 + 160 + (2 * 108), 8 + (3 * (75 + 8))), (image.Width, image.Height));
            (exitCode, _, err) = Run("-i", clips.ThreeShots, "-d", "content", "-q", "--contact-sheet", pdf);
            Assert.True(exitCode == 0, err);
            Assert.StartsWith("%PDF-1.4", File.ReadAllText(pdf), StringComparison.Ordinal);

            (exitCode, _, err) = Run("-i", clips.ThreeShots, "--contact-sheet", Path.Combine(dir, "sheet.jpg"));
            Assert.Equal(2, exitCode);
            Assert.Contains("--contact-sheet is a .png or a .pdf file", err, StringComparison.Ordinal);
            Assert.DoesNotContain("frames, processed at", err, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // shotdetect qc: loudness checks against the presets (docs/decisions/2026-11-01-delivery-qc.md), the same ebur128
    // figures as --loudness; exit 3 and the failing checks named when any judged check is out of range. Needs
    // SHOTDETECTOR_FFMPEG_LIBS (in-process only, like --loudness).
    [Fact]
    public void QcJudgesLoudnessAgainstThePresets()
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetect-qc-").FullName;
        try
        {
            string wav = Path.Combine(dir, "tone.wav");
            using (var ffmpeg = Process.Start("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.1*sin(440*2*PI*t)|0.08*sin(660*2*PI*t):s=48000:d=4", "-c:a", "pcm_s16le", wav]))
                ffmpeg.WaitForExit();
            static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (int ExitCode, System.Text.Json.JsonElement Json) Qc(params string[] more)
            {
                var (exitCode, output, err) = Run(["qc", "-i", wav, "--ffmpeg-dir", libs, "--json", "-", .. more]);
                Assert.True(exitCode is 0 or 3, err);
                return (exitCode, System.Text.Json.JsonDocument.Parse(output).RootElement);
            }

            static System.Text.Json.JsonElement Check(System.Text.Json.JsonElement report, string name) =>
                report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
            static string[] Failing(System.Text.Json.JsonElement report) =>
                [.. report.GetProperty("failing").EnumerateArray().Select(f => f.GetString()!)];

            // The figures are --loudness's.
            var (_, loudness, _) = Run("-i", wav, "--ffmpeg-dir", libs, "--loudness-json");
            var expected = System.Text.Json.JsonDocument.Parse(loudness).RootElement;
            var (_, report) = Qc();
            double integrated = expected.GetProperty("integrated").GetDouble(), truePeak = expected.GetProperty("truePeak").GetDouble();
            Assert.Equal(integrated, Check(report, "integrated-loudness").GetProperty("measured").GetDouble());
            Assert.Equal(truePeak, Check(report, "true-peak").GetProperty("measured").GetDouble());
            Assert.Equal(expected.GetProperty("range").GetDouble(), Check(report, "loudness-range").GetProperty("measured").GetDouble());
            Assert.Equal(expected.GetProperty("samplePeak").GetDouble(), Check(report, "sample-peak").GetProperty("measured").GetDouble());
            Assert.Equal("info", Check(report, "loudness-range").GetProperty("status").GetString());

            // Each preset's limits; EBU R128 by default.
            foreach (var (preset, target, tolerance, max) in new[] { ("ebu-r128", -23.0, 0.5, -1.0), ("atsc-a85", -24, 2, -2), ("streaming-14", -14, 1, -1), ("streaming-16", -16, 1, -1) })
            {
                var (_, r) = Qc("--preset", preset);
                Assert.Equal(preset, r.GetProperty("preset").GetString());
                Assert.Equal(target, Check(r, "integrated-loudness").GetProperty("target").GetDouble());
                Assert.Equal(tolerance, Check(r, "integrated-loudness").GetProperty("tolerance").GetDouble());
                Assert.Equal(max, Check(r, "true-peak").GetProperty("max").GetDouble());
            }

            Assert.Equal("ebu-r128", report.GetProperty("preset").GetString());

            // Pass and fail on each side of the tolerance, the preset's or given; the failing checks named.
            var (pass, passing) = Qc("--loudness-target", N(integrated + 0.4));
            Assert.Equal(0, pass);
            Assert.True(passing.GetProperty("pass").GetBoolean());
            Assert.Empty(Failing(passing));
            var (fail, failed) = Qc("--loudness-target", N(integrated + 0.6));
            Assert.Equal(3, fail);
            Assert.Equal(["integrated-loudness"], Failing(failed));
            Assert.Equal(0, Qc("--preset", "atsc-a85", "--loudness-target", N(integrated - 1.5)).ExitCode);
            Assert.Equal(3, Qc("--preset", "atsc-a85", "--loudness-target", N(integrated - 2.5)).ExitCode);
            Assert.Equal(0, Qc("--preset", "streaming-14", "--loudness-target", N(integrated + 0.9)).ExitCode);
            var (both, bothFailed) = Qc("--loudness-target", N(integrated + 5), "--true-peak-max", N(truePeak - 0.1));
            Assert.Equal(3, both);
            Assert.Equal(["integrated-loudness", "true-peak"], Failing(bothFailed));

            // The table on standard output, the JSON in a file.
            string json = Path.Combine(dir, "qc.json");
            var (exitCode, text, err) = Run("qc", "-i", wav, "--ffmpeg-dir", libs, "--json", json, "--loudness-target", N(integrated + 5));
            Assert.True(exitCode == 3, err);
            Assert.Matches(@"(?m)^integrated-loudness +-?\d+\.\d LUFS +.+ FAIL\r?$", text);
            Assert.Contains("qc: FAIL (ebu-r128): integrated-loudness", text, StringComparison.Ordinal);
            Assert.Equal(["integrated-loudness"], Failing(System.Text.Json.JsonDocument.Parse(File.ReadAllText(json)).RootElement));

            // --loudness takes the presets too.
            var (_, viaPreset, _) = Run("-i", wav, "--ffmpeg-dir", libs, "--loudness-json", "--loudness-preset", "atsc-a85");
            var atsc = System.Text.Json.JsonDocument.Parse(viaPreset).RootElement;
            Assert.Equal((-24.0, 2.0, -2.0), (atsc.GetProperty("target").GetDouble(), atsc.GetProperty("tolerance").GetDouble(), atsc.GetProperty("truePeakMax").GetDouble()));

            // Usage errors.
            Assert.Equal(2, Run("qc", "-i", wav, "--checks", "bogus").ExitCode);
            Assert.Equal(2, Run("qc", "-i", wav, "--preset", "bogus").ExitCode);
            Assert.Equal(2, Run("qc").ExitCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // shotdetect qc's picture checks: broadcast range is signalstats' BRNG, YMIN and YMAX over the programme frames (the
    // colour bars left out, docs/decisions/2026-11-01-delivery-qc.md), equal to ffmpeg's on the same frames; bars and
    // tone are reported, and judged with --require-bars-tone. Needs SHOTDETECTOR_FFMPEG_LIBS (with ffmpeg beside them).
    [Fact]
    public void QcChecksBroadcastRangeAndBarsTone()
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetect-qc-picture-").FullName;
        try
        {
            static void Ffmpeg(params string[] args)
            {
                var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
                foreach (string a in (string[])["-v", "error", "-y", .. args])
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, err);
            }

            // 6 s of SMPTE bars with 1 kHz tone, then 4 s of a test pattern with its luma lifted out of range.
            string lineUp = Path.Combine(dir, "lineup.mkv"), plain = Path.Combine(dir, "plain.mkv");
            Ffmpeg("-f",
                "lavfi",
                "-i",
                "smptebars=s=320x240:r=25:d=6",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=s=320x240:r=25:d=4",
                "-f",
                "lavfi",
                "-i",
                "sine=f=1000:r=48000:d=6,volume=-15dB",
                "-f",
                "lavfi",
                "-i",
                "sine=f=440:r=48000:d=4,volume=-20dB",
                "-filter_complex",
                "[1]lutyuv=y=min(val*1.25\\,255)[l];[0][2][l][3]concat=n=2:v=1:a=1[v][a];[v]format=yuv420p[vo]",
                "-map",
                "[vo]",
                "-map",
                "[a]",
                "-c:v",
                "libx264",
                "-qp",
                "0",
                "-c:a",
                "pcm_s16le",
                lineUp);
            Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-vf", "format=yuv420p", "-c:v", "libx264", "-qp", "0", plain);

            (int ExitCode, System.Text.Json.JsonElement Json) Qc(string file, params string[] more)
            {
                var (exitCode, output, err) = Run(["qc", "-i", file, "--ffmpeg-dir", libs, "--json", "-", .. more]);
                Assert.True(exitCode is 0 or 3, err);
                return (exitCode, System.Text.Json.JsonDocument.Parse(output).RootElement);
            }

            static System.Text.Json.JsonElement Check(System.Text.Json.JsonElement report, string name) =>
                report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
            static string[] Failing(System.Text.Json.JsonElement report) =>
                [.. report.GetProperty("failing").EnumerateArray().Select(f => f.GetString()!)];

            // ffmpeg's signalstats on the programme frames (the first 150 are the bars).
            string metadata = Path.Combine(dir, "signalstats.txt");
            Ffmpeg("-noautorotate", "-i", lineUp, "-map", "0:v:0", "-vf", $"signalstats=stat=brng,metadata=mode=print:file={metadata.Replace("\\", "/").Replace(":", "\\:")}", "-f", "null", "-");
            var frames = new List<Dictionary<string, string>>();
            foreach (string line in File.ReadLines(metadata))
            {
                if (line.StartsWith("frame:", StringComparison.Ordinal))
                    frames.Add([]);
                else if (line.IndexOf('=', StringComparison.Ordinal) is int eq and > 0)
                    frames[^1][line[..eq]] = line[(eq + 1)..];
            }

            var programme = frames.Skip(150).ToList();
            double F(Dictionary<string, string> f, string key) => double.Parse(f["lavfi.signalstats." + key], System.Globalization.CultureInfo.InvariantCulture);
            var (fails, report) = Qc(lineUp, "--checks", "range,bars-tone");
            var range = Check(report, "broadcast-range");
            Assert.Equal(3, fails);
            Assert.Equal(["broadcast-range"], Failing(report));
            Assert.Equal(150, range.GetProperty("barsFrames").GetInt32());
            Assert.Equal(250, range.GetProperty("frames").GetInt32());
            Assert.Equal(programme.Max(f => F(f, "BRNG")), range.GetProperty("measured").GetDouble(), 6);
            Assert.Equal(programme.Count(f => F(f, "BRNG") > 0.01), range.GetProperty("framesOver").GetInt32());
            Assert.Equal(programme.Min(f => F(f, "YMIN")), range.GetProperty("yMin").GetInt32());
            Assert.Equal(programme.Max(f => F(f, "YMAX")), range.GetProperty("yMax").GetInt32());
            Assert.Equal(6.84, range.GetProperty("at").GetDouble(), 6);
            Assert.Equal(0, Qc(lineUp, "--checks", "range", "--brng-max", "16").ExitCode);

            // Bars and tone: reported, not judged, unless required.
            var barsTone = Check(report, "bars-tone");
            Assert.Equal("info", barsTone.GetProperty("status").GetString());
            var bars = Assert.Single(barsTone.GetProperty("bars").EnumerateArray());
            Assert.Equal((0.0, 6.0), (bars.GetProperty("start").GetDouble(), bars.GetProperty("end").GetDouble()));
            var tone = Assert.Single(barsTone.GetProperty("tone").EnumerateArray());
            Assert.InRange(tone.GetProperty("start").GetDouble(), 0, 0.5);
            Assert.InRange(tone.GetProperty("end").GetDouble(), 5.5, 6.5);
            Assert.Equal("pass", Check(Qc(lineUp, "--checks", "bars-tone", "--require-bars-tone").Json, "bars-tone").GetProperty("status").GetString());

            // A file without line-up passes the range and fails only when bars and tone are required; no audio, no loudness.
            var (plainExit, plainReport) = Qc(plain);
            Assert.Equal(0, plainExit);
            Assert.Equal("pass", Check(plainReport, "broadcast-range").GetProperty("status").GetString());
            Assert.Equal("info", Check(plainReport, "loudness").GetProperty("status").GetString());
            var (required, requiredReport) = Qc(plain, "--require-bars-tone");
            Assert.Equal(3, required);
            Assert.Equal(["bars-tone"], Failing(requiredReport));
            Assert.Equal(2, Run("qc", "-i", plain, "--brng-max", "101").ExitCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // shotdetect qc's black check: blackdetect's spans from the detection pass (DetectionOptions.BlackDetect), equal to
    // ffmpeg's; only a span away from the first and last --edge seconds fails (docs/decisions/2026-11-01-delivery-qc.md).
    // Needs SHOTDETECTOR_FFMPEG_LIBS.
    [Fact]
    public void QcJudgesBlackOnlyMidProgramme()
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetect-qc-black-").FullName;
        try
        {
            // 30 s: black 0-3 s, picture, black 14-17 s, picture, black 27-30 s.
            string clip = Path.Combine(dir, "black.mkv");
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string a in (string[])["-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=160x120:r=10:d=3", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=10:d=11",
                "-f", "lavfi", "-i", "color=black:s=160x120:r=10:d=3", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=10:d=10", "-f", "lavfi", "-i", "color=black:s=160x120:r=10:d=3",
                "-filter_complex", "[0][1][2][3][4]concat=n=5:v=1:a=0,format=yuv420p", "-c:v", "libx264", "-qp", "0", clip])
            {
                psi.ArgumentList.Add(a);
            }

            using (var p = Process.Start(psi)!)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, err);
            }

            (int ExitCode, System.Text.Json.JsonElement Black) Qc(params string[] more)
            {
                var (exitCode, output, err) = Run(["qc", "-i", clip, "--ffmpeg-dir", libs, "--checks", "black", "--json", "-", .. more]);
                Assert.True(exitCode is 0 or 3, err);
                return (exitCode, System.Text.Json.JsonDocument.Parse(output).RootElement.GetProperty("checks")[0]);
            }

            // ffmpeg's blackdetect (d=2) on the same file.
            var log = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string a in (string[])["-hide_banner", "-nostats", "-i", clip, "-vf", "blackdetect=d=2", "-f", "null", "-"])
                log.ArgumentList.Add(a);
            string text;
            using (var p = Process.Start(log)!)
            {
                text = p.StandardError.ReadToEnd();
                p.WaitForExit();
            }

            var expected = Regex.Matches(text, @"black_start:(\S+) black_end:(\S+)")
                .Select(m => (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();
            Assert.Equal(3, expected.Count);

            var (exitCode, black) = Qc();
            Assert.Equal(3, exitCode);
            Assert.Equal("fail", black.GetProperty("status").GetString());
            var spans = black.GetProperty("spans").EnumerateArray().ToList();
            Assert.Equal(expected, spans.Select(b => (Math.Round(b.GetProperty("start").GetDouble(), 6), Math.Round(b.GetProperty("end").GetDouble(), 6))));
            Assert.Equal([false, true, false], spans.Select(b => b.GetProperty("judged").GetBoolean()));
            Assert.Equal(1, black.GetProperty("judgedSpans").GetInt32());

            // A wider edge takes the middle span into the head; a longer minimum finds none.
            Assert.Equal(0, Qc("--edge", "15").ExitCode);
            var (longer, none) = Qc("--black-min", "4");
            Assert.Equal(0, longer);
            Assert.Empty(none.GetProperty("spans").EnumerateArray());
            Assert.Equal(2, Run("qc", "-i", clip, "--edge", "-1").ExitCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
