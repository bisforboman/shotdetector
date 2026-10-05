using System.Diagnostics;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>Synthetic clips made with ffmpeg (which must be on PATH, as for the library itself).</summary>
public sealed class Clips : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-tests-").FullName;

    /// <summary>Hard cuts at frames 50 and 100 between three test patterns; 150 frames at 25 fps.</summary>
    public string ThreeShots { get; }

    /// <summary><see cref="ThreeShots"/> with a 90° rotation tag, as phones store portrait video.</summary>
    public string Rotated { get; }

    /// <summary>5 minutes of moving test pattern: a full run takes seconds, so it can be cancelled mid-run.</summary>
    public string Long { get; }

    /// <summary><see cref="ThreeShots"/> as mkv and as an mp4 with its headers first: both can be piped.</summary>
    public string ThreeShotsMkv { get; }
    public string ThreeShotsFaststart { get; }

    public Clips()
    {
        ThreeShots = Make("three.mp4",
            "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i smptebars=s=320x240:r=25:d=2 -f lavfi -i mandelbrot=s=320x240:r=25 " +
            "-filter_complex [2]trim=duration=2,setpts=PTS-STARTPTS[m];[0][1][m]concat=n=3:v=1:a=0,format=yuv420p");
        Rotated = Path.Combine(_dir, "rotated.mp4");
        Run($"-v error -y -display_rotation 90 -i {ThreeShots} -c copy {Rotated}");
        Long = Make("long.mp4", "-f lavfi -i testsrc2=s=640x360:r=25:d=300 -vf format=yuv420p");
        ThreeShotsMkv = Path.Combine(_dir, "three.mkv");
        Run($"-v error -y -i {ThreeShots} -c copy {ThreeShotsMkv}");
        ThreeShotsFaststart = Path.Combine(_dir, "three-faststart.mp4");
        Run($"-v error -y -i {ThreeShots} -c copy -movflags +faststart {ThreeShotsFaststart}");
    }

    string Make(string name, string input)
    {
        string path = Path.Combine(_dir, name);
        // mpeg4 is built into every ffmpeg, unlike libx264.
        Run($"-v error -y {input} -r 25 -c:v mpeg4 -q:v 3 {path}");
        return path;
    }

    static void Run(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"ffmpeg failed: {err}");
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
    public void RotatedVideoIsReadUpright()
    {
        var result = ShotDetection.Detect(clips.Rotated, new DetectionOptions { Detector = DetectorKind.Content });
        Assert.Equal((240, 320), (result.Video.SourceWidth, result.Video.SourceHeight));
        Assert.Equal([0L, 50L, 100L], result.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void FastYuvGivesTheSameShots()
    {
        var core = ShotDetection.Detect(clips.ThreeShots);
        var fast = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.Equal(FramePipeline.Yuv420Sampled, fast.Video.Pipeline);
        Assert.Equal(Shots.Csv(core.Shots), Shots.Csv(fast.Shots));
    }

    sealed class Recorder : IProgress<DetectionProgress>
    {
        public List<DetectionProgress> Reports { get; } = [];
        public void Report(DetectionProgress value) => Reports.Add(value);
    }

    [Fact]
    public void ReportsProgressUpToAllFrames()
    {
        var recorder = new Recorder();
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Progress = recorder });
        Assert.NotEmpty(recorder.Reports);
        Assert.Equal(new DetectionProgress(150, 150), recorder.Reports[^1]);
        Assert.Equal(1.0, recorder.Reports[^1].Fraction);
        Assert.True(recorder.Reports.Zip(recorder.Reports.Skip(1)).All(p => p.First.FramesProcessed <= p.Second.FramesProcessed));
        Assert.Equal(150, result.FrameCount);
    }

    [Fact]
    public void FindsFfmpegInTheGivenDirectory()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string dir = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .First(d => File.Exists(Path.Combine(d, exe)));
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = dir });
        Assert.Equal(3, result.Shots.Count);
    }

    [Fact]
    public void MissingFfmpegSaysWhatToDo()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Path.GetTempPath() }));
        Assert.Contains("FfmpegDirectory", e.Message);
    }

    [Theory]
    [InlineData(DetectorKind.Adaptive)]
    [InlineData(DetectorKind.Content)]
    [InlineData(DetectorKind.Threshold)]
    public async Task StreamingGivesTheSameShotsAsDetect(DetectorKind kind)
    {
        var options = new DetectionOptions { Detector = kind, MinSceneLength = "5" };
        var expected = ShotDetection.Detect(clips.ThreeShots, options).Shots;
        var streamed = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(clips.ThreeShots, options))
            streamed.Add(shot);
        Assert.Equal(expected, streamed);
    }

    [Fact]
    public async Task StreamingYieldsShotsBeforeTheEnd()
    {
        // The long clip has no cuts, so use the three-shot clip and check the first shot arrives
        // as its own item, then stop early: the background decoding must stop too.
        await foreach (var shot in ShotDetection.DetectStreamAsync(clips.ThreeShots))
        {
            Assert.Equal(1, shot.Number);
            Assert.Equal(50, shot.End.FrameNum);
            break;
        }
    }

    [Fact]
    public async Task CancellingStopsStreaming()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ShotDetection.DetectStreamAsync(clips.Long, cancellationToken: cts.Token)) { }
        });
    }

    [Fact]
    public async Task StreamingReportsErrors() =>
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in ShotDetection.DetectStreamAsync("does-not-exist.mp4")) { }
        });

    [Theory]
    [InlineData("mkv")]
    [InlineData("faststart")]
    public void PipedInputGivesTheSameShotsAsTheFile(string container)
    {
        var expected = ShotDetection.Detect(clips.ThreeShots);
        using var file = File.OpenRead(container == "mkv" ? clips.ThreeShotsMkv : clips.ThreeShotsFaststart);
        var piped = ShotDetection.Detect(file);
        Assert.True(piped.Video.Streaming);
        Assert.Null(piped.VideoPath);
        Assert.Equal(expected.Shots.Select(s => (s.Start.Timecode(), s.End.Timecode())), piped.Shots.Select(s => (s.Start.Timecode(), s.End.Timecode())));
        Assert.Equal(expected.FrameCount, piped.FrameCount);
    }

    [Fact]
    public async Task PipedInputStreamsShotsOut()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var shots = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(file))
            shots.Add(shot);
        Assert.Equal([0L, 50L, 100L], shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void PipedInputHonoursTheEndTime()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var r = ShotDetection.Detect(file, new() { EndTime = "3s" });
        Assert.Equal(75, r.FrameCount);
        Assert.Equal(75, r.Shots[^1].End.FrameNum);
    }

    [Fact]
    public void PipedInputCannotSeek()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(file, new() { StartTime = "1s" }));
    }

    [Fact]
    public void PipedMp4WithoutFaststartIsRefusedClearly()
    {
        using var file = File.OpenRead(clips.ThreeShots);
        var e = Assert.Throws<InvalidOperationException>(() => ShotDetection.Detect(file));
        Assert.Contains("faststart", e.Message);
    }

    [Fact]
    public void UrlInputIsStreamed()
    {
        using var server = new System.Net.HttpListener();
        int port = Random.Shared.Next(20000, 60000);
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                var ctx = await server.GetContextAsync();
                ctx.Response.ContentType = "video/mp4";
                try
                {
                    await using var f = File.OpenRead(clips.ThreeShotsFaststart);
                    await f.CopyToAsync(ctx.Response.OutputStream);
                    ctx.Response.Close();
                }
                catch (System.Net.HttpListenerException) { } // ffprobe hangs up after the headers
            }
        });
        try
        {
            var r = ShotDetection.Detect($"http://localhost:{port}/three.mp4");
            Assert.True(r.Video.Streaming);
            Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void ExportsNeedAPath()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var r = ShotDetection.Detect(file);
        Assert.Throws<InvalidOperationException>(() => Export.SaveImages(r, Path.GetTempPath()));
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
