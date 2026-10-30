using System.Diagnostics;
using System.Runtime.InteropServices;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Detection on frames the caller decoded (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// a file's frames, decoded here with FrameReader, give exactly the file's result with the same converter (FastYuv's).
/// Needs FFmpeg 8.1's shared libraries for the decoding (SHOTDETECTOR_FFMPEG_LIBS); without them the comparisons do
/// nothing. Its checks of the input need nothing.
/// </summary>
public class FrameInputTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Cases => ["adaptive", "content", "threshold", "histogram", "hash", "start", "end", "crop", "crop-scale", "scale", "skip", "drop", "v071", "parallel"];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SameAsTheFile(string name)
    {
        if (Libs is null)
            return;

        // Without a resize the file's frames are FFmpeg's conversion, which FastYuv reproduces on x86 (elsewhere FFmpeg's
        // assembly rounds differently).
        if (name is "scale" or "crop-scale" && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var options = new DetectionOptions { CollectStats = name != "skip", FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "content" => options with { Detector = DetectorKind.Content },
            "threshold" => options with { Detector = DetectorKind.Threshold, Threshold = 40 },
            "histogram" => options with { Detector = DetectorKind.Histogram },
            "hash" => options with { Detector = DetectorKind.Hash },
            "start" => options with { Detector = DetectorKind.Content, StartTime = "1.5" },
            "end" => options with { Detector = DetectorKind.Content, StartTime = "41", EndTime = "4.5" },
            "crop" => options with { Detector = DetectorKind.Content, Crop = (10, 20, 300, 200) },
            "crop-scale" => options with { Detector = DetectorKind.Content, Crop = (10, 20, 300, 200), Downscale = 1 },
            "scale" => options with { Downscale = 1 },
            "skip" => options with { Detector = DetectorKind.Content, FrameSkip = 2 },
            "drop" => options with { Detectors = [new(DetectorKind.Content), new(DetectorKind.Histogram)], DropShortScenes = true, MinSceneLength = "60" },
            "v071" => options with { Compatibility = PySceneDetectVersion.V0_7_1 },
            "parallel" => options with { Mode = DetectionMode.Parallel },
            _ => options,
        };
        await AssertSame(clips.ThreeShots, options);
    }

    // H.264 with B-frames at 29.97 fps in mp4 (an edit list, decode order unlike presentation order, timestamps whose
    // microseconds round): the showcase's input. The same stream in MPEG-TS starts at 1.4 s (StartPts). Made with the
    // ffmpeg executable's libx264; skipped without it.
    [Theory]
    [InlineData(DetectorKind.Adaptive, "mp4")]
    [InlineData(DetectorKind.Content, "mp4")]
    [InlineData(DetectorKind.Histogram, "mp4")]
    [InlineData(DetectorKind.Content, "ts")]
    public async Task SameAsTheFileForH264WithBFrames(DetectorKind detector, string container)
    {
        if (Libs is null || H264Clip() is not { } mp4)
            return;
        string clip = container == "ts" ? Path.ChangeExtension(mp4, ".ts") : mp4;
        if (container == "ts")
            Assert.NotEqual(0, FrameReader.MediaProbe.Probe(clip).Video!.StartPts);
        await AssertSame(clip, new DetectionOptions { Detector = detector, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
    }

    // A phone's file: the pixels stored sideways or upside down, with a display matrix (ffmpeg -display_rotation: a
    // file of that rotation, counterclockwise, which OpenCV turns back clockwise). The frames are given as decoded,
    // YuvVideo.Rotation says the file's angle, and they are turned after converting as the file's: through the resize,
    // without one (Downscale 1: the whole frame converted, then turned), cropped (the crop is the upright frame's).
    [Theory]
    [InlineData(90, "content")]
    [InlineData(180, "content")]
    [InlineData(-90, "content")]
    [InlineData(180, "hash")]
    [InlineData(90, "adaptive")]
    [InlineData(-90, "crop")]
    [InlineData(90, "scale")]
    [InlineData(180, "crop-scale")]
    public async Task SameAsTheFileTurned(int displayRotation, string name)
    {
        if (Libs is null || (name.EndsWith("scale", StringComparison.Ordinal) && RuntimeInformation.ProcessArchitecture != Architecture.X64))
            return;
        string clip = Rotated(clips.ThreeShots, displayRotation);
        var options = new DetectionOptions { CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "content" => options with { Detector = DetectorKind.Content },
            "hash" => options with { Detector = DetectorKind.Hash },
            "crop" => options with { Detector = DetectorKind.Content, Crop = (10, 21, 220, 290) },
            "scale" => options with { Detector = DetectorKind.Content, Downscale = 1 },
            "crop-scale" => options with { Detector = DetectorKind.Content, Crop = (11, 20, 300, 201), Downscale = 1 },
            _ => options,
        };
        var file = ShotDetection.Detect(clip, options);
        Assert.Equal(0, (file.Video.Rotation - displayRotation) % 360); // 180 is -180: av_display_rotation_get's angle
        await AssertSame(clip, options);
        var ours = ShotDetection.Detect(Decoded(clip).Video, Decoded(clip).Frames, options);
        Assert.Equal((file.Video.Rotation, file.Video.Width, file.Video.Height), (ours.Video.Rotation, ours.Video.Width, ours.Video.Height));
    }

    // A variable frame rate (issue #609's clip: frames last twice as long from frame 50 on, so the average rate is no
    // frame's): with every frame's timestamp given up front, the frames are at their own times and StartTime finds its
    // frame as the file path does (by the estimate where it is within a frame, else by searching the timestamps).
    [Theory]
    [InlineData("content")]
    [InlineData("adaptive")]
    [InlineData("histogram")]
    [InlineData("hash")]
    [InlineData("start")]
    [InlineData("start-early")]
    [InlineData("end")]
    [InlineData("v071")]
    public async Task SameAsTheFileAtAVariableRate(string name)
    {
        if (Libs is null)
            return;
        var options = new DetectionOptions { CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "content" => options with { Detector = DetectorKind.Content },
            "histogram" => options with { Detector = DetectorKind.Histogram },
            "hash" => options with { Detector = DetectorKind.Hash },
            "start" => options with { Detector = DetectorKind.Content, StartTime = "4.33" },
            "start-early" => options with { Detector = DetectorKind.Content, StartTime = "1.2" },
            "end" => options with { Detector = DetectorKind.Content, StartTime = "1", EndTime = "6.5" },
            "v071" => options with { Compatibility = PySceneDetectVersion.V0_7_1 },
            _ => options,
        };
        string clip = VariableRateClip(clips.ThreeShots);
        if (name == "start")
        {
            // Not round(4.33 x the average rate): the search finds the frame, the one at 4.32 s (within half a frame).
            var reader = new VideoReader(clip, options);
            Assert.NotEqual(reader.FrameAt(4.33), reader.SeekFrame(4.33));
            Assert.Equal(4.32, reader.Position(reader.SeekFrame(4.33)).Seconds, 6);
        }

        await AssertSame(clip, options, timestamps: true);

        // Without the timestamps, the frames off the average rate's grid are refused.
        var (video, frames) = Decoded(clip);
        Assert.Null(video.FrameTimestamps);
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, frames, options with { StartTime = null, EndTime = null })).Reason);
    }

    [Fact]
    public void FrameTimestampsAreChecked()
    {
        var video = Video() with { TimeBase = new(1, 100), FrameTimestamps = [.. Enumerable.Range(0, 10).Select(i => i * 4L)] };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        IEnumerable<YuvFrame> At(params long[] pts) => Frames(video, pts.Length).Select((f, i) => f with { Pts = pts[i] });

        // A time base to count them in, and rising.
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video with { TimeBase = null }, At(0, 4), options));
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video with { FrameTimestamps = [0, 4, 4, 8] }, At(0, 4), options));

        // Frame 0 at entry 0; each next frame at a later entry.
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, At(4, 8), options)).Reason);
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, At(0, 5), options)).Reason);
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, At(0, 8, 4), options)).Reason);
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, At(0, 4, 40), options)).Reason);

        // Off the constant-rate grid is fine with them (no grid check), as a frame the decoder dropped is: the frames keep
        // their own times and the numbers go on, as a file's run takes them.
        var uneven = video with { FrameTimestamps = [0, 4, 12, 13, 30, 31, 32, 33, 60, 61] };
        Assert.Equal(10, ShotDetection.Detect(uneven, At([.. uneven.FrameTimestamps]), options).FrameCount);
        var dropped = ShotDetection.Detect(video, At(0, 4, 8, 16, 20), options);
        Assert.Equal(5, dropped.FrameCount);
        Assert.Equal(FrameTime.Pts(160_000, 1_000_000, new Fps(25, 1)), dropped.Video.Position(3));

        // Not at a start time after a dropped frame: the file path would number the frames from the start's packet.
        Assert.Equal(ShotDetectionError.InvalidInput,
            Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, At(0, 4, 12, 16, 20, 24), options with { StartTime = "0.16" })).Reason);
        Assert.Equal(4, ShotDetection.Detect(video, At(0, 4, 8, 12, 16, 20, 24), options with { StartTime = "0.16" }).Shots[0].Start.FrameNum);
    }

    [Fact]
    public void RangesAreAtTheirFrameTimestamps()
    {
        // A range's frames at their entries; Merge refuses a frame that isn't (a drop, or the range given under another
        // first frame), which the overlap frames alone wouldn't show here.
        var video = Video() with { TimeBase = new(1, 100), FrameTimestamps = [.. Enumerable.Range(0, 40).Select(i => i < 20 ? i * 4L : 80 + ((i - 20) * 8L))] };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true };
        var pts = video.FrameTimestamps;
        IEnumerable<YuvFrame> Range(int from, int to) => Frames(video, 40).Select((f, i) => f with { Pts = pts[i] }).Skip(from).Take(to - from);
        var expected = ShotDetection.Detect(video, Range(0, 40), options);
        var first = ShotDetection.ScoreRange(video, Range(0, 21), 0, options);
        var second = ShotDetection.ScoreRange(video, Range(19, 40), 20, options);
        var merged = ShotDetection.Merge(video, [first, second], options);
        Assert.Equal(expected.Stats!.Csv(expected.Video.Position), merged.Stats!.Csv(merged.Video.Position));
        Assert.Equal(Shots.Csv(expected.Shots, cuts: expected.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));

        // A range's first frame given under another first frame is refused as it is scored.
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.ScoreRange(video, Range(20, 40), 20, options)).Reason);

        // A frame dropped inside a range: Merge refuses it.
        var dropping = ShotDetection.ScoreRange(video, Range(19, 40).Where((_, i) => i != 5), 20, options);
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [first, dropping], options)).Reason);

        // Other timestamps are another video.
        Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video with { FrameTimestamps = [.. pts.Select(p => p + 1)] }, [first, second], options));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(360)]
    [InlineData(-450)]
    public void RotationIsAQuarterTurn(int rotation)
    {
        var video = Video() with { Rotation = rotation };
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, Frames(video, 3), new DetectionOptions { Yuv420Converter = new SwscaleYuv420() }));
    }

    [Fact]
    public void TurnedFramesAreUpright()
    {
        // 64 x 48 as decoded, 48 x 64 upright: the planes are the decoded size's, the result the upright one's, and 270
        // is -90.
        var video = Video() with { Rotation = 270 };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true, Downscale = 1 };
        var result = ShotDetection.Detect(video, Frames(video, 30), options);
        Assert.Equal((270, 48, 64, 48, 64), (result.Video.Rotation, result.Video.Width, result.Video.Height, result.Video.ProcessedWidth, result.Video.ProcessedHeight));
        var minus = ShotDetection.Detect(video with { Rotation = -90 }, Frames(video, 30), options);
        Assert.Equal(result.Stats!.Csv(result.Video.Position), minus.Stats!.Csv(minus.Video.Position));
    }

    [Fact]
    public async Task NeedsAConverter()
    {
        var e = Assert.Throws<ArgumentException>(() => ShotDetection.Detect(Video(), Frames(Video(), 3)));
        Assert.Contains("Yuv420Converter", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(() => ShotDetection.DetectAsync(Video(), FrameSourceAsyncTests.Yielding(Frames(Video(), 3))));
        Assert.Contains("Yuv420Converter", e.Message);
    }

    [Theory]
    [InlineData("deinterlace")]
    [InlineData("ffmpeg-resize")]
    [InlineData("tone")]
    [InlineData("format")]
    [InlineData("odd")]
    [InlineData("colour")]
    [InlineData("plane")]
    public async Task RejectsWhatItCantReproduce(string name)
    {
        var video = name switch
        {
            "format" => Video() with { PixelFormat = "yuv422p" },
            "odd" => Video() with { Height = 63 },
            "colour" => Video() with { ColorSpace = "ycgco" },
            _ => Video(),
        };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.Auto },
            "ffmpeg-resize" => options with { FfmpegResize = true },
            "tone" => options with { TrimReferenceTone = true },
            _ => options,
        };
        var frames = Frames(video, 3);
        if (name == "plane")
            frames = frames.Select(f => f with { U = f.U[..^4] });
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, frames, options));
        await Assert.ThrowsAsync<ArgumentException>(() => ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(frames), options));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    [InlineData(30, true)]
    public async Task FramesMustComeAtTheFrameRate(int late, bool fails)
    {
        // Frame 5 is `late` hundredths of a second after where the rate puts it: up to half a frame (0.02 s) is fine.
        var video = Video() with { TimeBase = new(1, 100) };
        var frames = Frames(video, 10).Select((f, i) => f with { Pts = i * 4 + (i == 5 ? late : 0) });
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        if (fails)
        {
            Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, frames, options)).Reason);
            Assert.Equal(ShotDetectionError.InvalidInput,
                (await Assert.ThrowsAsync<ShotDetectionException>(() => ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(frames), options))).Reason);
        }
        else
        {
            Assert.Equal(10, ShotDetection.Detect(video, frames, options).FrameCount);
            Assert.Equal(10, (await ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(frames), options)).FrameCount);
        }
    }

    [Fact]
    public void FramesAreAtTheirPtsFromTheStreamsStart()
    {
        // As a file's: (pts - start) in microseconds, frame 0 (at 0) by its number.
        var video = Video() with { TimeBase = new(1, 100), StartPts = 100 };
        var frames = Frames(video, 10).Select((f, i) => f with { Pts = 100 + i * 4 });
        var result = ShotDetection.Detect(video, frames, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.Equal(FrameTime.Pts(280_000, 1_000_000, new Fps(25, 1)), result.Video.Position(7));
        Assert.Equal(FrameTime.Pts(0, 25, new Fps(25, 1)), result.Video.Position(0));
    }

    [Fact]
    public void WithoutTimestampsFramesAreAtTheirNumberOverTheRate()
    {
        var video = Video();
        var result = ShotDetection.Detect(video, Frames(video, 50), new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true });
        Assert.Equal(50, result.FrameCount);
        Assert.Equal(FrameTime.Pts(7, 25, new Fps(25, 1)), result.Video.Position(7));
        Assert.False(result.Video.DecodedInProcess);
        Assert.Null(result.VideoPath);
        Assert.Equal((64, 48), (result.Video.ProcessedWidth, result.Video.ProcessedHeight));
    }

    // Nothing on this path loads FFmpeg: in a process of its own (this one has FFmpeg.AutoGen loaded by other tests), with
    // no ffmpeg on PATH and no FFmpeg libraries named, FramesWithoutFfmpeg detects on generated frames and checks that
    // neither FFmpeg.AutoGen nor any of FFmpeg's libraries got loaded (the system's would be found if anything tried).
    [Fact]
    public async Task LoadsNothingOfFfmpeg()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string check = Path.Combine(bin.Parent!.Parent!.Parent!.Parent!.FullName, "FramesWithoutFfmpeg", "bin", bin.Parent.Name, bin.Name, "FramesWithoutFfmpeg.dll");
        string dotnet = Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet"
            ? host
            : Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(check);
        psi.Environment.Remove("SHOTDETECTOR_FFMPEG_LIBS");
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("DYLD_LIBRARY_PATH");
        psi.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"FramesWithoutFfmpeg failed ({p.ExitCode}): {err}");
        Assert.StartsWith("Timecode List:", await output);
    }

    // The frames of a file, as a decoder gives them, and the file's result for them.
    static async Task AssertSame(string path, DetectionOptions options, bool timestamps = false)
    {
        var file = ShotDetection.Detect(path, options);
        Assert.True(file.Video.DecodedInProcess);
        var (video, frames) = Decoded(path, timestamps);
        var ours = ShotDetection.Detect(video, frames, options);

        // The same frames from an asynchronous source (DetectAsync) give the same result.
        var awaited = await ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(Decoded(path, timestamps).Frames, every: 5), options);
        Assert.Equal(ours.FrameCount, awaited.FrameCount);
        Assert.Equal(ours.Shots, awaited.Shots);
        Assert.Equal(Shots.Csv(ours.Shots, cuts: ours.Cuts), Shots.Csv(awaited.Shots, cuts: awaited.Cuts));
        Assert.Equal(ours.Stats?.Csv(ours.Video.Position), awaited.Stats?.Csv(awaited.Video.Position));
        Assert.Equal(ours.Warnings, awaited.Warnings);
        Assert.Equal((ours.Video.ProcessedWidth, ours.Video.ProcessedHeight, ours.Video.FrameRate, ours.Video.OpenCvFrameCount, ours.Video.Duration),
            (awaited.Video.ProcessedWidth, awaited.Video.ProcessedHeight, awaited.Video.FrameRate, awaited.Video.OpenCvFrameCount, awaited.Video.Duration));
        Assert.Equal(file.FrameCount, ours.FrameCount);
        Assert.Equal(file.MinSceneLengthFrames, ours.MinSceneLengthFrames);
        Assert.Equal(file.Cuts, ours.Cuts);
        Assert.Equal(file.Shots, ours.Shots);
        Assert.Equal(Shots.Csv(file.Shots, cuts: file.Cuts), Shots.Csv(ours.Shots, cuts: ours.Cuts));
        Assert.Equal(file.Stats?.Csv(file.Video.Position), ours.Stats?.Csv(ours.Video.Position));
        Assert.Equal((file.Video.ProcessedWidth, file.Video.ProcessedHeight, file.Video.FrameRate, file.Video.OpenCvFrameCount, file.Video.Duration),
            (ours.Video.ProcessedWidth, ours.Video.ProcessedHeight, ours.Video.FrameRate, ours.Video.OpenCvFrameCount, ours.Video.Duration));

        // Parallel asked for: both read sequentially, for their own reason.
        static string Why(string warning) => warning.Split(": ")[0];
        Assert.Equal(file.Warnings.Select(Why), ours.Warnings.Select(Why));
        Assert.False(ours.Video.DecodedInProcess);
    }

    // A file split at its keyframes, each range decoded on a decoder of its own from its keyframe (a host's Web Workers:
    // the keyframe is the frame before the range's first, which its scores compare with), merges into the sequential
    // frame-source result: H.264 with B-frames, and the mpeg4 clip.
    [Theory]
    [InlineData("h264", DetectorKind.Adaptive)]
    [InlineData("h264", DetectorKind.Histogram)]
    [InlineData("mpeg4", DetectorKind.Content)]
    [InlineData("mpeg4", DetectorKind.Hash)]
    [InlineData("mpeg4-turned", DetectorKind.Content)]
    [InlineData("mpeg4-vfr", DetectorKind.Adaptive)]
    [InlineData("mpeg4-vfr", DetectorKind.Hash)]
    public void RangesFromKeyframesMergeIntoTheSequentialResult(string clip, DetectorKind detector)
    {
        string? file = clip switch
        {
            "h264" => H264Clip(),
            "mpeg4-turned" => Rotated(clips.ThreeShots, 90),
            "mpeg4-vfr" => VariableRateClip(clips.ThreeShots),
            _ => clips.ThreeShots,
        };
        if (Libs is null || file is not { } path)
            return;
        var options = new DetectionOptions { Detector = detector, CollectStats = true, Yuv420Converter = new SwscaleYuv420() };
        var (video, frames) = Decoded(path, timestamps: clip == "mpeg4-vfr");
        var pts = frames.Select(f => f.Pts).ToList();
        var expected = ShotDetection.Detect(video, Decoded(path).Frames, options);

        // Two or three ranges, from the keyframes nearest a third and two thirds of the way.
        List<long> keyframes;
        using (var keys = new FrameReader.FrameDecoder(path, DecoderOptions(path) with { KeyframesOnly = true }))
        {
            keyframes = [];
            while (keys.Next())
                keyframes.Add(keys.Pts);
        }

        var inner = keyframes.Select(p => pts.IndexOf(p)).Where(k => k > 0 && k < pts.Count - 2).ToList();
        Assert.NotEmpty(inner);
        int[] starts = [0, .. new[] { pts.Count / 3, 2 * pts.Count / 3 }.Select(t => inner.MinBy(k => Math.Abs(k - t)) + 1).Distinct()];
        var ranges = new List<RangeScores>();
        for (int i = 0; i < starts.Length; i++)
        {
            int first = starts[i], end = i + 1 < starts.Length ? starts[i + 1] : pts.Count;
            var given = first == 0 ? Decoded(path).Frames : Decoded(path, from: pts[first - 1]);
            var scores = ShotDetection.ScoreRange(video, given.Take(end + 1 - Math.Max(0, first - 1)), first, options);
            ranges.Add(RangeScores.FromBytes(scores.ToBytes()));
        }

        var merged = ShotDetection.Merge(video, ranges, options);
        if (video.FrameTimestamps is not null)
        {
            var fromFile = ShotDetection.Detect(path, options with { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess });
            Assert.Equal(Shots.Csv(fromFile.Shots, cuts: fromFile.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
            Assert.Equal(fromFile.Stats!.Csv(fromFile.Video.Position), merged.Stats!.Csv(merged.Video.Position));
        }

        Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video with { Rotation = video.Rotation == 0 ? 90 : 0 }, ranges, options));
        Assert.Equal(expected.FrameCount, merged.FrameCount);
        Assert.Equal(expected.Cuts, merged.Cuts);
        Assert.Equal(expected.Shots, merged.Shots);
        Assert.Equal(Shots.Csv(expected.Shots, cuts: expected.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
        Assert.Equal(expected.Stats!.Csv(expected.Video.Position), merged.Stats!.Csv(merged.Video.Position));
        Assert.True(expected.Shots.Count > 1);
    }

    /// <summary>The decoder options the file path decodes with: no autorotation, its IDCT.</summary>
    static FrameReader.FrameDecoderOptions DecoderOptions(string path)
    {
        var reader = new VideoReader(path, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess });
        return new FrameReader.FrameDecoderOptions
        {
            Autorotate = false,
            CodecOptions = VideoReader.IdctOptions(reader.Codec, reader.UsesXvidIdct) is [var key, var value] ? new Dictionary<string, string> { [key[1..]] = value } : null,
        };
    }

    /// <summary>The file's frames from the keyframe at <paramref name="from"/> on, decoded after a seek to it.</summary>
    static IEnumerable<YuvFrame> Decoded(string path, long from)
    {
        using var decoder = new FrameReader.FrameDecoder(path, DecoderOptions(path));
        decoder.Seek(from);
        while (decoder.Next())
        {
            if (decoder.Pts < from)
                continue;
            var y = decoder.GetPlane(0, out int ys).ToArray();
            var u = decoder.GetPlane(1, out int us).ToArray();
            var v = decoder.GetPlane(2, out int vs).ToArray();
            yield return new YuvFrame(y, ys, u, us, v, vs) { Pts = decoder.Pts };
        }
    }

    /// <summary>
    /// The file's properties as FFmpeg gives them (with <paramref name="timestamps"/>, every frame's: its packets' pts,
    /// sorted), and its frames decoded as the file path decodes them.
    /// </summary>
    static (YuvVideo Video, IEnumerable<YuvFrame> Frames) Decoded(string path, bool timestamps = false)
    {
        var probed = FrameReader.MediaProbe.Probe(path, new FrameReader.ProbeOptions { PacketTimestamps = timestamps });
        var info = probed.Video!;
        var decoderOptions = DecoderOptions(path);
        string colorSpace, colorRange;
        using (var first = new FrameReader.FrameDecoder(path, decoderOptions))
        {
            Assert.True(first.Next());
            colorSpace = first.ColorSpace ?? info.ColorSpace ?? "";
            colorRange = first.ColorRange ?? info.ColorRange ?? "";
        }

        var video = new YuvVideo
        {
            Width = info.Width,
            Height = info.Height,
            FrameRate = info.AverageFrameRate,
            PixelFormat = info.PixelFormat!,
            ColorSpace = colorSpace,
            FullRange = colorRange == "pc",
            TimeBase = info.TimeBase,
            StartPts = info.StartPts ?? 0,
            FrameCount = info.FrameCount,
            Duration = info.DurationPts is { } d ? TimeSpan.FromTicks((long)Math.Round(d * 1e7 * info.TimeBase.Num / info.TimeBase.Den / 10) * 10) : null,
            Codec = info.Codec,
            Rotation = (int)Math.Round(info.Rotation ?? 0) % 360,
            FrameTimestamps = timestamps ? [.. probed.PacketTimestamps!.Select(p => p!.Value).Order()] : null,
        };
        return (video, Frames());

        IEnumerable<YuvFrame> Frames()
        {
            using var decoder = new FrameReader.FrameDecoder(path, decoderOptions);
            while (decoder.Next())
            {
                var y = decoder.GetPlane(0, out int ys).ToArray();
                var u = decoder.GetPlane(1, out int us).ToArray();
                var v = decoder.GetPlane(2, out int vs).ToArray();
                yield return new YuvFrame(y, ys, u, us, v, vs) { Pts = decoder.Pts };
            }
        }
    }

    /// <summary>ffprobe's seconds become the nearest tick: TimeSpan.FromSeconds(4.56) is 4.5599999.</summary>
    [Fact]
    public void DurationIsTheNearestTick()
    {
        Assert.Equal(TimeSpan.FromTicks(45_600_000), VideoReader.Seconds("4.560000"));
        Assert.Null(VideoReader.Seconds("N/A"));
    }

    static YuvVideo Video() => new() { Width = 64, Height = 48, FrameRate = new(25, 1) };

    /// <summary>Noise frames, two shots of it (a cut at frame 25), with padded rows.</summary>
    static IEnumerable<YuvFrame> Frames(YuvVideo video, int count)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        for (int i = 0; i < count; i++)
        {
            var random = new Random(i < 25 ? 1 : 2);
            byte[] Plane(int width, int height)
            {
                var plane = new byte[(width + 3) * height];
                random.NextBytes(plane);
                return plane;
            }

            yield return new YuvFrame(Plane(w, h), w + 3, Plane(cw, ch), cw + 3, Plane(cw, ch), cw + 3);
        }
    }

    /// <summary>A copy of <paramref name="path"/> with a display matrix of ffmpeg's -display_rotation <paramref name="degrees"/>.</summary>
    static string Rotated(string path, int degrees)
    {
        string rotated = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-rotated{degrees}.mp4");
        lock (H264Lock)
        {
            if (!File.Exists(rotated))
            {
                string partial = rotated + ".part.mp4";
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-display_rotation", degrees.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-c", "copy", partial], default);
                File.Move(partial, rotated, true);
            }
        }

        return rotated;
    }

    /// <summary>
    /// A copy of <paramref name="path"/> at a variable frame rate (issue #609's): its frames at 25 fps to frame 50, then
    /// each lasting twice as long; MPEG-4 Part 2 again, in mp4.
    /// </summary>
    static string VariableRateClip(string path)
    {
        string vfr = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-vfr.mp4");
        lock (H264Lock)
        {
            if (!File.Exists(vfr))
            {
                string partial = vfr + ".part.mp4";
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", path, "-vf", "setpts='if(lt(N,50),N,50+(N-50)*2)/(25*TB)'", "-fps_mode", "passthrough", "-c:v", "mpeg4", "-q:v", "3", partial], default);
                File.Move(partial, vfr, true);
            }
        }

        return vfr;
    }

    static string? _h264;
    static readonly object H264Lock = new();

    static string? H264Clip()
    {
        lock (H264Lock)
        {
            if (_h264 is not null)
                return _h264.Length > 0 ? _h264 : null;
            string path = Path.Combine(Directory.CreateTempSubdirectory("shotdetector-frames-").FullName, "bframes.mp4");
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string arg in new[]
            {
                "-v", "error", "-y", "-filter_complex",
                "testsrc2=d=2:s=320x240:r=30000/1001[a];smptebars=d=2:s=320x240:r=30000/1001[b];mandelbrot=s=320x240:r=30000/1001,trim=duration=2[c];[a][b][c]concat=n=3,format=yuv420p",
                "-c:v", "libx264", "-bf", "3", "-g", "48", "-crf", "23", path,
            })
            {
                psi.ArgumentList.Add(arg);
            }

            try
            {
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                _h264 = p.ExitCode == 0 ? path : "";
                if (p.ExitCode == 0)
                {
                    using var ts = Process.Start("ffmpeg", ["-v", "error", "-y", "-i", path, "-c", "copy", Path.ChangeExtension(path, ".ts")]);
                    ts.WaitForExit();
                    Assert.Equal(0, ts.ExitCode);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _h264 = "";
            }

            return _h264.Length > 0 ? _h264 : null;
        }
    }
}
