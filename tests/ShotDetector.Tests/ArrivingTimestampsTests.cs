using ShotDetector;

/// <summary>
/// MPEG-PS clips (packets without a pts) with three shots: as made, cut short at the start (the first frames lost, so the
/// first frame's time isn't the stream's start), with a hole in the middle, and joined to its own second half (timestamps
/// that start over), each made by slicing the file's bytes.
/// </summary>
public sealed class MpegPsVariants : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-psvariants-").FullName;

    public MpegPsVariants()
    {
        string clean = Path.Combine(_dir, "clean.mpg");
        VideoReader.Run("ffmpeg",
        [
            "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=4",
            "-f", "lavfi", "-i", "smptebars=s=320x240:r=25:d=4",
            "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=4",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p",
            "-c:v", "mpeg2video", "-q:v", "31", "-f", "mpeg", clean,
        ],
        default);
        string packets = VideoReader.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "default=nw=1", clean], default);
        Assert.Contains("pts=N/A", packets);

        byte[] bytes = File.ReadAllBytes(clean);
        int third = bytes.Length / 3;
        Paths = new()
        {
            ["clean"] = clean,
            ["cut-start"] = Write("cut-start.mpg", bytes[third..]),
            ["hole"] = Write("hole.mpg", [.. bytes[..third], .. bytes[(third + (bytes.Length / 10))..]]),
            ["joined"] = Write("joined.mpg", [.. bytes, .. bytes[(bytes.Length / 2)..]]),
        };
    }

    public Dictionary<string, string> Paths { get; }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>
/// MPEG-PS decoded in-process takes each frame's timestamp from the frame as it arrives, not from a decoding pass first:
/// the same list as the pass, and the same results.
/// </summary>
public class ArrivingTimestampsTests(MpegPsVariants clips) : IClassFixture<MpegPsVariants>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Variants() => ["clean", "cut-start", "hole", "joined"];

    static DetectionOptions Options(bool passFirst, int pass = 0, Action? onPass = null) => new()
    {
        Decoder = VideoDecoder.InProcess,
        FfmpegDirectory = Libs,

        // One decoding thread: FFmpeg's error concealment of a damaged MPEG-2 stream (the holed clip) differs between runs
        // with slice threads, whatever the timestamps.
        DecodeThreads = 1,
        CollectStats = true,
        Detectors = [new(DetectorKind.Adaptive), new(DetectorKind.Content), new(DetectorKind.Threshold), new(DetectorKind.Histogram), new(DetectorKind.Hash)],
        Compatibility = pass % 2 == 0 ? PySceneDetectVersion.V0_7_2 : PySceneDetectVersion.V0_7_1,
        FrameTimestampsFirst = passFirst,
        OnFrameTimestamps = onPass,
    };

    // The timestamps that came with the frames are the decoding pass's, and no pass ran.
    [Theory]
    [MemberData(nameof(Variants))]
    public void TheTimestampsAreThePass(string variant)
    {
        if (Libs is null)
            return;
        string path = clips.Paths[variant];
        bool passed = false;
        var reader = new VideoReader(path, Options(passFirst: false, onPass: () => passed = true));
        Assert.True(reader.TimestampsArrive);
        Assert.Equal(reader.OpenCvFrameCount, reader.ExpectedFrames);
        int frames = reader.Frames().Count();
        Assert.False(passed, "The decoding pass ran.");
        Assert.False(reader.TimestampsArrive);
        var pass = InProcessProbe.FrameTimestamps(path, Libs, VideoReader.InputOptions(path));
        Assert.Equal(pass, reader.KnownTimestamps);
        Assert.Equal(frames, reader.ExpectedFrames);
    }

    // Shots, cuts, stats and every frame's position as with the pass first, in both compatibility modes (frames converted on
    // the decoding thread, then on a thread of their own).
    [Theory]
    [MemberData(nameof(Variants))]
    public void ResultsAreThePass(string variant)
    {
        if (Libs is null)
            return;
        string path = clips.Paths[variant];
        for (int mode = 0; mode < 2; mode++)
        {
            var expected = ShotDetection.Detect(path, Options(passFirst: true, mode));
            bool passed = false;
            var actual = ShotDetection.Detect(path, Options(passFirst: false, mode, () => passed = true) with { ConvertThreads = mode });
            Assert.False(passed, "The decoding pass ran.");
            FrameRangeTests.AssertSame(expected, actual);
            if (variant == "cut-start")
                Assert.True(expected.Video.Position(0).Seconds > 0, "The first frame's time is the stream's start: the clip doesn't test the first shot's start.");
        }
    }

    // Deinterlaced, the frames carry yadif's times: the pass instead, as before.
    [Fact]
    public void DeinterlacingTakesThePass()
    {
        if (Libs is null || !InProcess.CanDeinterlace(Libs))
            return;
        string path = clips.Paths["cut-start"];
        var expected = ShotDetection.Detect(path, Options(passFirst: true) with { Deinterlace = DeinterlaceMode.On });
        var actual = ShotDetection.Detect(path, Options(passFirst: false) with { Deinterlace = DeinterlaceMode.On });
        FrameRangeTests.AssertSame(expected, actual);
    }

    // A read that stops early leaves the later frames' timestamps to the pass: a later frame's position, or a read from
    // there, as with the pass first.
    [Fact]
    public void APartialReadLeavesTheRestToThePass()
    {
        if (Libs is null)
            return;
        string path = clips.Paths["joined"];
        var reader = new VideoReader(path, Options(passFirst: false));
        Assert.Equal(40, reader.Frames(0, 40).Count());
        Assert.True(reader.TimestampsArrive);
        var pass = new VideoReader(path, Options(passFirst: true));
        Assert.Equal(40, pass.Frames(0, 40).Count());
        for (int k = 0; k < 40; k++)
            Assert.Equal(pass.Position(k), reader.Position(k));
        Assert.Equal(pass.Position(250), reader.Position(250));
        Assert.False(reader.TimestampsArrive);
        Assert.Equal(pass.KnownTimestamps, reader.KnownTimestamps);
    }

    // A start time seeks by the timestamps: the pass first, as before (and cancelling stops it: FrameTimestampsTests).
    [Fact]
    public void AStartTimeTakesThePass()
    {
        if (Libs is null)
            return;
        string path = clips.Paths["hole"];
        bool passed = false;
        var actual = ShotDetection.Detect(path, Options(passFirst: false, onPass: () => passed = true) with { StartTime = "3.5" });
        Assert.True(passed);
        FrameRangeTests.AssertSame(ShotDetection.Detect(path, Options(passFirst: true) with { StartTime = "3.5" }), actual);
    }

    // A frame whose time no timestamp so far settles (the search reached the last one without deciding) waits for the
    // rest: undecided while the list is incomplete, the next packet once it is complete.
    [Fact]
    public void APacketTheTimestampsSoFarDontSettleIsUndecided()
    {
        long[] pts = [0, 3600, 3600];
        double timeBase = 1 / 90000.0;
        Assert.Null(VideoReader.PacketOf(pts, timeBase, 25, 0.1, previous: 0, complete: false));
        Assert.Equal(1, VideoReader.PacketOf(pts, timeBase, 25, 0.1, previous: 0, complete: true));
        Assert.Equal(1, VideoReader.PacketOf(pts, timeBase, 25, 0.04, previous: 0, complete: false));
        Assert.Equal(1, VideoReader.PacketOf([0, 3600, 9000], timeBase, 25, 0.07, previous: 0, complete: false));
    }
}
