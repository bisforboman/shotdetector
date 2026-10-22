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

    // The chunks' readers take the outer reader's probe (stream entries, packet pts, the Xvid IDCT): the file is probed once.
    [Fact]
    public void ChunksTakeTheOuterReadersProbe()
    {
        if (Libs is null)
            return;
        int probes = 0;
        var options = Options(DetectorKind.Content) with { OnProbe = () => Interlocked.Increment(ref probes) };
        var parallel = ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Parallel, Chunks = 3 });
        Assert.Equal(3, parallel.Video.Chunks);
        Assert.Equal(1, probes);
        AssertSame(ShotDetection.Detect(clips.ThreeShots, Options(DetectorKind.Content)), parallel);
    }

    // A reader from another's probe is the reader that probes for itself: sizes, rate, tags, every frame's position and
    // the frames, through the executable (ffprobe) and in-process. The clips are MPEG-4 Part 2 (the Xvid IDCT probe).
    [Theory]
    [InlineData(VideoDecoder.FfmpegProcess)]
    [InlineData(VideoDecoder.InProcess)]
    public void AReaderFromAnotherReadersProbeIsTheSame(VideoDecoder decoder)
    {
        if (decoder == VideoDecoder.InProcess && Libs is null)
            return;
        var options = Options(DetectorKind.Content) with { Decoder = decoder };
        var own = new VideoReader(clips.ThreeShots, options);
        Assert.NotNull(own.Probed);
        int probes = 0;
        var shared = new VideoReader(clips.ThreeShots, options with { OnProbe = () => probes++ }, own.Probed, default);
        Assert.Equal(0, probes);
        Assert.Same(own.Probed, shared.Probed);
        Assert.Equal((own.Width, own.Height, own.SourceWidth, own.SourceHeight, own.Fps, own.Codec, own.Container, own.FieldOrder),
            (shared.Width, shared.Height, shared.SourceWidth, shared.SourceHeight, shared.Fps, shared.Codec, shared.Container, shared.FieldOrder));
        Assert.Equal((own.ExpectedFrames, own.OpenCvFrameCount, own.Duration, own.PtsFromFrames, own.PixelFormat, own.Rotation),
            (shared.ExpectedFrames, shared.OpenCvFrameCount, shared.Duration, shared.PtsFromFrames, shared.PixelFormat, shared.Rotation));
        Assert.Equal(Enumerable.Range(0, own.ExpectedFrames).Select(k => own.Position(k)), Enumerable.Range(0, shared.ExpectedFrames).Select(k => shared.Position(k)));
        Assert.Equal(own.Frames(40, 20).Select(f => f.ToArray()).ToList(), shared.Frames(40, 20).Select(f => f.ToArray()).ToList());
    }

    // Dropped frames (positions that aren't the probe's), MPEG-PS (no cheap seek), a time range and frame skip: read
    // sequentially, with the same result. Two chunks: the dropped frames (60, 61) then shift the second chunk's frames by
    // two, while the boundary (75) is in a still shot, where the overlap scores agree; only the positions tell. Dropped
    // frames fall back after chunking (ChunkFallback says why); the others never chunk; either way a warning says why.
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
        if (name == "dropped")
        {
            Assert.Matches(@"^chunk \d: ", parallel.Video.ChunkFallback);
            Assert.Equal("read sequentially, from the start, as scoring in chunks fell back: " + parallel.Video.ChunkFallback, Assert.Single(parallel.Warnings));
        }
        else
        {
            Assert.Null(parallel.Video.ChunkFallback);
            string why = name switch
            {
                "mpegps" => "packets without timestamps (MPEG-PS)",
                "start" => "a time range",
                "skip" => "frame skip",
                _ => "decoded by the ffmpeg executable",
            };
            Assert.Equal("read sequentially, not in chunks: " + why, Assert.Single(parallel.Warnings));
        }
    }

    // Two chunks meeting where two copies of a transport stream join, the second's first frame under half a frame after
    // the first's last (RangeTests.JoinedTransportStream): the second chunk's seek must not take frame 50 for 49.
    [Fact]
    public void ChunksMeetingAtATransportStreamJoinDontFallBack()
    {
        if (Libs is null)
            return;
        string ts = RangeTests.JoinedTransportStream(clips.ThreeShots);
        try
        {
            var options = Options(DetectorKind.Content);
            var parallel = ShotDetection.Detect(ts, options with { Mode = DetectionMode.Parallel, Chunks = 2 });
            Assert.Null(parallel.Video.ChunkFallback);
            Assert.Equal(2, parallel.Video.Chunks);
            AssertSame(ShotDetection.Detect(ts, options), parallel);
        }
        finally
        {
            File.Delete(ts);
        }
    }

    // Auto reads sequentially without a word (its own choice); chunks that run warn about nothing.
    [Fact]
    public void WarnsOnlyWhenParallelWasAskedFor()
    {
        if (Libs is null)
            return;
        var options = Options(DetectorKind.Content) with { CollectStats = false };
        Assert.Empty(ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Auto }).Warnings);
        Assert.Empty(ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Parallel, Chunks = 3 }).Warnings);
        var parallel = ShotDetection.Detect(clips.ThreeShots, options with { Mode = DetectionMode.Parallel });
        Assert.Equal(1, parallel.Video.Chunks);
        Assert.Null(parallel.Video.ChunkFallback);
        Assert.StartsWith("read sequentially, not in chunks: ", Assert.Single(parallel.Warnings));
    }

    // Why the automatic count is 1, for the same arguments as AutoChunks; null when it's more.
    [Theory]
    [InlineData(2, 100_000, 1920 * 1080, true, null, "fewer than 4 CPUs (2)")]
    [InlineData(3, 100_000, 1920 * 1080, false, null, "fewer than 4 CPUs (3)")]
    [InlineData(4, 100_000, 1920 * 1080, false, null, null)]
    [InlineData(16, 100_000, 4096 * 4096, false, null, "frames larger than 3840 x 2160")]
    [InlineData(16, 100_000, 1920 * 1080, false, 1, "one decoder thread")]
    [InlineData(16, 2999, 1920 * 1080, false, null, "shorter than two minutes")]
    [InlineData(4, 100_000, 1920 * 1080, true, null, null)]
    [InlineData(16, 100_000, 4096 * 4096, true, null, null)]
    public void SaysWhyOneChunk(int cpus, int frames, long pixels, bool forced, int? threads, string? expected) =>
        Assert.Equal(expected, ShotDetection.WhyOneChunk(cpus, frames, new Fps(25, 1), pixels, forced, threads));

    // A chunk's decoder threads: the total shared out, the remainder to the first chunks, at least 1 each.
    [Theory]
    [InlineData(4, 2, new[] { 2, 2 })]
    [InlineData(5, 2, new[] { 3, 2 })]
    [InlineData(3, 2, new[] { 2, 1 })]
    [InlineData(16, 3, new[] { 6, 5, 5 })]
    [InlineData(2, 4, new[] { 1, 1, 1, 1 })]
    public void DecoderThreadsSharedOut(int threads, int chunks, int[] expected) =>
        Assert.Equal(expected, Enumerable.Range(0, chunks).Select(i => ShotDetection.ChunkThreads(threads, chunks, i)));

    // The decoder threads a chunked run shares out unless the caller set some: a sequential run's (the CPUs, at most 16),
    // or twice the CPUs, at most 16, for a frame-threaded decoder (h264, HEVC) with frames up to 1920 x 1080: each chunk
    // then at most the CPUs' count.
    [Theory]
    [InlineData(4, false, 1920 * 1080, 4)]
    [InlineData(4, true, 1920 * 1080, 8)]
    [InlineData(4, true, 1920 * 1080 + 1, 4)]
    [InlineData(4, true, 3840 * 2160, 4)]
    [InlineData(6, true, 1280 * 720, 12)]
    [InlineData(8, true, 1920 * 1080, 16)]
    [InlineData(16, true, 1920 * 1080, 16)]
    [InlineData(32, false, 1920 * 1080, 16)]
    public void ChunkedRunsGiveFrameThreadedDecodersTwiceTheCpus(int cpus, bool frameThreads, long pixels, int expected) =>
        Assert.Equal(expected, ShotDetection.ChunkedDecodeThreads(cpus, frameThreads, pixels));

    // A chunk's frame buffers: a sequential run's 8 shared out, at least 2 each (one decoding, one scored).
    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(16, 2)]
    public void FrameBuffersSharedOut(int chunks, int expected)
    {
        Assert.Equal(8, new DetectionOptions().FrameBuffers);
        Assert.Equal(expected, ShotDetection.ChunkBuffers(chunks));
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
        var progress = new SynchronousProgress<DetectionProgress>(p =>
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

    // Auto: half the CPUs (none below 4), at most 4, for frames up to 3840 x 2160; none shorter than a minute; no
    // more than the decoder threads set. Parallel (forced): from 4 CPUs on, at any size.
    [Theory]
    [InlineData(1, 100_000, 1)]
    [InlineData(3, 100_000, 1)]
    [InlineData(4, 100_000, 2)]
    [InlineData(5, 100_000, 2)]
    [InlineData(7, 100_000, 3)]
    [InlineData(8, 100_000, 4)]
    [InlineData(64, 100_000, 4)]
    [InlineData(16, 3000, 2)]  // 2 minutes at 25 fps
    [InlineData(16, 2999, 1)]
    [InlineData(16, 1000, 1)]
    public void AutomaticChunks(int cpus, int frames, int expected) =>
        Assert.Equal(expected, ShotDetection.AutoChunks(cpus, frames, new Fps(25, 1), 1920 * 1080));

    [Theory]
    [InlineData(16, 3840 * 2160, false, null, 4)]
    [InlineData(16, 3840 * 2160 + 1, false, null, 1)] // larger than 4K UHD: sequential
    [InlineData(16, 4096 * 4096, true, null, 4)]      // forced: any size
    [InlineData(3, 1920 * 1080, true, null, 1)]
    [InlineData(3, 1920 * 1080, false, null, 1)]
    [InlineData(4, 1920 * 1080, true, null, 2)]       // from 4 CPUs on, forced or not
    [InlineData(4, 1920 * 1080, false, null, 2)]
    [InlineData(4, 3840 * 2160 + 1, false, null, 1)]
    [InlineData(4, 3840 * 2160 + 1, true, null, 2)]
    [InlineData(6, 1920 * 1080, true, null, 3)]
    [InlineData(16, 1920 * 1080, false, 1, 1)]        // --threads 1: one decoder
    [InlineData(16, 1920 * 1080, false, 2, 2)]
    [InlineData(16, 1920 * 1080, true, 3, 3)]
    [InlineData(16, 1920 * 1080, false, 0, 4)]        // 0: FFmpeg's choice, no limit
    public void AutomaticChunksBySizeModeAndThreads(int cpus, long pixels, bool forced, int? threads, int expected) =>
        Assert.Equal(expected, ShotDetection.AutoChunks(cpus, 100_000, new Fps(25, 1), pixels, forced, threads));

    // DetectionOptions.Warnings: DetectStreamAsync has no result, so it hears why Parallel read sequentially before the
    // first shot (in-process or through the executable, which can't chunk either).
    [Fact]
    public async Task StreamingCallersGetTheWarningsBeforeTheFirstShot()
    {
        var warnings = new List<string>();
        var options = new DetectionOptions
        {
            FfmpegDirectory = Libs,
            Decoder = Libs is null ? VideoDecoder.FfmpegProcess : VideoDecoder.InProcess,
            Mode = DetectionMode.Parallel,
            Warnings = new SynchronousProgress<string>(w =>
            {
                lock (warnings)
                    warnings.Add(w);
            }),
        };
        int shots = 0;
        await foreach (var _ in ShotDetection.DetectStreamAsync(clips.ThreeShots, options))
        {
            if (shots++ == 0)
            {
                lock (warnings)
                    Assert.StartsWith("read sequentially, not in chunks: ", Assert.Single(warnings));
            }
        }

        Assert.Equal(3, shots);
        Assert.Single(warnings);
    }

    // Detect tells DetectionOptions.Warnings exactly what DetectionResult.Warnings holds, and nothing when it's empty.
    [Theory]
    [InlineData(DetectionMode.Parallel)]
    [InlineData(DetectionMode.Sequential)]
    public void DetectTellsTheWarningsItReturns(DetectionMode mode)
    {
        var warnings = new List<string>();
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions
        {
            FfmpegDirectory = Libs,
            Decoder = Libs is null ? VideoDecoder.FfmpegProcess : VideoDecoder.InProcess,
            Mode = mode,
            Warnings = new SynchronousProgress<string>(warnings.Add),
        });
        Assert.Equal(result.Warnings, warnings);
        Assert.Equal(mode == DetectionMode.Parallel ? 1 : 0, warnings.Count);
    }

    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
