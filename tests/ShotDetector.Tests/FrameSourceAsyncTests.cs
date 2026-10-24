using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// <see cref="ShotDetection.DetectAsync(YuvVideo, IAsyncEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>: the
/// synchronous frame-source Detect's result for the same frames, pulled the same way (one frame per step, none ahead),
/// cancellable between frames. A file's decoded frames are compared in FrameInputTests (with SHOTDETECTOR_FFMPEG_LIBS);
/// these need nothing.
/// </summary>
public class FrameSourceAsyncTests
{
    /// <summary>Records what's reported on the reporting thread (System.Progress would post it elsewhere).</summary>
    sealed class Told<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    static YuvVideo Video() => new() { Width = 64, Height = 48, FrameRate = new(25, 1) };

    /// <summary>Three shots of noise (cuts at frames 25 and 50), with padded rows; timed (pts at 1/100 s) when asked.</summary>
    static IEnumerable<YuvFrame> Frames(YuvVideo video, int count = 75)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        for (int i = 0; i < count; i++)
        {
            var random = new Random((i / 25) + 1);
            byte[] Plane(int width, int height)
            {
                var plane = new byte[(width + 3) * height];
                random.NextBytes(plane);
                return plane;
            }

            yield return new YuvFrame(Plane(w, h), w + 3, Plane(cw, ch), cw + 3, Plane(cw, ch), cw + 3) { Pts = i * 4 };
        }
    }

    /// <summary>The frames as an asynchronous source that yields to its caller every <paramref name="every"/> frames.</summary>
    internal static async IAsyncEnumerable<YuvFrame> Yielding(IEnumerable<YuvFrame> frames, int every = 3, Action? pulled = null)
    {
        int i = 0;
        foreach (var frame in frames)
        {
            if (i++ % every == 0)
                await Task.Yield();
            pulled?.Invoke();
            yield return frame;
        }
    }

    public static TheoryData<string> Cases => ["adaptive", "content", "histogram", "hash", "timed", "start", "start-late", "end", "skip", "drop", "several", "v071", "parallel", "empty"];

    static (YuvVideo Video, DetectionOptions Options, int Count) Case(string name)
    {
        var video = Video();
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = name != "skip" };
        options = name switch
        {
            "content" => options with { Detector = DetectorKind.Content },
            "histogram" => options with { Detector = DetectorKind.Histogram },
            "hash" => options with { Detector = DetectorKind.Hash },
            "start" => options with { Detector = DetectorKind.Content, StartTime = "0.6" },
            "start-late" => options with { Detector = DetectorKind.Content, StartTime = "60" },
            "end" => options with { Detector = DetectorKind.Content, StartTime = "0.4", EndTime = "2.2" },
            "skip" => options with { Detector = DetectorKind.Content, FrameSkip = 2 },
            "drop" => options with { Detectors = [new(DetectorKind.Content), new(DetectorKind.Histogram)], DropShortScenes = true, MinSceneLength = "30" },
            "several" => options with { Detectors = [new(DetectorKind.Content), new(DetectorKind.Threshold) { Threshold = 40 }] },
            "v071" => options with { Compatibility = PySceneDetectVersion.V0_7_1 },
            "parallel" => options with { Mode = DetectionMode.Parallel },
            _ => options,
        };
        if (name is "timed" or "start" or "end")
            video = video with { TimeBase = new(1, 100) };
        if (name == "start-late")
            video = video with { FrameCount = 75 }; // the start past the last frame: the last
        return (video, options, name == "empty" ? 0 : 75);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SameAsTheSynchronousDetect(string name)
    {
        var (video, options, count) = Case(name);

        // The shots told, progress (at fixed steps), warnings and frames pulled, as each overload reports them.
        async Task<(DetectionResult Result, List<Shot> Shots, List<DetectionProgress> Progress, List<string> Warnings, int Pulled)> Run(bool async)
        {
            var (shots, progress, warnings) = (new Told<Shot>(), new Told<DetectionProgress>(), new Told<string>());
            var told = options with { OnShot = shots, Progress = progress, ProgressStep = 0.1, Warnings = warnings };
            int pulled = 0;
            var frames = Frames(video, count).Select(f =>
            {
                pulled++;
                return f;
            });
            var result = async ? await ShotDetection.DetectAsync(video, Yielding(frames), told) : ShotDetection.Detect(video, frames, told);
            return (result, shots.Values, progress.Values, warnings.Values, pulled);
        }

        var sync = await Run(async: false);
        var ours = await Run(async: true);
        Assert.Equal(sync.Result.FrameCount, ours.Result.FrameCount);
        Assert.Equal(sync.Result.MinSceneLengthFrames, ours.Result.MinSceneLengthFrames);
        Assert.Equal(sync.Result.Cuts, ours.Result.Cuts);
        Assert.Equal(sync.Result.Shots, ours.Result.Shots);
        Assert.Equal(Shots.Csv(sync.Result.Shots, cuts: sync.Result.Cuts), Shots.Csv(ours.Result.Shots, cuts: ours.Result.Cuts));
        Assert.Equal(sync.Result.Stats?.Csv(sync.Result.Video.Position), ours.Result.Stats?.Csv(ours.Result.Video.Position));
        Assert.Equal((sync.Result.Video.ProcessedWidth, sync.Result.Video.ProcessedHeight, sync.Result.Video.FrameRate, sync.Result.Video.OpenCvFrameCount, sync.Result.Video.Duration, sync.Result.Video.DecodedInProcess),
            (ours.Result.Video.ProcessedWidth, ours.Result.Video.ProcessedHeight, ours.Result.Video.FrameRate, ours.Result.Video.OpenCvFrameCount, ours.Result.Video.Duration, ours.Result.Video.DecodedInProcess));
        Assert.Equal(sync.Shots, ours.Shots);
        Assert.Equal(sync.Result.Shots, ours.Shots);
        Assert.Equal(sync.Progress, ours.Progress);
        Assert.Equal(sync.Warnings, ours.Warnings);
        Assert.Equal(sync.Result.Warnings, ours.Result.Warnings);
        Assert.Equal(sync.Pulled, ours.Pulled);
        if (name == "parallel")
            Assert.Contains(ours.Warnings, w => w.Contains("frames from the caller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PullsOneFramePerStepAndNoneAhead()
    {
        // The frames before the start time, then one per analysed frame; none after the end.
        var video = Video() with { TimeBase = new(1, 100) };
        int pulled = 0, pulledWhenToldFirstShot = -1;
        var options = new DetectionOptions
        {
            Yuv420Converter = new SwscaleYuv420(),
            Detector = DetectorKind.Content,
            StartTime = "0.4",
            EndTime = "2.2",
            OnShot = new SynchronousProgress<Shot>(_ => { if (pulledWhenToldFirstShot < 0) pulledWhenToldFirstShot = pulled; }),
        };
        var result = await ShotDetection.DetectAsync(video, Yielding(Frames(video), pulled: () => pulled++), options);
        Assert.Equal(10 + result.FrameCount, pulled); // 0.4 s is frame 10, its frame and those before it
        Assert.Equal(45, result.FrameCount);          // frames 10 to 54: the next would reach 2.2 s (frame 55)

        // The shot ending at the cut at frame 25 is told while frame 25 is analysed: frames 0 to 25 pulled, no more.
        Assert.Equal(26, pulledWhenToldFirstShot);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(10, null)]
    [InlineData(40, null)]
    [InlineData(10, "2.5")] // while the frames before the start are taken
    public async Task CancelsBetweenFrames(int after, string? startTime)
    {
        var video = Video();
        using var cancel = new CancellationTokenSource();
        int pulled = 0;
        async IAsyncEnumerable<YuvFrame> Source()
        {
            foreach (var frame in Frames(video))
            {
                // The page's loop: a frame, then a turn of the event loop, where the "cancel" message arrives; the
                // source doesn't check the token itself, so it gives the next frame anyway.
                pulled++;
                yield return frame;
                await Task.Yield();
                if (pulled == after)
                    await cancel.CancelAsync();
            }
        }

        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), StartTime = startTime };
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShotDetection.DetectAsync(video, Source(), options, cancel.Token));
        Assert.Equal(cancel.Token, e.CancellationToken);
        Assert.InRange(pulled, after, after + 1);
    }

    [Fact]
    public void SynchronousDetectCancelsBeforeEachPull()
    {
        // Cancelled while the frames before the start time are taken: no frame is pulled after it.
        var video = Video();
        using var cancel = new CancellationTokenSource();
        int pulled = 0;
        IEnumerable<YuvFrame> Source()
        {
            foreach (var frame in Frames(video))
            {
                pulled++;
                yield return frame;
                if (pulled == 10)
                    cancel.Cancel();
            }
        }

        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), StartTime = "2.5" };
        Assert.ThrowsAny<OperationCanceledException>(() => ShotDetection.Detect(video, Source(), options, cancel.Token));
        Assert.Equal(10, pulled);
    }

    [Fact]
    public async Task DisposesTheSourceWhenCancelledOrFailing()
    {
        var video = Video();
        bool disposed = false;
        async IAsyncEnumerable<YuvFrame> Source()
        {
            try
            {
                foreach (var frame in Frames(video).Select((f, i) => f with { Pts = i == 5 ? 1000 : i * 4 }))
                {
                    await Task.Yield();
                    yield return frame;
                }
            }
            finally
            {
                disposed = true;
            }
        }

        var timed = video with { TimeBase = new(1, 100) };
        var e = await Assert.ThrowsAsync<ShotDetectionException>(() => ShotDetection.DetectAsync(timed, Source(), new DetectionOptions { Yuv420Converter = new SwscaleYuv420() }));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.True(disposed);
    }

    [Fact]
    public async Task RunsOnTheCallersAwaitsWithoutAnotherThread()
    {
        // A source that never awaits: the whole run is done before DetectAsync returns, on this thread.
        var video = Video();
        int thread = Environment.CurrentManagedThreadId;
        var seen = new HashSet<int>();
        async IAsyncEnumerable<YuvFrame> Source()
        {
            foreach (var frame in Frames(video))
            {
                seen.Add(Environment.CurrentManagedThreadId);
                yield return frame;
            }

            await Task.CompletedTask;
        }

        var task = ShotDetection.DetectAsync(video, Source(), new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal([thread], seen);
        Assert.Equal(75, (await task).FrameCount);
    }

    [Fact]
    public void NullsAreRefusedAtOnce()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = ShotDetection.DetectAsync(null!, Yielding([])); });
        Assert.Throws<ArgumentNullException>(() => { _ = ShotDetection.DetectAsync(Video(), null!); });
    }

    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
