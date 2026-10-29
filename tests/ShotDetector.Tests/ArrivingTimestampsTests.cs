using ShotDetector;

/// <summary>
/// MPEG-PS clips (packets without a pts) with three shots: as made, cut short at the start (the first frames lost, so the
/// first frame's time isn't the stream's start), with a hole in the middle, joined to its own second half (timestamps
/// that start over) and with bytes overwritten here and there, each made from the file's bytes; and the same shots in a
/// DVD's VOB and as h264 in a program stream, many of whose frames have neither a pts nor a dts (ffmpeg's command line
/// gives its filters a time it extrapolates for those, so the executable takes the pass's).
/// </summary>
public sealed class MpegPsVariants : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-psvariants-").FullName;

    public MpegPsVariants()
    {
        string Make(string name, params string[] output)
        {
            string path = Path.Combine(_dir, name);
            VideoReader.Run("ffmpeg",
            [
                "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=4",
                "-f", "lavfi", "-i", "smptebars=s=320x240:r=25:d=4",
                "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=4",
                "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p",
                .. output, path,
            ],
            default);
            string packets = VideoReader.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "default=nw=1", path], default);
            Assert.Contains("pts=N/A", packets);
            return path;
        }

        string clean = Make("clean.mpg", "-c:v", "mpeg2video", "-q:v", "31", "-f", "mpeg");
        byte[] bytes = File.ReadAllBytes(clean);
        int third = bytes.Length / 3;
        byte[] damaged = [.. bytes];
        var random = new Random(7);
        for (int i = 0; i < 20; i++)
            random.NextBytes(damaged.AsSpan(random.Next(2048, damaged.Length - 64), random.Next(1, 32)));
        Paths = new()
        {
            ["clean"] = clean,
            ["cut-start"] = Write("cut-start.mpg", bytes[third..]),
            ["hole"] = Write("hole.mpg", [.. bytes[..third], .. bytes[(third + (bytes.Length / 10))..]]),
            ["joined"] = Write("joined.mpg", [.. bytes, .. bytes[(bytes.Length / 2)..]]),
            ["damaged"] = Write("damaged.mpg", damaged),
            ["dvd"] = Make("dvd.vob", "-c:v", "mpeg2video", "-q:v", "31", "-g", "15", "-bf", "2", "-f", "dvd"),
            ["h264"] = Make("h264.mpg", "-c:v", "libx264", "-crf", "30", "-f", "mpeg"),
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
/// MPEG-PS read from the start takes each frame's timestamp as it arrives, not from a decoding pass first: in-process
/// from the frame, through the executable from ffprobe's pass running alongside. The same list as the pass first, and
/// the same results.
/// </summary>
public class ArrivingTimestampsTests(MpegPsVariants clips) : IClassFixture<MpegPsVariants>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static readonly string[] VariantNames = ["clean", "cut-start", "hole", "joined", "damaged", "dvd", "h264"];

    public static TheoryData<string, VideoDecoder> Variants()
    {
        var data = new TheoryData<string, VideoDecoder>();
        foreach (var decoder in (VideoDecoder[])[VideoDecoder.InProcess, VideoDecoder.FfmpegProcess])
        {
            foreach (string variant in VariantNames)
                data.Add(variant, decoder);
        }

        return data;
    }

    public static TheoryData<VideoDecoder> Decoders() => [VideoDecoder.InProcess, VideoDecoder.FfmpegProcess];

    // In-process needs the libraries; the executable is ffmpeg's (on PATH without them).
    static bool Skip(VideoDecoder decoder) => decoder == VideoDecoder.InProcess && Libs is null;

    static DetectionOptions Options(VideoDecoder decoder, bool passFirst, int pass = 0, Action? onPass = null) => new()
    {
        Decoder = decoder,
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

    // The timestamps that came with the frames (or alongside them) are the decoding pass's, and no pass ran first.
    [Theory]
    [MemberData(nameof(Variants))]
    public void TheTimestampsAreThePass(string variant, VideoDecoder decoder)
    {
        if (Skip(decoder))
            return;
        string path = clips.Paths[variant];
        bool passed = false;
        var reader = new VideoReader(path, Options(decoder, passFirst: false, onPass: () => passed = true));
        Assert.True(reader.TimestampsArrive);
        Assert.Equal(reader.OpenCvFrameCount, reader.ExpectedFrames);
        int frames = reader.Frames().Count();
        Assert.False(passed, "The decoding pass ran.");
        Assert.False(reader.TimestampsArrive);
        Assert.Equal(new VideoReader(path, Options(decoder, passFirst: true)).KnownTimestamps, reader.KnownTimestamps);
        Assert.Equal(frames, reader.ExpectedFrames);
        if (decoder == VideoDecoder.FfmpegProcess)
            Assert.True(reader.LastPassAlongside!.HasExited);
    }

    // Shots, cuts, stats and every frame's position as with the pass first, in both compatibility modes (frames converted on
    // the decoding thread, then on a thread of their own).
    [Theory]
    [MemberData(nameof(Variants))]
    public void ResultsAreThePass(string variant, VideoDecoder decoder)
    {
        if (Skip(decoder))
            return;
        string path = clips.Paths[variant];
        for (int mode = 0; mode < 2; mode++)
        {
            var expected = ShotDetection.Detect(path, Options(decoder, passFirst: true, mode));
            bool passed = false;
            var actual = ShotDetection.Detect(path, Options(decoder, passFirst: false, mode, () => passed = true) with { ConvertThreads = mode });
            Assert.False(passed, "The decoding pass ran.");
            FrameRangeTests.AssertSame(expected, actual);
            if (variant == "cut-start")
                Assert.True(expected.Video.Position(0).Seconds > 0, "The first frame's time is the stream's start: the clip doesn't test the first shot's start.");
        }
    }

    // Deinterlaced, the frames carry yadif's times: the pass first instead, as before.
    [Theory]
    [MemberData(nameof(Decoders))]
    public void DeinterlacingTakesThePass(VideoDecoder decoder)
    {
        if (Skip(decoder) || (decoder == VideoDecoder.InProcess && !InProcess.CanDeinterlace(Libs!)))
            return;
        string path = clips.Paths["cut-start"];
        bool passed = false;
        var expected = ShotDetection.Detect(path, Options(decoder, passFirst: true) with { Deinterlace = DeinterlaceMode.On });
        var actual = ShotDetection.Detect(path, Options(decoder, passFirst: false, onPass: () => passed = true) with { Deinterlace = DeinterlaceMode.On });
        Assert.True(passed, "No decoding pass first.");
        FrameRangeTests.AssertSame(expected, actual);
    }

    // A read that stops early leaves the later frames' timestamps to the pass (the executable stops the one alongside): a
    // later frame's position, or a read from there, as with the pass first.
    [Theory]
    [MemberData(nameof(Decoders))]
    public void APartialReadLeavesTheRestToThePass(VideoDecoder decoder)
    {
        if (Skip(decoder))
            return;
        string path = clips.Paths["joined"];
        var reader = new VideoReader(path, Options(decoder, passFirst: false));
        Assert.Equal(40, reader.Frames(0, 40).Count());
        Assert.True(reader.TimestampsArrive);
        if (decoder == VideoDecoder.FfmpegProcess)
            Assert.True(reader.LastPassAlongside!.HasExited, "The pass alongside the read still runs.");
        var pass = new VideoReader(path, Options(decoder, passFirst: true));
        Assert.Equal(40, pass.Frames(0, 40).Count());
        for (int k = 0; k < 40; k++)
            Assert.Equal(pass.Position(k), reader.Position(k));
        Assert.Equal(pass.Position(250), reader.Position(250));
        Assert.False(reader.TimestampsArrive);
        Assert.Equal(pass.KnownTimestamps, reader.KnownTimestamps);
    }

    // A start time seeks by the timestamps: the pass first, as before (and cancelling stops it: FrameTimestampsTests).
    [Theory]
    [MemberData(nameof(Decoders))]
    public void AStartTimeTakesThePass(VideoDecoder decoder)
    {
        if (Skip(decoder))
            return;
        string path = clips.Paths["hole"];
        bool passed = false;
        var actual = ShotDetection.Detect(path, Options(decoder, passFirst: false, onPass: () => passed = true) with { StartTime = "3.5" });
        Assert.True(passed);
        FrameRangeTests.AssertSame(ShotDetection.Detect(path, Options(decoder, passFirst: true) with { StartTime = "3.5" }), actual);
    }

    // ffprobe failing fails the read as the pass first did: its exit code and errors.
    [Fact]
    public void AFailedPassThrows()
    {
        string notVideo = Path.Combine(Path.GetDirectoryName(clips.Paths["clean"])!, "not-a-video.mpg");
        File.WriteAllBytes(notVideo, new byte[4096]);
        string ffprobe = Libs is null ? "ffprobe" : Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        using var pass = new VideoReader.TimestampPass(ffprobe, notVideo, [], default);
        var e = Assert.Throws<ShotDetectionException>(() =>
        {
            while (pass.TakeNext([], default))
            {
            }
        });
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("failed (", e.Message);
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
