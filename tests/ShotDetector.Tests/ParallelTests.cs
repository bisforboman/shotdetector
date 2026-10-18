using ShotDetector;

/// <summary>
/// DetectionOptions.Mode: chunks scored at once give exactly the sequential result, and what a chunk can't reproduce
/// falls back to a sequential read. Decodes in-process, so like InProcessTests it needs SHOTDETECTOR_FFMPEG_LIBS (but
/// not the mode's own tests).
/// </summary>
public class ParallelTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    // Sequential, the reference; each test asks for the chunks it checks.
    static DetectionOptions Options(DetectorKind kind, PySceneDetectVersion version = PySceneDetectVersion.V0_7_2) => new()
    {
        Detector = kind,
        Compatibility = version,
        CollectStats = true,
        FfmpegDirectory = Libs,
        Decoder = VideoDecoder.InProcess,
        Mode = DetectionMode.Sequential,
    };

    static void AssertSame(DetectionResult sequential, DetectionResult parallel)
    {
        Assert.Equal(sequential.Stats?.Csv(sequential.Video.Position), parallel.Stats?.Csv(parallel.Video.Position));
        Assert.Equal(Shots.Csv(sequential.Shots), Shots.Csv(parallel.Shots));
        Assert.Equal(sequential.Cuts, parallel.Cuts);
        Assert.Equal(sequential.FrameCount, parallel.FrameCount);
    }

    public static TheoryData<DetectorKind, PySceneDetectVersion, int> Cases => new()
    {
        { DetectorKind.Content, PySceneDetectVersion.V0_7_2, 3 },
        { DetectorKind.Adaptive, PySceneDetectVersion.V0_7_2, 3 },
        { DetectorKind.Adaptive, PySceneDetectVersion.V0_7_1, 4 },
        { DetectorKind.Histogram, PySceneDetectVersion.V0_7_2, 3 },
        { DetectorKind.Histogram, PySceneDetectVersion.V0_7_1, 7 },
        { DetectorKind.Hash, PySceneDetectVersion.V0_7_2, 3 },
        { DetectorKind.Threshold, PySceneDetectVersion.V0_7_2, 2 },
    };

    // Chunk boundaries at 37, 75 and 112 (and others) fall around the cuts at 50 and 100 and inside adaptive's window.
    [Theory]
    [MemberData(nameof(Cases))]
    public void ChunksGiveTheSequentialResult(DetectorKind kind, PySceneDetectVersion version, int chunks)
    {
        if (Libs is null)
            return;
        var options = Options(kind, version);
        var sequential = ShotDetection.Detect(clips.ThreeShots, options);
        var parallel = ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Parallel, Chunks = chunks });
        Assert.Equal(1, sequential.Video.Chunks);
        Assert.Equal(chunks, parallel.Video.Chunks);
        AssertSame(sequential, parallel);
    }

    [Fact]
    public void SeveralDetectorsAndNoStats()
    {
        if (Libs is null)
            return;
        var options = Options(DetectorKind.Content) with
        {
            CollectStats = false,
            Detectors = [new(DetectorKind.Content), new(DetectorKind.Threshold) { Threshold = 30 }, new(DetectorKind.Hash)],
        };
        var parallel = ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Parallel, Chunks = 3 });
        Assert.Equal(3, parallel.Video.Chunks);
        AssertSame(ShotDetection.Detect(clips.ThreeShots, options), parallel);
    }

    // Dropped frames (positions that aren't the probe's), MPEG-PS (no cheap seek), a time range and frame skip: read
    // sequentially, with the same result. Two chunks: the dropped frames (60, 61) then shift the second chunk's frames by
    // two, while the boundary (75) is in a still shot, where the overlap scores agree; only the positions tell.
    [Theory]
    [InlineData("dropped")]
    [InlineData("mpegps")]
    [InlineData("start")]
    [InlineData("skip")]
    [InlineData("executable")]
    public void FallsBackToSequential(string name)
    {
        if (Libs is null)
            return;
        var options = Options(DetectorKind.Content) with { CollectStats = name != "skip" };
        options = name switch
        {
            "start" => options with { StartTime = "1.5" },
            "skip" => options with { FrameSkip = 2 },
            "executable" => options with { Decoder = VideoDecoder.FfmpegProcess },
            _ => options,
        };
        string path = name switch { "dropped" => clips.ThreeShotsDropped, "mpegps" => clips.ThreeShotsMpegPs, _ => clips.ThreeShots };
        var parallel = ShotDetection.Detect(path, options with { Mode = DetectionMode.Parallel, Chunks = 2 });
        Assert.Equal(1, parallel.Video.Chunks);
        AssertSame(ShotDetection.Detect(path, options), parallel);
    }

    // A larger second half (from frame 100) with timestamps that carry on: scenedetect scales its frames to the first
    // size, which the last of four chunks (from 111) would take from them instead. The overlap scores and the first
    // sizes both tell.
    [Fact]
    public void SizeChangeFallsBack()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-parallel-size-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            string[] h264 = ["-c:v", "libx264", "-bf", "0", "-bsf:v", "h264_mp4toannexb", "-muxdelay", "0", "-muxpreload", "0"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "100", .. h264, a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "4", "-i", clips.ThreeShots, "-vf", "scale=480:360", .. h264, "-output_ts_offset", "4", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            var options = Options(DetectorKind.Content);
            var parallel = ShotDetection.Detect(joined, options with { Mode = DetectionMode.Parallel, Chunks = 4 });
            Assert.Equal(1, parallel.Video.Chunks);
            AssertSame(ShotDetection.Detect(joined, options), parallel);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Two encodes joined, the second at another size with its timestamps starting over (InProcessTests'
    // JoinedEncodesWithTimestampsStartingOver): a chunk starting in the second half would see another first size.
    [Fact]
    public void JoinedEncodesFallBack()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-parallel-joined-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "4", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "1", "-i", clips.ThreeShots, "-vf", "scale=240:180", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            var options = Options(DetectorKind.Content);
            var parallel = ShotDetection.Detect(joined, options with { Mode = DetectionMode.Parallel, Chunks = 3 });
            Assert.Equal(1, parallel.Video.Chunks);
            AssertSame(ShotDetection.Detect(joined, options), parallel);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Progress counts the frames of all chunks together and ends at the total.
    [Fact]
    public void ReportsProgressOverAllChunks()
    {
        if (Libs is null)
            return;
        var reports = new List<DetectionProgress>();
        var progress = new SynchronousProgress(p =>
        {
            lock (reports)
                reports.Add(p);
        });
        var result = ShotDetection.Detect(clips.ThreeShots, Options(DetectorKind.Content) with { Mode = DetectionMode.Parallel, Chunks = 3, Progress = progress, ProgressStep = 0.25 });
        Assert.Equal(3, result.Video.Chunks);
        Assert.Equal([0, 38, 75, 113, 150], reports.Select(r => r.FramesProcessed));
    }

    [Fact]
    public void Cancels()
    {
        if (Libs is null)
            return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ShotDetection.Detect(clips.Long, Options(DetectorKind.Content) with { CollectStats = false, Mode = DetectionMode.Parallel, Chunks = 4 }, cancel.Token));
    }

    // Auto (the default) chunks for Detect, not when the shots are streamed (DetectStreamAsync delivers them as they come).
    [Theory]
    [InlineData(DetectionMode.Auto, false, DetectionMode.Parallel)]
    [InlineData(DetectionMode.Auto, true, DetectionMode.Sequential)]
    [InlineData(DetectionMode.Sequential, false, DetectionMode.Sequential)]
    [InlineData(DetectionMode.Sequential, true, DetectionMode.Sequential)]
    [InlineData(DetectionMode.Parallel, false, DetectionMode.Parallel)]
    [InlineData(DetectionMode.Parallel, true, DetectionMode.Parallel)]
    public void ResolvesTheMode(DetectionMode mode, bool streaming, DetectionMode expected) =>
        Assert.Equal(expected, new DetectionOptions { Mode = mode }.ResolvedMode(streaming));

    [Fact]
    public void AutoIsTheDefault() => Assert.Equal(DetectionMode.Auto, new DetectionOptions().Mode);

    // The obsolete opt-in switch sets the mode: true Parallel, false Sequential (what it meant before Auto).
    [Fact]
    public void ObsoleteParallelSetsTheMode()
    {
#pragma warning disable CS0618
        Assert.Equal(DetectionMode.Parallel, new DetectionOptions { Parallel = true }.Mode);
        Assert.Equal(DetectionMode.Sequential, new DetectionOptions { Parallel = false }.Mode);
        Assert.True(new DetectionOptions { Mode = DetectionMode.Parallel }.Parallel);
        Assert.False(new DetectionOptions().Parallel);
#pragma warning restore CS0618
    }

    // Detect with the default mode chunks (a forced count, the clip being short); Sequential doesn't; the obsolete
    // switch still does.
    [Fact]
    public void DetectChunksByDefault()
    {
        if (Libs is null)
            return;
        var options = Options(DetectorKind.Content) with { Chunks = 3 };
        var auto = ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Auto });
        var sequential = ShotDetection.Detect(clips.ThreeShots, options);
