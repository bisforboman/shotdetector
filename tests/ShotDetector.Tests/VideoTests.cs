using System.Diagnostics;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>Synthetic clips made with ffmpeg (which must be on PATH, as for the library itself).</summary>
public sealed class Clips : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-tests-").FullName;

    /// <summary>Hard cuts at frames 50 and 100 between three test patterns; 150 frames at 25 fps.</summary>
    public string ThreeShots { get; }

    /// <summary>5 minutes of moving test pattern: a full run takes seconds, so it can be cancelled mid-run.</summary>
    public string Long { get; }

    public Clips()
    {
        ThreeShots = Make("three.mp4",
            "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i smptebars=s=320x240:r=25:d=2 -f lavfi -i mandelbrot=s=320x240:r=25 " +
            "-filter_complex [2]trim=duration=2,setpts=PTS-STARTPTS[m];[0][1][m]concat=n=3:v=1:a=0,format=yuv420p");
        Long = Make("long.mp4", "-f lavfi -i testsrc2=s=640x360:r=25:d=300 -vf format=yuv420p");
    }

    string Make(string name, string input)
    {
        string path = Path.Combine(_dir, name);
        // mpeg4 is built into every ffmpeg, unlike libx264.
        var psi = new ProcessStartInfo("ffmpeg", $"-v error -y {input} -r 25 -c:v mpeg4 -q:v 3 {path}") { RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"ffmpeg failed: {err}");
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class VideoTests(Clips clips) : IClassFixture<Clips>
{
    [Fact]
    public void FindsTheHardCuts()
    {
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Detector = DetectorKind.Content });
        Assert.Equal([0L, 50L, 100L], result.Shots.Select(s => s.Start.FrameNum));
        Assert.Equal(150, result.FrameCount);
    }

    [Fact]
    public void FastYuvGivesTheSameShots()
    {
        var core = ShotDetection.Detect(clips.ThreeShots);
        var fast = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.Equal("yuv420p+sampled", fast.Video.Pipeline);
        Assert.Equal(Shots.Csv(core.Shots), Shots.Csv(fast.Shots));
    }

    [Fact]
    public void AlreadyCancelledTokenThrowsImmediately() =>
        Assert.Throws<OperationCanceledException>(() => ShotDetection.Detect(clips.Long, cancellationToken: new CancellationToken(true)));

    [Fact]
    public void CancellingStopsDetection()
    {
        // A full run takes several times longer than this; without cancellation Detect would return normally.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsAny<OperationCanceledException>(() => ShotDetection.Detect(clips.Long, cancellationToken: cts.Token));
    }

    [Fact]
    public void CancellingStopsTheFrameStreamAtTheNextFrame()
    {
        using var cts = new CancellationTokenSource();
        int read = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var _ in new VideoReader(clips.Long).Frames(cts.Token))
                if (++read == 10)
                    cts.Cancel();
        });
        Assert.Equal(10, read);
    }

    [Fact]
    public void CancellingStopsExport()
    {
        var result = ShotDetection.Detect(clips.ThreeShots);
        string dir = Directory.CreateTempSubdirectory("shotdetector-split-").FullName;
        try
        {
            Assert.Throws<OperationCanceledException>(() => Export.SplitVideo(result, dir, new CancellationToken(true)));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
