using ShotDetector;

/// <summary>
/// Two h264 encodes joined into one .ts, the second's timestamps starting over (as InProcessTests' joined encodes): the
/// second half's cuts come out of order, so a run that told shots as cuts arrived would have told shots the sorted
/// result doesn't have.
/// </summary>
public sealed class JoinedEncodes : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-reset-").FullName;

    public JoinedEncodes()
    {
        string three = Path.Combine(_dir, "three.mp4"), a = Path.Combine(_dir, "a.ts"), b = Path.Combine(_dir, "b.ts");
        VideoReader.Run("ffmpeg",
        [
            "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2",
            "-f", "lavfi", "-i", "smptebars=s=320x240:r=25:d=2",
            "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=2",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p",
            "-c:v", "libx264", three,
        ],
        default);

        // The halves as separate encodes, each starting at the same timestamp; the second's shots cut at other times.
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", three, "-t", "4", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", a], default);
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "1", "-i", three, "-vf", "scale=240:180", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", b], default);
        Video = Path.Combine(_dir, "joined.ts");
        File.WriteAllBytes(Video, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
    }

    public string Video { get; }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

/// <summary>
/// DetectionOptions.OnShot and DetectStreamAsync where timestamps start over: a file shows that in its packets before the
/// first frame, so its shots are told at the end, exactly the result's; MPEG-PS decoded in-process (timestamps arriving
/// with the frames) too. A Stream can't know it ahead, with either decoder. A clean file still tells each shot
/// as its cut is confirmed. Needs SHOTDETECTOR_FFMPEG_LIBS (FFmpeg 8.1, its executables beside the libraries).
/// </summary>
public class TimestampResetTests(JoinedEncodes joined, MpegPsVariants ps, Clips clips) : IClassFixture<JoinedEncodes>, IClassFixture<MpegPsVariants>, IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    sealed class Told : IProgress<Shot>
    {
        public List<Shot> Shots { get; } = [];

        public void Report(Shot value) => Shots.Add(value);
    }

    static DetectionOptions Options(string run) => new DetectionOptions { Detector = DetectorKind.Content, FfmpegDirectory = Libs } with
    {
        Decoder = run.Contains("in-process") ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess,
        Mode = run.Contains("chunks") ? DetectionMode.Parallel : run.Contains("auto") ? DetectionMode.Auto : DetectionMode.Sequential,
        Chunks = run.Contains("chunks") ? 2 : null,
        Compatibility = run.Contains("0.7.1") ? PySceneDetectVersion.V0_7_1 : PySceneDetectVersion.V0_7_2,
    };

    public static TheoryData<string> FileRuns() =>
        ["executable", "executable auto", "executable 0.7.1", "in-process", "in-process chunks", "in-process 0.7.1"];

    [Theory]
    [MemberData(nameof(FileRuns))]
    public void AFileTellsTheResultsShots(string run)
    {
        if (Libs is null)
            return;
        var told = new Told();
        var result = ShotDetection.Detect(joined.Video, Options(run) with { OnShot = told });

        // The second half's cut at 75 sorts between the first half's at 50 and 100: told as they came, [50, 100) would
        // have been told, and 50-75 and 75-100 never.
        Assert.Equal([50L, 75L, 100L], result.Cuts.Select(c => c.FrameNum));
        Assert.Equal(result.Shots, told.Shots);
    }

    [Theory]
    [MemberData(nameof(FileRuns))]
    public async Task DetectStreamAsyncOnAFileYieldsTheResultsShots(string run)
    {
        if (Libs is null)
            return;
        var options = Options(run);
        var yielded = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(joined.Video, options))
            yielded.Add(shot);
        Assert.Equal(ShotDetection.Detect(joined.Video, options).Shots, yielded);
    }

    public static TheoryData<string> Decoders() => ["executable", "in-process"];

    // Streamed, the timestamps start over as in the file, as OpenCV gives them on a pipe too (scenedetect 0.7.2 reading
    // pipe:0: 50, 75, 100). Without -copyts the executable's ffmpeg made them continue (50, 100, 125, 175).
    [Theory]
    [MemberData(nameof(Decoders))]
    public void AStreamGivesTheFilesCuts(string run)
    {
        if (Libs is null)
            return;
        var options = Options(run);
        DetectionResult result;
        using (var file = File.OpenRead(joined.Video))
            result = ShotDetection.Detect(file, options);
        Assert.Equal(run == "in-process", result.Video.DecodedInProcess);
        Assert.Equal([50L, 75L, 100L], result.Cuts.Select(c => c.FrameNum));
        Assert.Equal([50L, 75L, 100L], ShotDetection.Detect(joined.Video, options with { Streaming = true }).Cuts.Select(c => c.FrameNum));
    }

    // Nothing shows the reset ahead on a Stream, so both decoders tell the same shots before it, and none again after.
    [Fact]
    public async Task AStreamTellsTheSameShotsWithEitherDecoder()
    {
        if (Libs is null)
            return;
        var told = new Dictionary<string, List<Shot>>();
        var yielded = new Dictionary<string, List<Shot>>();
        foreach (string run in Decoders())
        {
            var listener = new Told();
            using (var file = File.OpenRead(joined.Video))
                ShotDetection.Detect(file, Options(run) with { OnShot = listener });
            told[run] = listener.Shots;

            yielded[run] = [];
            using var stream = File.OpenRead(joined.Video);
            await foreach (var shot in ShotDetection.DetectStreamAsync(stream, Options(run)))
                yielded[run].Add(shot);
        }

        Assert.NotEmpty(told["in-process"]);
        Assert.Equal(told["in-process"], told["executable"]);
        Assert.Equal(yielded["in-process"], yielded["executable"]);
    }

    // The input's start on ffmpeg's Duration line (under "Input #0"), which streamed frames' times are taken from.
    [Theory]
    [InlineData("  Duration: N/A, start: 1.480000, bitrate: N/A", 1_480_000L)]
    [InlineData("  Duration: 00:00:09.00, start: 0.000000, bitrate: 150 kb/s", 0L)]
    [InlineData("  Duration: 00:00:01.00, start: -0.040000, bitrate: 150 kb/s", -40_000L)]
    [InlineData("  Duration: 00:00:01.00, start: 12345.678901, bitrate: 150 kb/s", 12_345_678_901L)]
    [InlineData("  Duration: N/A, bitrate: N/A", 0L)]
    [InlineData("  Duration: N/A, start: N/A, bitrate: N/A", 0L)]
    [InlineData("    Stream #0:0[0x100]: Video: h264", null)]
    [InlineData("Input #0, mpegts, from 'pipe:':", null)]
    public void TheInputsStartFromFfmpegsLog(string line, long? micros) => Assert.Equal(micros, VideoReader.InputStart(line));

    // av_rescale_q from µs, AV_ROUND_NEAR_INF: halves away from 0.
    [Fact]
    public void MicrosInATimeBaseRoundAsFfmpeg()
    {
        Assert.Equal(133_200L, VideoReader.MicrosIn(1_480_000, 1, 90_000));
        Assert.Equal(-133_200L, VideoReader.MicrosIn(-1_480_000, 1, 90_000));
        Assert.Equal(1L, VideoReader.MicrosIn(500, 1, 1000)); // half: away from 0
        Assert.Equal(-1L, VideoReader.MicrosIn(-500, 1, 1000));
        Assert.Equal(0L, VideoReader.MicrosIn(499, 1, 1000));
        Assert.Equal(1_480_000L, VideoReader.MicrosIn(1_480_000, 1, 1_000_000));
        Assert.Equal(37L, VideoReader.MicrosIn(1_480_000, 1, 25));
    }

    // The packets show it before the first frame; MPEG-PS whose timestamps arrive with the frames may start over; a clean
    // file doesn't, nor a stream (nothing is known ahead).
    [Fact]
    public void WhatIsKnownBeforeTheFirstFrame()
    {
        if (Libs is null)
            return;
        Assert.True(new VideoReader(joined.Video, Options("executable")).TimestampsStartOver);
        Assert.True(new VideoReader(joined.Video, Options("in-process")).TimestampsStartOver);
        Assert.False(new VideoReader(clips.ThreeShots, Options("executable")).TimestampsStartOver);
        Assert.False(new VideoReader(clips.ThreeShots, Options("in-process")).TimestampsStartOver);
        using (var file = File.OpenRead(joined.Video))
            Assert.False(new VideoReader(file, Options("in-process")).TimestampsStartOver);

        var arriving = new VideoReader(ps.Paths["joined"], Options("in-process"));
        Assert.True(arriving.TimestampsArrive);
        Assert.True(arriving.TimestampsStartOver);

        // From the decoding pass (the executable), what the frames' timestamps show: the joined copy's go back.
        Assert.True(new VideoReader(ps.Paths["joined"], Options("executable")).TimestampsStartOver);
        Assert.False(new VideoReader(ps.Paths["clean"], Options("executable")).TimestampsStartOver);
    }

    // MPEG-PS decoded in-process tells every shot at the end: after the last frame.
    [Theory]
    [InlineData("joined")]
    [InlineData("clean")]
    public void MpegPsWithArrivingTimestampsTellsTheShotsAtTheEnd(string variant)
    {
        if (Libs is null)
            return;
        int frames = 0, framesWhenFirstTold = -1;
        var told = new Told();
        var options = Options("in-process") with
        {
            OnShot = new SynchronousProgress<Shot>(shot =>
            {
                if (framesWhenFirstTold < 0)
                    framesWhenFirstTold = frames;
                told.Report(shot);
            }),
            Progress = new SynchronousProgress<DetectionProgress>(p => frames = p.FramesProcessed),
            ProgressStep = 0.01,
        };
        var result = ShotDetection.Detect(ps.Paths[variant], options);
        Assert.True(result.Video.DecodedInProcess);
        Assert.True(result.Shots.Count > 1);
        Assert.Equal(result.Shots, told.Shots);
        Assert.Equal(result.FrameCount, framesWhenFirstTold);
    }

    // A clean file still tells each shot once its cut is confirmed, while frames are still being read.
    [Theory]
    [InlineData("executable")]
    [InlineData("in-process")]
    public void ACleanFileTellsEachShotBeforeItsLastFrame(string run)
    {
        if (Libs is null)
            return;
        int frames = 0;
        var toldAt = new List<int>();
        var told = new Told();
        var options = Options(run) with
        {
            OnShot = new SynchronousProgress<Shot>(shot =>
            {
                toldAt.Add(frames);
                told.Report(shot);
            }),
            Progress = new SynchronousProgress<DetectionProgress>(p => frames = p.FramesProcessed),
            ProgressStep = 0.01,
        };
        var result = ShotDetection.Detect(clips.ThreeShots, options);
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);
        Assert.All(toldAt[..^1], at => Assert.True(at < result.FrameCount, $"told after {at} of {result.FrameCount} frames"));
    }

    // Past any decoder's reordering (16 frames): B-frames' pts below a few packets' earlier don't count; a jump back does.
    [Fact]
    public void StartsOverLooksPastTheReordering()
    {
        Assert.False(VideoReader.StartsOver([]));
        Assert.False(VideoReader.StartsOver([0, 8, 4, 2, 1, 3, 6, 5, 7, 16, 12, 10, 9, 11, 14, 13, 15, 24, 20, 18, 17, 19, 22, 21, 23]));
        long[] reordered = [.. Enumerable.Range(0, 40).Select(i => (long)(i % 17 == 0 ? i + 16 : i))];
        Assert.False(VideoReader.StartsOver(reordered));
        Assert.True(VideoReader.StartsOver([.. Enumerable.Range(0, 30).Select(i => (long)i), .. Enumerable.Range(0, 30).Select(i => (long)i)]));
        Assert.True(VideoReader.StartsOver([.. Enumerable.Range(0, 30).Select(i => (long)i), .. Enumerable.Range(10, 30).Select(i => (long)i)]));
    }

    /// <summary>Reports on the thread that reports it (System.Progress would post it elsewhere).</summary>
    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
