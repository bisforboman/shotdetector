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
}