#pragma warning disable CS0618
        var obsolete = ShotDetection.Detect(clips.ThreeShots, options with { Parallel = true });
#pragma warning restore CS0618
        Assert.Equal(3, auto.Video.Chunks);
        Assert.Equal(1, sequential.Video.Chunks);
        Assert.Equal(3, obsolete.Video.Chunks);
        AssertSame(sequential, auto);
    }

    // Half the CPUs, at most 4, from 4 CPUs on; none shorter than a minute.
    [Theory]
    [InlineData(1, 100_000, 1)]
    [InlineData(2, 100_000, 1)]
    [InlineData(3, 100_000, 1)]
    [InlineData(4, 100_000, 2)]
    [InlineData(6, 100_000, 3)]
    [InlineData(8, 100_000, 4)]
    [InlineData(64, 100_000, 4)]
    [InlineData(16, 3000, 2)]  // 2 minutes at 25 fps
    [InlineData(16, 2999, 1)]
    [InlineData(16, 1000, 1)]
    public void AutomaticChunks(int cpus, int frames, int expected) =>
        Assert.Equal(expected, ShotDetection.AutoChunks(cpus, frames, new Fps(25, 1)));

    sealed class SynchronousProgress(Action<DetectionProgress> report) : IProgress<DetectionProgress>
    {
        public void Report(DetectionProgress value) => report(value);
    }
}
