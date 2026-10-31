using System.Diagnostics;
using System.Globalization;
using ShotDetector;

/// <summary>
/// Timeline.ShotChanges: Subtitle Edit's .shotchanges text, read back with SE's own parsing (SE5, SubtitleEdit/subtitleedit
/// main at 8c6fc9d, MIT): the cache file's reader and the Import shot changes dialog's (docs/ideas/subtitle-edit.md).
/// </summary>
public class ShotChangesTests(Clips clips) : IClassFixture<Clips>
{
    /// <summary>src/ui/Logic/Media/ShotChangesHelper.cs, FromDisk: every non-blank line, double.Parse invariant, sorted.</summary>
    static List<double> SeFromDisk(string text)
    {
        var list = new List<double>();
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                list.Add(double.Parse(line, CultureInfo.InvariantCulture));
        }

        list.Sort();
        return list;
    }

    /// <summary>
    /// src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs, LoadTextFile then Ok, for a plain text file: the text
    /// trimmed; seconds when it has a '.' (else the unit last used, here frames, the case that goes wrong); each non-blank
    /// line with ',' as '.', double.TryParse(AllowDecimalPoint, invariant). Null when SE wouldn't read it as seconds.
    /// </summary>
    static List<double>? SeImport(string text)
    {
        string s = text.Trim();
        if (!s.Contains('.', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal))
            return null;
        var list = new List<double>();
        foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string l = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(l))
                continue;
            Assert.True(double.TryParse(l.Replace(",", ".", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double d), l);
            list.Add(d);
        }

        return list;
    }

    static void RoundTrips(DetectionResult r)
    {
        string text = Timeline.ShotChanges(r);
        double[] expected = [.. r.Cuts.Select(c => Math.Round(c.Seconds, 3, MidpointRounding.AwayFromZero))];
        Assert.Equal(expected, SeFromDisk(text));
        Assert.Equal(expected, SeImport(text));
        foreach (var (cut, seconds) in r.Cuts.Zip(expected))
            Assert.InRange(Math.Abs(seconds - cut.Seconds), 0, 0.0005);
    }

    [Fact]
    public void OneTimePerCutInSeconds()
    {
        // Cuts at 2 s and 4 s (frames 50 and 100) of a 6 s, 25 fps clip: whole seconds keep their '.'.
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        Assert.Equal("2.000\n4.000", Timeline.ShotChanges(r));
        RoundTrips(r);
    }

    [Fact]
    public void NoCutsIsAnEmptyFile()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content, Threshold = 255 });
        Assert.Empty(r.Cuts);
        Assert.Equal("", Timeline.ShotChanges(r));
    }

    // Variable frame rate: 2 s at 25 fps, 2 s at 10 fps, 2 s at 25 fps. Each line is the cut frame's time (2 s and 4 s),
    // not a frame count over one rate (the cuts are the 51st and 71st frames; the file says 50 fps, no average).
    [Fact]
    public void VariableFrameRateTimesAreTheFramesOwn()
    {
        string dir = Directory.CreateTempSubdirectory("shotchanges-vfr-").FullName;
        try
        {
            string path = Path.Combine(dir, "vfr.mkv");
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string a in (string[])["-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=2", "-f", "lavfi", "-i", "smptebars=s=320x240:r=10:d=2",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2",
                "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0,format=yuv420p,settb=1/50", "-fps_mode", "passthrough",
                "-c:v", "mpeg4", "-q:v", "2", path])
            {
                psi.ArgumentList.Add(a);
            }

            using (var p = Process.Start(psi)!)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, err);
            }

            var r = ShotDetection.Detect(path, new() { Detector = DetectorKind.Content });
            Assert.Equal("2.000\n4.000", Timeline.ShotChanges(r));
            RoundTrips(r);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
