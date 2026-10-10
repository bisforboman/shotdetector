using System.Diagnostics;
using ShotDetector;

/// <summary>
/// Reference tone in ShotDetector: finding it, and TrimReferenceTone as the time range it stands for. The audio is
/// read in-process: SHOTDETECTOR_FFMPEG_LIBS names an FFmpeg 8.1 shared build's folder (CI sets it); without it the
/// tests that read files do nothing.
/// </summary>
public sealed class ReferenceToneTests : IDisposable
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-tone-").FullName;

    // Bars and -18 dBFS tone, and programme: two test patterns (a cut between them) over noise.
    const string Bars = "-f lavfi -i smptebars=s=320x240:r=25:d={0} -f lavfi -i sine=f=1000:r=48000:d={0}";
    const string Programme = "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i anoisesrc=r=48000:a=0.1:d=2 " +
        "-f lavfi -i mandelbrot=s=320x240:r=25 -f lavfi -i anoisesrc=r=48000:a=0.1:c=pink:d=2";

    DetectionOptions Options => new() { FfmpegDirectory = Libs, Detector = DetectorKind.Content };

    string Make(string name, string inputs, string concat)
    {
        string path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in $"-v error -y {inputs} -filter_complex {concat} -map [v] -map [a] -c:v mpeg4 -q:v 2 -c:a aac".Split(' '))
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, true);

    static void Same(DetectionResult expected, DetectionResult actual) =>
        Assert.Equal(expected.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)), actual.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));

    [Fact]
    public void LeadingToneIsTrimmed()
    {
        if (Libs is null)
            return;
        string path = Make("lead.mp4", string.Format(Bars, 6) + " " + Programme,
            "[4]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][2:v][3:a][m][5:a]concat=n=3:v=1:a=1[v][a]");
        var tone = Assert.Single(ShotDetection.FindReferenceTone(path, Options));
        Assert.InRange(tone.Start.TotalSeconds, 0, 0.1);
        Assert.InRange(tone.End.TotalSeconds, 5.9, 6.1);
        Assert.InRange(tone.Level, -18.5, -17.5);

        var trimmed = ShotDetection.Detect(path, Options with { TrimReferenceTone = true });
        Assert.Equal(tone, Assert.Single(trimmed.ReferenceTone!));
        Same(ShotDetection.Detect(path, Options with { StartTime = FormattableString.Invariant($"{tone.End.TotalSeconds:0.###}s") }), trimmed);
        // The programme's first frame starts the first shot: the bars are gone.
        Assert.Equal(150, trimmed.Shots[0].Start.FrameNum);
        Assert.Equal(2, trimmed.Shots.Count);
    }

    [Fact]
    public void TrailingToneIsTrimmed()
    {
        if (Libs is null)
            return;
        string path = Make("trail.mp4", Programme + " " + string.Format(Bars, 6),
            "[2]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][m][3:a][4:v][5:a]concat=n=3:v=1:a=1[v][a]");
        var tone = Assert.Single(ShotDetection.FindReferenceTone(path, Options));
        Assert.InRange(tone.Start.TotalSeconds, 3.9, 4.1);
        var trimmed = ShotDetection.Detect(path, Options with { TrimReferenceTone = true });
        Assert.Equal(2, trimmed.Shots.Count);
        Assert.Equal(100, trimmed.Shots[^1].End.FrameNum);
    }

    [Fact]
    public void OffChangesNothing()
    {
        if (Libs is null)
            return;
        string path = Make("none.mp4", Programme, "[2]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][m][3:a]concat=n=2:v=1:a=1[v][a]");
        Assert.Empty(ShotDetection.FindReferenceTone(path, Options));
        var plain = ShotDetection.Detect(path, Options);
        Assert.Null(plain.ReferenceTone);
        var trimmed = ShotDetection.Detect(path, Options with { TrimReferenceTone = true });
        Assert.Empty(trimmed.ReferenceTone!);
        Same(plain, trimmed);
    }

    [Fact]
    public void AudioOnlyFile()
    {
        if (Libs is null)
            return;
        string path = Path.Combine(_dir, "lineup.wav");
        var psi = new ProcessStartInfo(Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
        foreach (var a in "-v error -y -f lavfi -i sine=f=1000:r=48000:d=8 -f lavfi -i anoisesrc=r=48000:a=0.1:d=4 -filter_complex [0][1]concat=n=2:v=0:a=1".Split(' '))
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        using (var p = Process.Start(psi)!)
            p.WaitForExit();
        var tone = Assert.Single(ShotDetection.FindReferenceTone(path, Options));
        Assert.InRange(tone.End.TotalSeconds, 7.9, 8.1);
    }

    [Fact]
    public void TrimNeedsAFileAndItsOwnRange()
    {
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(new MemoryStream(), Options with { TrimReferenceTone = true }));
        if (Libs is null)
            return;
        string path = Make("range.mp4", Programme, "[2]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][m][3:a]concat=n=2:v=1:a=1[v][a]");
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(path, Options with { TrimReferenceTone = true, StartTime = "1s" }));
    }

    [Fact]
    public void OnlyTheEndsAreTrimmed()
    {
        static ReferenceToneSegment S(double start, double end) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), -18);
        var o = new DetectionOptions();
        var t = ShotDetection.TrimmedToTone(o, [S(1, 31.5), S(100, 110), S(596, 600)], TimeSpan.FromSeconds(600));
        Assert.Equal(("31.5s", "596s"), (t.StartTime, t.EndTime));
        // Starting after 5 s, ending more than 5 s before the end: programme time, left alone.
        t = ShotDetection.TrimmedToTone(o, [S(6, 40), S(500, 590)], TimeSpan.FromSeconds(600));
        Assert.Equal((null, null), (t.StartTime, t.EndTime));
        // One segment over the whole file is leading only.
        t = ShotDetection.TrimmedToTone(o, [S(0, 600)], TimeSpan.FromSeconds(600));
        Assert.Equal(("600s", null), (t.StartTime, t.EndTime));
        // A short file: tone from 4 s to the end is trailing, though it starts in the first 5 s.
        t = ShotDetection.TrimmedToTone(o, [S(4, 10)], TimeSpan.FromSeconds(10));
        Assert.Equal((null, "4s"), (t.StartTime, t.EndTime));
        Assert.Same(o, ShotDetection.TrimmedToTone(o, [], TimeSpan.FromSeconds(600)));
    }
}
