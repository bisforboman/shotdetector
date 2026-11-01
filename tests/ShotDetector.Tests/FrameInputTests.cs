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

    // Reference tone (issue #629): bars with 1 kHz tone for 6 s before the programme ("lead": the trim sets the start) or
    // after it ("trail": the end, from YuvVideo.Duration). The host finds the tone in audio it decoded itself (here the
    // ffmpeg executable's f32le, fed to FrameReader's ReferenceToneFinder in chunks of its own size): the file path's
    // segments, and with them in YuvVideo.ReferenceTone the file path's trimmed result.
    [Theory]
    [InlineData("lead", "content")]
    [InlineData("trail", "content")]
    [InlineData("lead", "adaptive")]
    public async Task SameAsTheFileTrimmedToTone(string where, string detector)
    {
        if (Libs is null)
            return;
        string clip = ToneClip(where);
        var options = new DetectionOptions
        {
            Detector = detector == "content" ? DetectorKind.Content : DetectorKind.Adaptive,
            CollectStats = true,
            FfmpegDirectory = Libs,
            Decoder = VideoDecoder.InProcess,
            Yuv420Converter = new SwscaleYuv420(),
            TrimReferenceTone = true,
        };
        var expected = ShotDetection.FindReferenceTone(clip, options);
        Assert.Single(expected);

        var finder = new FrameReader.ReferenceToneFinder(48000, 2, options.ReferenceTone);
        var psi = new ProcessStartInfo(Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-i", clip, "-map", "0:a", "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            var stderr = p.StandardError.ReadToEndAsync();
            var buffer = new byte[4000 * 2 * sizeof(float)];
            long instants = 0;
            int filled;
            while ((filled = await p.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, filled));
                finder.Add(samples, TimeSpan.FromSeconds(instants / 48000.0));
                instants += samples.Length / 2;
            }

            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, await stderr);
        }

        var tone = finder.Finish();
        Assert.Equal(expected, tone);
        await AssertSame(clip, options, tone: tone);
        var trimmed = ShotDetection.Detect(clip, options);
        Assert.Equal(tone, trimmed.ReferenceTone);
        Assert.True(where == "lead" ? trimmed.Shots[0].Start.FrameNum == 150 : trimmed.Shots[^1].End.FrameNum == 100);
    }

    /// <summary>The three-part clip with stereo AAC: 6 s of bars and -18 dBFS tone before (lead) or after (trail) 4 s of programme.</summary>
    string ToneClip(string where)
    {
        string path = Path.Combine(Path.GetDirectoryName(clips.ThreeShots)!, $"tone-{where}.mp4");
        if (File.Exists(path))
            return path;
        const string Bars = "-f lavfi -i smptebars=s=320x240:r=25:d=6 -f lavfi -i sine=f=1000:r=48000:d=6";
        const string Programme = "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i anoisesrc=r=48000:a=0.1:d=2 "
            + "-f lavfi -i mandelbrot=s=320x240:r=25 -f lavfi -i anoisesrc=r=48000:a=0.1:c=pink:d=2";
        string inputs = where == "lead" ? Bars + " " + Programme : Programme + " " + Bars;
        string concat = where == "lead"
            ? "[4]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][2:v][3:a][m][5:a]concat=n=3:v=1:a=1[v][a]"
            : "[2]trim=duration=2,setpts=PTS-STARTPTS[m];[0:v][1:a][m][3:a][4:v][5:a]concat=n=3:v=1:a=1[v][a]";
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (string a in $"-v error -y {inputs} -filter_complex {concat} -map [v] -map [a] -ac 2 -c:v mpeg4 -q:v 2 -c:a aac".Split(' '))
            psi.ArgumentList.Add(a);
        string temporary = path + ".part.mp4";
        psi.ArgumentList.Add(temporary);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        File.Move(temporary, path, overwrite: true);
        return path;
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

    // Interlaced H.264 (libx264 MBAFF, as shotdetector-web decodes it with ManagedCodecs) deinterlaced: the frames as
    // decoded, YuvVideo.FieldOrder as FFmpeg reports the stream's and each frame's flags as FFmpeg sets them (top field
    // first on one clip, bottom on the other: yadif's parity), held as FrameDecoder's row-wise yadif holds them. Auto
    // and On; from a start time (the frame before it is the first's previous); every row (no resize); with frame skip
    // (the skipped frames are still the analysed ones' neighbours); turned after deinterlacing; MPEG-2.
    [Theory]
    [InlineData("tff", "auto")]
    [InlineData("bff", "auto")]
    [InlineData("tff", "on")]
    [InlineData("bff", "adaptive")]
    [InlineData("tff", "hash")]
    [InlineData("bff", "start")]
    [InlineData("tff", "skip")]
    [InlineData("bff", "scale")]
    [InlineData("tff", "rotated")]
    [InlineData("mpeg2", "auto")]
    [InlineData("progressive", "on")]
    public async Task SameAsTheFileDeinterlaced(string clip, string name)
    {
        if (Libs is null || (name == "scale" && RuntimeInformation.ProcessArchitecture != Architecture.X64))
            return;
        string path = clip == "progressive" ? clips.ThreeShots : InterlacedClip(clip);
        if (name == "rotated")
            path = Rotated(path, 90);
        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            CollectStats = true,
            FfmpegDirectory = Libs,
            Decoder = VideoDecoder.InProcess,
            Yuv420Converter = new SwscaleYuv420(),
            Deinterlace = name == "on" ? DeinterlaceMode.On : DeinterlaceMode.Auto,
            Yadif = new Yadif(),
        };
        options = name switch
        {
            "adaptive" => options with { Detector = DetectorKind.Adaptive },
            "hash" => options with { Detector = DetectorKind.Hash },
            "start" => options with { StartTime = "2.5", EndTime = "5" },
            "skip" => options with { FrameSkip = 2, CollectStats = false },
            "scale" => options with { Downscale = 1, Crop = (10, 21, 220, 190) },
            _ => options,
        };
        Assert.True(ShotDetection.Detect(path, options).Video.Deinterlaced);
        await AssertSame(path, options);
    }

    [Fact]
    public void AutoDeinterlacesWhatTheFieldOrderSays()
    {
        // Auto goes by YuvVideo.FieldOrder: progressive or unknown isn't deinterlaced, and needs no Yadif then.
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true, Deinterlace = DeinterlaceMode.Auto };
        foreach (var order in new[] { FrameReader.FieldOrder.Unknown, FrameReader.FieldOrder.Progressive })
        {
            var video = Video() with { FieldOrder = order };
            var result = ShotDetection.Detect(video, Frames(video, 30), options);
            Assert.Equal((order, false), (result.Video.FieldOrder, result.Video.Deinterlaced));
        }

        var interlaced = Video() with { FieldOrder = FrameReader.FieldOrder.BottomFirst };
        var e = Assert.Throws<ArgumentException>(() => ShotDetection.Detect(interlaced, Frames(interlaced, 30), options));
        Assert.Contains("Yadif", e.Message);
        var deinterlaced = ShotDetection.Detect(interlaced, Frames(interlaced, 30), options with { Yadif = new Yadif() });
        Assert.Equal((FrameReader.FieldOrder.BottomFirst, true), (deinterlaced.Video.FieldOrder, deinterlaced.Video.Deinterlaced));
        var off = ShotDetection.Detect(interlaced, Frames(interlaced, 30), options with { Deinterlace = DeinterlaceMode.Off, Yadif = new Yadif() });
        Assert.False(off.Video.Deinterlaced);
        Assert.NotEqual(off.Stats!.Csv(off.Video.Position), deinterlaced.Stats!.Csv(deinterlaced.Video.Position));
    }

    [Fact]
    public void EachFramesFlagsSetItsParity()
    {
        // yadif's auto parity, per frame: top first unless flagged interlaced and not top first. Frames given in one
        // buffer the caller reuses give the same result: the reader copies those yadif reads again.
        var video = Video() with { FieldOrder = FrameReader.FieldOrder.TopFirst };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true, Downscale = 1, Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        string Stats(Func<int, YuvFrame, YuvFrame> flags, bool reused = false)
        {
            var frames = Frames(video, 30).Select((f, i) => flags(i, f));
            if (reused)
                frames = Reused(frames);
            var r = ShotDetection.Detect(video, frames, options);
            return r.Stats!.Csv(r.Video.Position);
        }

        string top = Stats((_, f) => f with { Interlaced = true, TopFieldFirst = true });
        Assert.Equal(top, Stats((_, f) => f));
        Assert.Equal(top, Stats((_, f) => f with { TopFieldFirst = false }));
        string bottom = Stats((_, f) => f with { Interlaced = true });
        Assert.NotEqual(top, bottom);
        Assert.NotEqual(bottom, Stats((i, f) => f with { Interlaced = true, TopFieldFirst = i == 10 }));
        Assert.Equal(bottom, Stats((_, f) => f with { Interlaced = true }, reused: true));

        static IEnumerable<YuvFrame> Reused(IEnumerable<YuvFrame> frames)
        {
            byte[]? y = null, u = null, v = null;
            foreach (var f in frames)
            {
                y ??= new byte[f.Y.Length];
                u ??= new byte[f.U.Length];
                v ??= new byte[f.V.Length];
                f.Y.CopyTo(y);
                f.U.CopyTo(u);
                f.V.CopyTo(v);
                yield return f with { Y = y, U = u, V = v };
            }
        }
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
            "format" => Video() with { PixelFormat = "yuv444p" },
            "odd" => Video() with { Width = 63, Height = 63 },
            "colour" => Video() with { ColorSpace = "ycgco" },
            _ => Video(),
        };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.On },
            "ffmpeg-resize" => options with { FfmpegResize = true },
            "tone" => options with { TrimReferenceTone = true },
            _ => options,
        };
        var frames = Frames(video, 3);
        if (name == "plane")
            frames = frames.Select(f => f with { U = f.U[..^4] });
        var e = Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, frames, options));
        await Assert.ThrowsAsync<ArgumentException>(() => ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(frames), options));

        // Refused for what it is, not by a later check that happens to catch it too.
        if (name == "format")
            Assert.Contains("must be yuv420p, yuvj420p, yuv422p, yuvj422p or yuv420p10le so far, not yuv444p", e.Message);
        if (name == "odd")
            Assert.Contains("8-bit of an odd height, at an even width", e.Message);
        if (name == "tone")
            Assert.Contains("needs YuvVideo.ReferenceTone", e.Message);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(64, 48, true)]
    [InlineData(80, 48, false)]
    [InlineData(64, 40, false)]
    [InlineData(-1, 0, false)]
    public async Task AFrameOfAnotherSizeIsRefused(int width, int height, bool taken)
    {
        // Frame 30 of another size than the video's (its planes large enough for either): refused, not read at the
        // video's size, by Detect, DetectAsync and ScoreRange alike; the video's own size, or 0, is taken.
        var video = Video();
        var big = Frames(video with { Width = 80, Height = 64 }, 40).ToList();
        IEnumerable<YuvFrame> Frames40() => Frames(video, 40).Select((f, i) => i == 30 ? big[i] with { Width = width, Height = height } : f);
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        if (taken)
        {
            Assert.Equal(40, ShotDetection.Detect(video, Frames40(), options).FrameCount);
            return;
        }

        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, Frames40(), options));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("Frame 30 is", e.Message);
        Assert.Equal(ShotDetectionError.InvalidInput,
            (await Assert.ThrowsAsync<ShotDetectionException>(() => ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(Frames40()), options))).Reason);
        Assert.Equal(ShotDetectionError.InvalidInput,
            Assert.Throws<ShotDetectionException>(() => ShotDetection.ScoreRange(video, Frames40().Skip(20), 20, options)).Reason);
    }

    // A stream whose resolution changes midway (two libx264 encodes, 320x240 then 352x288, joined in MPEG-TS): the file
    // path scales the later frames to 320x240; given as decoded, with their own size, they are refused.
    [Fact]
    public void AResolutionChangeIsRefused()
    {
        if (Libs is null || ResolutionChange() is not { } path)
            return;
        var (video, frames) = Decoded(path);
        Assert.Equal((320, 240), (video.Width, video.Height));
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, frames, options));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("352x288, not the video's 320x240", e.Message);
        Assert.Equal(new[] { 50, 100 }, ShotDetection.Detect(path, options with { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess }).Cuts.Select(c => (int)c.FrameNum));
    }

    /// <summary>The three-shot clip's first 75 frames at 320x240 and the rest at 352x288, joined in MPEG-TS; null without libx264.</summary>
    string? ResolutionChange()
    {
        string dir = Path.GetDirectoryName(clips.ThreeShots)!, path = Path.Combine(dir, "resolution-change.ts");
        lock (H264Lock)
        {
            if (File.Exists(path))
                return path;
            string a = Path.Combine(dir, "resolution-a.ts"), b = Path.Combine(dir, "resolution-b.ts"), list = Path.Combine(dir, "resolution.txt");
            try
            {
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "75", "-c:v", "libx264", "-bf", "0", a], default);
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-vf", "trim=start_frame=75,setpts=PTS-STARTPTS,scale=352:288", "-c:v", "libx264", "-bf", "0", b], default);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                return null;
            }

            File.WriteAllText(list, $"file '{a}'\nfile '{b}'\n");
            string partial = path + ".part.ts";
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", partial], default);
            File.Move(partial, path, true);
        }

        return path;
    }

    // The formats FastYuv converts besides 8-bit 4:2:0: 10-bit 4:2:0 (H.264 and HEVC, chroma sited left; a copy sited
    // top-left, whose chroma is interpolated from there), 8-bit 4:2:2 (H.264 yuv422p, MJPEG yuvj422p), through the resize
    // and without it (Downscale 1), turned, and deinterlaced (10-bit and 4:2:2 MBAFF H.264): the file's results byte
    // for byte. Made with the ffmpeg executable's encoders; skipped without them.
    [Theory]
    [InlineData("h264-10", "content")]
    [InlineData("h264-10", "adaptive")]
    [InlineData("h264-10", "scale")]
    [InlineData("h264-10", "turned")]
    [InlineData("h264-10-topleft", "content")]
    [InlineData("hevc-10", "content")]
    [InlineData("hevc-10", "histogram")]
    [InlineData("h264-422", "content")]
    [InlineData("h264-422", "scale")]
    [InlineData("h264-422", "turned")]
    [InlineData("mjpeg-422", "content")]
    [InlineData("mjpeg-422", "histogram")]
    [InlineData("h264-10-tff", "deinterlace")]
    [InlineData("h264-422-tff", "deinterlace")]
    [InlineData("ffv1-420-odd", "content")]
    [InlineData("ffv1-420-odd", "adaptive")]
    [InlineData("ffv1-420-odd", "scale")]
    [InlineData("ffv1-420-odd", "turned")]
    [InlineData("ffv1-422-odd", "content")]
    [InlineData("ffv1-422-odd", "histogram")]
    [InlineData("ffv1-422-odd", "scale")]
    [InlineData("ffv1-422-odd", "turned")]
    public async Task SameAsTheFileForEachFormat(string clip, string name)
    {
        if (Libs is null || FormatClip(clip) is not { } path)
            return;
        if (name == "scale" && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        if (name == "turned")
            path = Rotated(path, 90);
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "adaptive" => options with { Detector = DetectorKind.Adaptive },
            "histogram" => options with { Detector = DetectorKind.Histogram },
            "scale" => options with { Downscale = 1 },
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() },
            _ => options,
        };
        await AssertSame(path, options);
        var (video, _) = Decoded(path);
        Assert.Equal(clip.Contains("-10", StringComparison.Ordinal) ? "yuv420p10le" : clip.StartsWith("mjpeg", StringComparison.Ordinal) ? "yuvj422p" : clip.Contains("420", StringComparison.Ordinal) ? "yuv420p" : "yuv422p", video.PixelFormat);
        if (clip.EndsWith("-odd", StringComparison.Ordinal))
            Assert.Equal((1, name == "turned" ? null : "left"), (video.Height % 2 + video.Width % 2, video.ChromaLocation)); // turned: mp4 keeps no siting
        if (clip == "h264-10-topleft")
            Assert.Equal("topleft", video.ChromaLocation);
    }

    /// <summary>The three-shot clip in another pixel format (<see cref="SameAsTheFileForEachFormat"/>); null without the encoder.</summary>
    string? FormatClip(string name)
    {
        string dir = Path.GetDirectoryName(clips.ThreeShots)!, path = Path.Combine(dir, $"format-{name}" + (name.StartsWith("mjpeg", StringComparison.Ordinal) ? ".avi" : name.StartsWith("ffv1", StringComparison.Ordinal) ? ".mkv" : ".mp4"));
        lock (H264Lock)
        {
            if (File.Exists(path))
                return path;
            string[] args = name switch
            {
                "h264-10" => ["-i", clips.ThreeShots, "-pix_fmt", "yuv420p10le", "-c:v", "libx264", "-bf", "2"],
                "h264-10-topleft" => ["-i", clips.ThreeShots, "-pix_fmt", "yuv420p10le", "-c:v", "libx264", "-chroma_sample_location", "left", "-x264-params", "chromaloc=2"],
                "hevc-10" => ["-i", clips.ThreeShots, "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-x265-params", "log-level=error"],
                "h264-422" => ["-i", clips.ThreeShots, "-pix_fmt", "yuv422p", "-c:v", "libx264"],
                "mjpeg-422" => ["-i", clips.ThreeShots, "-pix_fmt", "yuvj422p", "-c:v", "mjpeg", "-q:v", "3"],
                "h264-10-tff" => ["-i", clips.ThreeShots, "-pix_fmt", "yuv420p10le", "-c:v", "libx264", "-flags", "+ildct+ilme", "-top", "1"],

                // Odd heights (swscale scales these, interpolating the chroma from where it sits): lossless, sited left.
                "ffv1-420-odd" => ["-i", clips.ThreeShots, "-vf", "format=yuv444p,crop=320:239:0:0,format=yuv420p", "-chroma_sample_location", "left", "-c:v", "ffv1"],
                "ffv1-422-odd" => ["-i", clips.ThreeShots, "-vf", "format=yuv444p,crop=320:237:0:0,format=yuv422p", "-chroma_sample_location", "left", "-c:v", "ffv1"],
                _ => ["-i", clips.ThreeShots, "-pix_fmt", "yuv422p", "-c:v", "libx264", "-flags", "+ildct+ilme", "-top", "1"],
            };
            string partial = path + ".part" + Path.GetExtension(path);
            try
            {
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. args, partial], default);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                return null;
            }

            File.Move(partial, path, true);
        }

        return path;
    }

    // A spliced stream (libx264 BT.709 then SMPTE 170M, joined in MPEG-TS): each frame converted with its own colour
    // matrix, as the file path in-process (and OpenCV) converts it, gives the file's stats byte for byte.
    [Theory]
    [InlineData(DetectorKind.Content)]
    [InlineData(DetectorKind.Histogram)]
    [InlineData(DetectorKind.Adaptive)]
    public async Task SameAsTheFileWhereTheColourTagsChange(DetectorKind detector)
    {
        if (Libs is null || TagsChange() is not { } path)
            return;
        await AssertSame(path, new DetectionOptions { Detector = detector, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
        var (video, frames) = Decoded(path);
        Assert.Equal("bt709", video.ColorSpace);
        Assert.Equal("smpte170m", frames.Last().ColorSpace);
    }

    [Fact]
    public void EachFrameIsConvertedWithItsOwnColours()
    {
        // Frames tagged BT.709 full range in a video tagged otherwise: the stats of a video tagged that way; null tags
        // the video's; a matrix the converter doesn't take is refused at its frame.
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), Detector = DetectorKind.Content, CollectStats = true };
        var tagged = Video() with { ColorSpace = "bt709", FullRange = true };
        string Stats(YuvVideo video, IEnumerable<YuvFrame> frames) => ShotDetection.Detect(video, frames, options) is var r ? r.Stats!.Csv(r.Video.Position) : "";
        string expected = Stats(tagged, Frames(tagged, 30));
        Assert.Equal(expected, Stats(Video(), Frames(Video(), 30).Select(f => f with { ColorSpace = "bt709", FullRange = true })));
        Assert.Equal(expected, Stats(tagged, Frames(tagged, 30).Select(f => f with { ColorSpace = null, FullRange = null })));
        Assert.NotEqual(expected, Stats(tagged, Frames(tagged, 30).Select(f => f with { FullRange = false })));
        Assert.NotEqual(expected, Stats(tagged, Frames(tagged, 30).Select(f => f with { ColorSpace = "smpte170m" })));
        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(Video(), Frames(Video(), 30).Select((f, i) => i == 12 ? f with { ColorSpace = "ycgco" } : f), options));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("Frame 12's colours", e.Message);
    }

    /// <summary>The three-shot clip's first 75 frames in libx264 tagged BT.709, the rest tagged SMPTE 170M (h264_metadata), joined in MPEG-TS; null without libx264.</summary>
    string? TagsChange()
    {
        string dir = Path.GetDirectoryName(clips.ThreeShots)!, path = Path.Combine(dir, "tags-change.ts");
        lock (H264Lock)
        {
            if (File.Exists(path))
                return path;
            string a = Path.Combine(dir, "tags-a.ts"), b = Path.Combine(dir, "tags-b.ts"), list = Path.Combine(dir, "tags.txt");
            try
            {
                string[] x264 = ["-c:v", "libx264", "-bf", "0", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "75", .. x264, a], default);
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-vf", "trim=start_frame=75,setpts=PTS-STARTPTS", .. x264, "-bsf:v", "h264_metadata=matrix_coefficients=6", b], default);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                return null;
            }

            File.WriteAllText(list, $"file '{a}'\nfile '{b}'\n");
            string partial = path + ".part.ts";
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", partial], default);
            File.Move(partial, path, true);
        }

        return path;
    }

    [Theory]
    [InlineData("yuv422p")]
    [InlineData("yuvj422p")]
    [InlineData("yuv420p10le")]
    public void PlanesAreCheckedForTheFormat(string format)
    {
        // 4:2:2: chroma planes of the full height; 10-bit: two bytes a sample. Planes of 8-bit 4:2:0's size are refused.
        var video = Video() with { PixelFormat = format };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, Frames(video, 3), options));
        Assert.Equal(3, ShotDetection.Detect(video, FramesOf(video, 3), options).FrameCount);

        // An odd height: taken for every format (swscale scales those; 8-bit since #630).
        Assert.Equal(3, ShotDetection.Detect(video with { Height = 47 }, FramesOf(video with { Height = 47 }, 3), options).FrameCount);
    }

    [Fact]
    public void TenBitChromaIsSitedAsTheFramesSay()
    {
        // yuv420p10le's chroma is interpolated from where it sits: the video's siting, or each frame's over it.
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), Detector = DetectorKind.Content, CollectStats = true };
        var video = Video() with { PixelFormat = "yuv420p10le" };
        string Stats(YuvVideo v, string? frameSiting) =>
            ShotDetection.Detect(v, FramesOf(v, 30).Select(f => f with { ChromaLocation = frameSiting }), options) is var r ? r.Stats!.Csv(r.Video.Position) : "";
        string topLeft = Stats(video with { ChromaLocation = "topleft" }, null);
        Assert.Equal(topLeft, Stats(video with { ChromaLocation = "left" }, "topleft"));
        Assert.NotEqual(topLeft, Stats(video with { ChromaLocation = "left" }, null));
        Assert.Equal(Stats(video, null), Stats(video with { ChromaLocation = "center" }, null));
    }

    /// <summary>Noise frames in <paramref name="video"/>'s format (10-bit samples in 0..1023), tightly packed.</summary>
    static IEnumerable<YuvFrame> FramesOf(YuvVideo video, int count)
    {
        int b = video.PixelFormat == "yuv420p10le" ? 2 : 1, w = video.Width, h = video.Height, cw = (w + 1) / 2;
        int ch = video.PixelFormat.Contains("422", StringComparison.Ordinal) ? h : (h + 1) / 2;
        for (int i = 0; i < count; i++)
        {
            var random = new Random(i);
            byte[] Plane(int width, int height)
            {
                var plane = new byte[width * height * b];
                random.NextBytes(plane);
                if (b == 2)
                {
                    for (int j = 1; j < plane.Length; j += 2)
                        plane[j] &= 3;
                }

                return plane;
            }

            yield return new YuvFrame(Plane(w, h), w * b, Plane(cw, ch), cw * b, Plane(cw, ch), cw * b);
        }
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
    static async Task AssertSame(string path, DetectionOptions options, bool timestamps = false, IReadOnlyList<FrameReader.ToneSegment>? tone = null)
    {
        var file = ShotDetection.Detect(path, options);
        Assert.True(file.Video.DecodedInProcess);
        var (video, frames) = Decoded(path, timestamps);
        video = video with { ReferenceTone = tone };
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
        Assert.Equal(file.ReferenceTone, ours.ReferenceTone);
        Assert.Equal(file.BlackSpans, ours.BlackSpans);
        Assert.Equal(ours.BlackSpans, awaited.BlackSpans);
        Assert.Equal(file.FreezeSpans, ours.FreezeSpans);
        Assert.Equal(ours.FreezeSpans, awaited.FreezeSpans);
        Assert.Equal(ours.ReferenceTone, awaited.ReferenceTone);
        Assert.Equal(file.Cuts, ours.Cuts);
        Assert.Equal(file.Shots, ours.Shots);
        Assert.Equal(Shots.Csv(file.Shots, cuts: file.Cuts), Shots.Csv(ours.Shots, cuts: ours.Cuts));
        Assert.Equal(file.Stats?.Csv(file.Video.Position), ours.Stats?.Csv(ours.Video.Position));
        Assert.Equal((file.Video.ProcessedWidth, file.Video.ProcessedHeight, file.Video.FrameRate, file.Video.OpenCvFrameCount, file.Video.Duration),
            (ours.Video.ProcessedWidth, ours.Video.ProcessedHeight, ours.Video.FrameRate, ours.Video.OpenCvFrameCount, ours.Video.Duration));
        Assert.Equal((file.Video.FieldOrder, file.Video.Deinterlaced), (ours.Video.FieldOrder, ours.Video.Deinterlaced));

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

    // Deinterlaced: an interlaced file split at its keyframes, each range decoded on a decoder of its own from its keyframe
    // (now two frames before the range's first: yadif's previous frame for the frame before it) to two frames past its
    // end, as RangeFrames says, merges into the sequential deinterlaced frame-source result, and the file's. H.264 MBAFF
    // top and bottom field first, MPEG-2, 10-bit and 4:2:2 MBAFF.
    [Theory]
    [InlineData("tff", DetectorKind.Content)]
    [InlineData("tff", DetectorKind.Adaptive)]
    [InlineData("bff", DetectorKind.Histogram)]
    [InlineData("mpeg2", DetectorKind.Content)]
    [InlineData("mpeg2", DetectorKind.Hash)]
    [InlineData("h264-10-tff", DetectorKind.Content)]
    [InlineData("h264-422-tff", DetectorKind.Content)]
    public void DeinterlacedRangesFromKeyframesMergeIntoTheSequentialResult(string clip, DetectorKind detector)
    {
        if (Libs is null)
            return;
        string? file = clip.StartsWith("h264", StringComparison.Ordinal) ? FormatClip(clip) : InterlacedClip(clip);
        if (file is not { } path)
            return;
        var options = new DetectionOptions { Detector = detector, CollectStats = true, Yuv420Converter = new SwscaleYuv420(), Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() };
        var fromFile = ShotDetection.Detect(path, options with { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Deinterlace = DeinterlaceMode.On });
        Assert.True(fromFile.Video.Deinterlaced);
        var (video, frames) = Decoded(path);
        if (video.FieldOrder is FrameReader.FieldOrder.Progressive or FrameReader.FieldOrder.Unknown)
            options = options with { Deinterlace = DeinterlaceMode.On };
        var sequential = frames.ToList();
        var flags = sequential.ToDictionary(f => f.Pts);
        var pts = sequential.Select(f => f.Pts).ToList();
        var expected = ShotDetection.Detect(video, sequential, options);
        Assert.True(expected.Video.Deinterlaced);

        List<long> keyframes;
        using (var keys = new FrameReader.FrameDecoder(path, DecoderOptions(path) with { KeyframesOnly = true }))
        {
            keyframes = [];
            while (keys.Next())
                keyframes.Add(keys.Pts);
        }

        // Two or three ranges, each starting two frames after a keyframe nearest a third and two thirds of the way.
        var inner = keyframes.Select(p => pts.IndexOf(p)).Where(k => k > 0 && k < pts.Count - 4).ToList();
        Assert.NotEmpty(inner);
        int[] starts = [0, .. new[] { pts.Count / 3, 2 * pts.Count / 3 }.Select(t => inner.MinBy(k => Math.Abs(k - t)) + 2).Distinct()];
        var ranges = new List<RangeScores>();
        for (int i = 0; i < starts.Length; i++)
        {
            int first = starts[i], end = i + 1 < starts.Length ? starts[i + 1] : pts.Count;
            var (from, to) = ShotDetection.RangeFrames(video, options, first, end);
            Assert.Equal(Math.Max(0, first - 2), from);
            var given = first == 0 ? sequential : Decoded(path, from: pts[(int)from], flags);
            ranges.Add(RangeScores.FromBytes(ShotDetection.ScoreRange(video, given.Take((int)(Math.Min(pts.Count, to) - from)), first, options).ToBytes()));
        }

        var merged = ShotDetection.Merge(video, ranges, options);
        Assert.Equal(expected.FrameCount, merged.FrameCount);
        Assert.Equal(expected.Cuts, merged.Cuts);
        Assert.Equal(Shots.Csv(expected.Shots, cuts: expected.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
        Assert.Equal(expected.Stats!.Csv(expected.Video.Position), merged.Stats!.Csv(merged.Video.Position));
        Assert.Equal(Shots.Csv(fromFile.Shots, cuts: fromFile.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
        Assert.Equal(fromFile.Stats!.Csv(fromFile.Video.Position), merged.Stats!.Csv(merged.Video.Position));
    }

    // load-scenes from an edited list (a cut removed, two added, one of them out of order), for frames from the caller:
    // the video's properties as the frame source gives them (every frame's timestamp too, as a file's from its packets)
    // and the list as text give LoadScenes(path)'s shots, cuts, positions, CSV and timeline exports byte for byte, by
    // frame number and by timecode, with a time range, and with short shots dropped or the last one merged.
    [Theory]
    [InlineData("mpeg4", "Start Frame", "")]
    [InlineData("mpeg4", "Start Timecode", "")]
    [InlineData("mpeg4", "Start Frame", "start")]
    [InlineData("mpeg4", "Start Frame", "drop")]
    [InlineData("h264", "Start Frame", "")]
    [InlineData("h264", "Start Time (seconds)", "end")]
    [InlineData("h264", "Start Frame", "merge")]
    public void LoadScenesFromTextIsTheFiles(string clip, string column, string name)
    {
        string? file = clip == "h264" ? H264Clip() : clips.ThreeShots;
        if (Libs is null || file is not { } path)
            return;
        var detect = new DetectionOptions { Detector = DetectorKind.Content, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess };
        var detected = ShotDetection.Detect(path, detect);
        Assert.True(detected.Cuts.Count >= 2);
        var fps = detected.Video.FrameRate;

        // The list edited: the first cut gone, cuts at frames 20 and 7 (out of order, as a hand-edited list can be).
        var starts = new List<long> { 0, 20, 7 };
        starts.AddRange(detected.Cuts.Skip(1).Select(c => c.FrameNum));
        var edited = starts.Order().Select((f, i) => new Shot(i + 1, FrameTime.Frame(f, fps), FrameTime.Frame(i + 1 < starts.Count ? starts.Order().ElementAt(i + 1) : detected.FrameCount, fps))).ToList();
        string text = Shots.Csv(edited);
        var lines = text.Split('\n').ToList();
        (lines[3], lines[4]) = (lines[4], lines[3]); // two shots' rows swapped: LoadScenes sorts
        text = string.Join('\n', lines);

        var options = new DetectionOptions { MinSceneLength = "10", FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess };
        options = name switch
        {
            "start" => options with { StartTime = "1.5" },
            "end" => options with { StartTime = "15", EndTime = "4s" },
            "drop" => options with { DropShortScenes = true },
            "merge" => options with { MergeLastScene = true, MinSceneLength = "3s" },
            _ => options,
        };
        string csv = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.csv");
        DetectionResult fromFile;
        try
        {
            File.WriteAllText(csv, text);
            fromFile = ShotDetection.LoadScenes(path, csv, column, options);
        }
        finally
        {
            File.Delete(csv);
        }

        var (video, _) = Decoded(path, timestamps: true);
        var ours = ShotDetection.LoadScenes(video, new StringReader(text), column, options with { FfmpegDirectory = null, Decoder = VideoDecoder.Auto });
        Assert.Null(ours.VideoPath);
        Assert.Equal(fromFile.FrameCount, ours.FrameCount);
        Assert.Equal(fromFile.MinSceneLengthFrames, ours.MinSceneLengthFrames);
        Assert.Equal(fromFile.Cuts, ours.Cuts);
        Assert.Equal(fromFile.Shots, ours.Shots);
        Assert.Equal(Shots.Csv(fromFile.Shots, cuts: fromFile.Cuts), Shots.Csv(ours.Shots, cuts: ours.Cuts));
        Assert.Equal(Timeline.Edl(fromFile, title: "edited"), Timeline.Edl(ours, title: "edited"));
        Assert.Equal(Timeline.Qp(fromFile), Timeline.Qp(ours));
        Assert.Equal(Timeline.Keyframes(fromFile), Timeline.Keyframes(ours));
        Assert.Equal(Timeline.ShotChanges(fromFile), Timeline.ShotChanges(ours));
        for (int k = 0; k <= fromFile.FrameCount; k++)
            Assert.Equal(fromFile.Video.Position(k), ours.Video.Position(k));
        Assert.True(ours.Shots.Count > 1);
    }

    // The case shotdetector-web met: an MJPEG file (every frame a keyframe) deinterlaced in runs that stop anywhere, each
    // resumed at the earlier ranges' EndFrame - 1 on a decoder of its own from RangeFrames' start for it: the merge is
    // the sequential deinterlaced result and the file's.
    [Fact]
    public void DeinterlacedRunsOfAnAllKeyframeFileResumeAtEndFrame()
    {
        if (Libs is null || FormatClip("mjpeg-422") is not { } path)
            return;
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, Yuv420Converter = new SwscaleYuv420(), Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        var fromFile = ShotDetection.Detect(path, options with { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess });
        var (video, frames) = Decoded(path);
        var sequential = frames.ToList();
        var pts = sequential.Select(f => f.Pts).ToList();
        var expected = ShotDetection.Detect(video, sequential, options);
        var ranges = new List<RangeScores>();
        int first = 0;
        foreach (int stop in new[] { 40, 41, 77, 120, pts.Count })
        {
            long from = ShotDetection.RangeFrames(video, options, first, first + 1).From;
            var given = from == 0 ? sequential : Decoded(path, from: pts[(int)from]).ToList();
            ranges.Add(RangeScores.FromBytes(ShotDetection.ScoreRange(video, ranges, given.Take(stop - (int)from), first, options).ToBytes()));
            Assert.Equal(stop - 1, ranges[^1].EndFrame);
            first = ranges[^1].EndFrame - 1;
        }

        var merged = ShotDetection.Merge(video, ranges, options);
        Assert.Equal(Shots.Csv(expected.Shots, cuts: expected.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
        Assert.Equal(expected.Stats!.Csv(expected.Video.Position), merged.Stats!.Csv(merged.Video.Position));
        Assert.Equal(Shots.Csv(fromFile.Shots, cuts: fromFile.Cuts), Shots.Csv(merged.Shots, cuts: merged.Cuts));
        Assert.Equal(fromFile.Stats!.Csv(fromFile.Video.Position), merged.Stats!.Csv(merged.Video.Position));
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

    /// <summary>
    /// The file's frames from the keyframe at <paramref name="from"/> on, decoded after a seek to it, with each frame's
    /// interlacing flags by its pts from <paramref name="flags"/> (a sequential decode's), when given.
    /// </summary>
    static IEnumerable<YuvFrame> Decoded(string path, long from, IReadOnlyDictionary<long, YuvFrame>? flags = null)
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
            var frame = new YuvFrame(y, ys, u, us, v, vs) { Width = decoder.Width, Height = decoder.Height, ColorSpace = decoder.ColorSpace, ChromaLocation = decoder.ChromaLocation, Pts = decoder.Pts };
            yield return flags?.GetValueOrDefault(decoder.Pts) is { } f ? frame with { Interlaced = f.Interlaced, TopFieldFirst = f.TopFieldFirst } : frame;
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
        string? chromaLocation;
        using (var first = new FrameReader.FrameDecoder(path, decoderOptions))
        {
            Assert.True(first.Next());
            colorSpace = first.ColorSpace ?? info.ColorSpace ?? "";
            colorRange = first.ColorRange ?? info.ColorRange ?? "";
            chromaLocation = first.ChromaLocation ?? info.ChromaLocation;
        }

        var video = new YuvVideo
        {
            Width = info.Width,
            Height = info.Height,
            FrameRate = info.AverageFrameRate,
            PixelFormat = info.PixelFormat!,
            ColorSpace = colorSpace,
            FullRange = colorRange == "pc",
            ChromaLocation = chromaLocation,
            TimeBase = info.TimeBase,
            StartPts = info.StartPts ?? 0,
            FrameCount = info.FrameCount,
            Duration = info.DurationPts is { } d ? TimeSpan.FromTicks((long)Math.Round(d * 1e7 * info.TimeBase.Num / info.TimeBase.Den / 10) * 10) : probed.Duration,
            Codec = info.Codec,
            Rotation = (int)Math.Round(info.Rotation ?? 0) % 360,
            FrameTimestamps = timestamps ? [.. probed.PacketTimestamps!.Select(p => p!.Value).Order()] : null,
            FieldOrder = info.FieldOrder,
        };

        // Each frame's interlaced and top-field-first flags as FFmpeg's decoder sets them (ffprobe's), in output order.
        var flags = VideoReader.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=interlaced_frame,top_field_first", "-of", "compact=p=0:nk=0", path], default)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Interlaced: line.Contains("interlaced_frame=1", StringComparison.Ordinal), TopFieldFirst: line.Contains("top_field_first=1", StringComparison.Ordinal)))
            .ToList();
        return (video, Frames());

        IEnumerable<YuvFrame> Frames()
        {
            using var decoder = new FrameReader.FrameDecoder(path, decoderOptions);
            for (int i = 0; decoder.Next(); i++)
            {
                var y = decoder.GetPlane(0, out int ys).ToArray();
                var u = decoder.GetPlane(1, out int us).ToArray();
                var v = decoder.GetPlane(2, out int vs).ToArray();
                yield return new YuvFrame(y, ys, u, us, v, vs) { Width = decoder.Width, Height = decoder.Height, ColorSpace = decoder.ColorSpace, FullRange = decoder.ColorRange is { } range ? range == "pc" : null, ChromaLocation = decoder.ChromaLocation, Pts = decoder.Pts, Interlaced = flags[i].Interlaced, TopFieldFirst = flags[i].TopFieldFirst };
            }
        }
    }

    // Black spans (DetectionOptions.BlackDetect) from the frames given: blackdetect's count on each frame's luma as given,
    // or deinterlaced whole when deinterlacing, at its own pts: the file path's spans. The clip fades through black (2 s)
    // and ends black; 8-bit, 10-bit, and MBAFF H.264 deinterlaced (blackdetect after yadif, in its time base).
    [Theory]
    [InlineData("yuv420p", "")]
    [InlineData("yuv420p10le", "")]
    [InlineData("yuv420p", "tff")]
    public async Task SameAsTheFileWithBlackSpans(string format, string interlaced)
    {
        if (Libs is null)
            return;
        string path = Path.Combine(Path.GetDirectoryName(clips.ThreeShots)!, $"black-{format}-{interlaced}.mp4");
        lock (H264Lock)
        {
            if (!File.Exists(path))
            {
                string partial = path + ".part.mp4";
                string[] field = interlaced == "tff" ? ["-flags", "+ildct+ilme", "-top", "1"] : [];
                VideoReader.Run("ffmpeg",
                    ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2,fade=out:st=1:d=1", "-f", "lavfi", "-i", "color=black:s=320x240:r=25:d=2",
                        "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2,fade=in:st=0:d=0.5", "-f", "lavfi", "-i", "color=black:s=320x240:r=25:d=0.6",
                        "-filter_complex", "[0][1][2][3]concat=n=4:v=1", "-pix_fmt", format, "-c:v", "libx264", .. field, partial],
                    default);
                File.Move(partial, path, true);
            }
        }

        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            CollectStats = true,
            FfmpegDirectory = Libs,
            Decoder = VideoDecoder.InProcess,
            Yuv420Converter = new SwscaleYuv420(),
            BlackDetect = new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.2) },
        };
        if (interlaced.Length > 0)
            options = options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        await AssertSame(path, options);
        Assert.Equal(2, ShotDetection.Detect(path, options).BlackSpans!.Count);
    }

    // Still pictures (DetectionOptions.FreezeDetect) from the frames given: freezedetect's comparison of each frame's planes
    // as given, or deinterlaced whole when deinterlacing: the file path's spans. Held stills, one under light noise, one to
    // the end; 8-bit, 10-bit, and MBAFF H.264 deinterlaced.
    [Theory]
    [InlineData("yuv420p", "")]
    [InlineData("yuv420p10le", "")]
    [InlineData("yuv420p", "tff")]
    public async Task SameAsTheFileWithFreezeSpans(string format, string interlaced)
    {
        if (Libs is null)
            return;
        string path = Path.Combine(Path.GetDirectoryName(clips.ThreeShots)!, $"freeze-{format}-{interlaced}.mp4");
        lock (H264Lock)
        {
            if (!File.Exists(path))
            {
                string partial = path + ".part.mp4";
                string[] field = interlaced == "tff" ? ["-flags", "+ildct+ilme", "-top", "1"] : [];
                VideoReader.Run("ffmpeg",
                    ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1",
                        "-filter_complex", "[0]tpad=stop_mode=clone:stop_duration=1.5[a];[1]tpad=stop_mode=clone:stop_duration=1,noise=alls=2:allf=t[b];[a][b]concat=n=2:v=1",
                        "-pix_fmt", format, "-c:v", "libx264", "-crf", "12", .. field, partial],
                    default);
                File.Move(partial, path, true);
            }
        }

        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            CollectStats = true,
            FfmpegDirectory = Libs,
            Decoder = VideoDecoder.InProcess,
            Yuv420Converter = new SwscaleYuv420(),
            FreezeDetect = new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(0.5) },
        };
        if (interlaced.Length > 0)
            options = options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        await AssertSame(path, options);
        Assert.NotEmpty(ShotDetection.Detect(path, options).FreezeSpans!);
    }

    /// <summary>
    /// The three-shot clip interlaced: libx264 MBAFF with top-field-first frames ("tff", field order tt) or bottom-first
    /// ones ("bff", bb), or MPEG-2 ("mpeg2", top first).
    /// </summary>
    string InterlacedClip(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(clips.ThreeShots)!, $"interlaced-{name}.mp4");
        lock (H264Lock)
        {
            if (!File.Exists(path))
            {
                string[] codec = name switch
                {
                    "tff" => ["-c:v", "libx264", "-flags", "+ildct+ilme", "-top", "1", "-bf", "3"],
                    "bff" => ["-c:v", "libx264", "-x264-params", "interlaced=1:bff=1", "-bf", "3"],
                    _ => ["-vf", "setfield=tff", "-c:v", "mpeg2video", "-q:v", "3", "-flags", "+ildct+ilme"],
                };
                string partial = path + ".part" + Path.GetExtension(path);
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, .. codec, partial], default);
                File.Move(partial, path, true);
            }
        }

        return path;
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
