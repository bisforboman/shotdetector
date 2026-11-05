using System.Diagnostics;
using FrameReader;

/// <summary>
/// FrameSteps on MediaProbe's packet timestamps (ffprobe's, checked in MediaProbeTests): a clip with frames 20 to 22
/// left out (select, timestamps kept) has one gap of 3 there; the whole clip none. And on timestamps made here: steps
/// that aren't a frame, rounding, null entries, any order.
/// </summary>
public class FrameStepsTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Theory]
    [InlineData("testsrc2=s=160x90:r=25:d=4,select=not(between(n\\,20\\,22))", 1, 3, 1)]
    [InlineData("testsrc2=s=160x90:r=25:d=4", 0, 0, 0)]
    public void FindsGapsInAFile(string source, int gaps, long missing, int irregular)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-steps-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mov");
            var psi = new ProcessStartInfo(Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
            foreach (string a in (string[])["-v", "error", "-y", "-f", "lavfi", "-i", source, "-c:v", "mjpeg", "-fps_mode", "passthrough", path])
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!)
            {
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, error);
            }

            var info = MediaProbe.Probe(path, new ProbeOptions { PacketTimestamps = true });
            var steps = FrameSteps.Of(info.PacketTimestamps!, info.Video!.TimeBase, info.Video.FrameRate);
            Assert.Equal((gaps, missing, irregular), (steps.Gaps.Count, steps.Missing, steps.Irregular));

            Assert.Equal(info.PacketTimestamps!.Count - 1, steps.Steps);
            if (gaps == 1)
            {
                double tb = info.Video.TimeBase.Value;
                Assert.Equal(19 / 25.0, steps.Gaps[0].BeforePts * tb, 1e-9);
                Assert.Equal(23 / 25.0, steps.Gaps[0].AfterPts * tb, 1e-9);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadsTimestampsOfYours()
    {
        // 30000/1001 in a 1/30000 time base: a step of 1001 is a frame; 2.4 frames is 1 missing, 2.6 is 2; 1.6 a gap of 1.
        var steps = FrameSteps.Of([5005, null, 0, 1001, 2002, 4004, 3003, 7407, 10010, 11612], new Rational(1, 30000), new Rational(30000, 1001));
        Assert.Equal(8, steps.Steps);
        Assert.Equal([new FrameGap(5005, 7407, 1), new FrameGap(7407, 10010, 2), new FrameGap(10010, 11612, 1)], steps.Gaps);
        Assert.Equal(4, steps.Missing);
        Assert.Equal(3, steps.Irregular);
        Assert.Equal(new FrameSteps(0, 0, []).Steps, FrameSteps.Of([], new Rational(1, 25), new Rational(25, 1)).Steps);
        Assert.Throws<ArgumentException>(() => FrameSteps.Of([0], new Rational(0, 1), new Rational(25, 1)));
    }
}
