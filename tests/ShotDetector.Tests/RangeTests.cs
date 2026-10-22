using ShotDetector;

/// <summary>Time range, frame skip and crop, on the 150-frame 25 fps clip with cuts at 50 and 100.</summary>
public class RangeTests(Clips clips) : IClassFixture<Clips>
{
    static readonly Fps Fps25 = new(25, 1);

    static DetectionOptions Content(DetectionOptions? o = null) => (o ?? new()) with { Detector = DetectorKind.Content };

    [Theory]
    [InlineData("00:01:30.500", false, 90.5)]
    [InlineData("12.5s", false, 12.5)]
    [InlineData("12.5", false, 12.5)]
    [InlineData("300", false, 12.0)]   // frames
    [InlineData("300", true, 11.96)]   // 1-based frame number: frame 299
    [InlineData("0", true, 0.0)]
    public void TimecodesParseLikeScenedetect(string text, bool oneBased, double seconds) =>
        Assert.Equal(seconds, ShotDetection.TimecodeSeconds(text, Fps25, oneBased), 9);

    [Theory]
    [InlineData("15", 15)]
    [InlineData("0.6s", 15)]
    [InlineData("0.6", 15)]
    [InlineData("00:00:00.600", 15)]
    [InlineData("00:00:01", 25)]
    [InlineData("0.5s", 12)]   // 12.5 rounds half to even
    public void MinSceneLengthTakesScenedetectsFormats(string value, int frames) =>
        Assert.Equal(frames, ShotDetection.MinSceneLengthInFrames(value, Fps25));

    [Fact]
    public void StartAndEndSelectFrames()
    {
        // Frames 50 .. 99: the cut at 100 is outside, so one shot [50, 100).
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "2s", EndTime = "4s" }));
        Assert.Equal(50, r.FrameCount);
        Assert.Equal([(50L, 100L)], r.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));
        Assert.Equal("00:00:02.000", r.Shots[0].Start.Timecode());
    }

    [Fact]
    public void DurationCountsFromTheStart()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "1s", RangeDuration = "4s" }));
        Assert.Equal(100, r.FrameCount); // 25 .. 124
        Assert.Equal([(25L, 50L), (50L, 100L), (100L, 125L)], r.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));
    }

    [Fact]
    public void StartAsFrameNumberIsOneBased()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "51" }));
        Assert.Equal(50, r.Shots[0].Start.FrameNum);
        Assert.Equal(100, r.FrameCount);
    }

    [Fact]
    public void FrameSkipAnalysesEveryNthFrame()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { FrameSkip = 1 }));
        Assert.Equal(150, r.FrameCount); // all frames are read, every other one analysed
        Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void FrameSkipReadsAheadBeforeCheckingTheEnd()
    {
        // scenedetect analyses a frame, skips the next ones, then checks the end: with stride 3 from
        // frame 50 and an end at 4 s (frame 100), it analyses 98 and reads up to 100.
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "2s", EndTime = "4s", FrameSkip = 2 }));
        Assert.Equal(51, r.FrameCount);
        Assert.Equal(101, r.Shots[^1].End.FrameNum);
    }

    [Fact]
    public void FrameSkipWithStatsIsRefused() =>
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { FrameSkip = 1, CollectStats = true }));

    [Fact]
    public void CropAnalysesPartOfTheFrame()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { Crop = (0, 0, 159, 239) }));
        Assert.Equal((0, 0, 160, 240), r.Video.CropRegion);
        Assert.Equal((160, 240), (r.Video.ProcessedWidth, r.Video.ProcessedHeight)); // smaller than 256: no downscale
        Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void CroppedVideoDownscalesFromTheCropSizePlusOne()
    {
        // A 300x240 crop: scenedetect's factor comes from 301x241 (255x204), not 300x240 (256x205).
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { Crop = (0, 0, 299, 239) }));
        Assert.Equal((255, 204), (r.Video.ProcessedWidth, r.Video.ProcessedHeight));
    }

    [Theory]
    [InlineData(0, 0, 499, 299, 255, 153)]      // 500x300 crop; scenedetect's factor comes from 501x301
    [InlineData(600, 0, 1000, 479, 135, 255)]   // runs off the right edge: 254x480 crop, factor from 255x481
    public void CroppedDownscaleMatchesScenedetect(int x0, int y0, int x1, int y1, int w, int h)
    {
        int actualW = Math.Min(x1 + 1, 854) - x0, actualH = Math.Min(y1 + 1, 480) - y0;
        Assert.Equal((w, h), VideoReader.DownscaledSize(actualW + 1, actualH + 1, actualW, actualH));
    }

    [Fact]
    public void SeekingStartsOnExactlyTheRequestedFrame()
    {
        var all = new VideoReader(clips.ThreeShots).Frames().Select(f => f.ToArray()).ToList();
        foreach (int start in new[] { 1, 49, 50, 51, 149 })
        {
            var seeked = new VideoReader(clips.ThreeShots).Frames(start, 2).Select(f => f.ToArray()).ToList();
            Assert.Equal(all.Skip(start).Take(2), seeked);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekingInATransportStreamLosesNoFrames(bool inProcess)
    {
        // MPEG-TS has no index: a seek finds a packet by time and decoding starts at the next keyframe after it. With
        // keyframes at 0 and 100, a seek for 60 lands at 100, and one for 120 after the last keyframe, at nothing.
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (inProcess && libs is null)
            return;
        string ts = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.ts");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c:v", "mpeg2video", "-g", "100", "-q:v", "3", ts], default);
        try
        {
            var o = new DetectionOptions { FfmpegDirectory = libs, Decoder = inProcess ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess };
            var all = new VideoReader(ts, o).Frames().Select(f => f.ToArray()).ToList();
            foreach (int start in new[] { 60, 120 })
                Assert.Equal(all.Skip(start).Take(10), new VideoReader(ts, o).Frames(start, 10).Select(f => f.ToArray()));
        }
        finally
        {
            File.Delete(ts);
        }
    }

    // Two copies of the clip's first 2 s as a transport stream, joined by ffmpeg's concat demuxer with -c copy, the
    // second copy shifted by 0.024 s less than the first's length (the audio's, when that is shorter): its keyframe (50)
    // comes 0.016 s after the first copy's last frame (49, no keyframe), under half a frame. A seek for 49 lands on that
    // keyframe; the check for a late landing allowed half a frame, so 50 passed for 49 and every later frame was one off
    // (#452's 12-minute MPEG-TS fell back from parallel detection so).
    internal static string JoinedTransportStream(string clip)
    {
        string name = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}");
        string copy = name + "-copy.ts", list = name + ".txt", joined = name + ".ts";
        try
        {
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clip, "-an", "-frames:v", "50", "-c:v", "mpeg2video", "-g", "12", "-bf", "2", "-q:v", "3", copy], default);
            File.WriteAllText(list, $"file '{copy}'\nduration 1.976\nfile '{copy}'\nduration 1.976\n");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", joined], default);
            return joined;
        }
        finally
        {
            File.Delete(copy);
            File.Delete(list);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekingToAJoinCloserThanHalfAFrameLosesNoFrames(bool inProcess)
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (inProcess && libs is null)
            return;
        string ts = JoinedTransportStream(clips.ThreeShots);
        try
        {
            var o = new DetectionOptions { FfmpegDirectory = libs, Decoder = inProcess ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess };
            var all = new VideoReader(ts, o).Frames().Select(f => f.ToArray()).ToList();
            Assert.Equal(100, all.Count);
            var reader = new VideoReader(ts, o);
            Assert.Equal(all.Skip(49).Take(10), reader.Frames(49, 10).Select(f => f.ToArray()));
            Assert.Equal(Enumerable.Range(49, 10).Select(new VideoReader(ts, o).Position), Enumerable.Range(49, 10).Select(reader.Position));
        }
        finally
        {
            File.Delete(ts);
        }
    }

    // Interlaced MPEG-TS, deinterlaced in-process: a seek for a keyframe's frame lands on that very frame (decoding starts
    // at the next keyframe after the packet found by time), so yadif would get no previous frame for it, and its pixels
    // would differ from the sequential read's; such a landing reads again from further back.
    [Fact]
    public void SeekingAnInterlacedTransportStreamToAKeyframeDeinterlacesAsSequentially()
    {
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (libs is null)
            return;
        string ts = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.ts");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-vf", "setfield=tff", "-c:v", "mpeg2video", "-flags", "+ildct+ilme", "-g", "12", "-q:v", "3", ts], default);
        try
        {
            var o = new DetectionOptions { FfmpegDirectory = libs, Decoder = VideoDecoder.InProcess, Deinterlace = DeinterlaceMode.Auto };
            var reader = new VideoReader(ts, o);
            Assert.True(reader.Deinterlaces);
            var all = reader.Frames().Select(f => f.ToArray()).ToList();
            foreach (int start in new[] { 24, 60, 61 })
                Assert.Equal(all.Skip(start).Take(3), new VideoReader(ts, o).Frames(start, 3).Select(f => f.ToArray()));
        }
        finally
        {
            File.Delete(ts);
        }
    }

    // DetectedVideo.Crop: the region as the option's corners, ordered and clamped, so it can be passed back.
    [Fact]
    public void CropComesBackAsCorners()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { Crop = (299, 239, 40, 30) }));   // corners swapped
        Assert.Equal((40, 30, 260, 210), r.Video.CropRegion);
        Assert.Equal((40, 30, 299, 239), r.Video.Crop);
        var clamped = ShotDetection.Detect(clips.ThreeShots, Content(new() { Crop = (10, 0, 1000, 1000) }));
        Assert.Equal((10, 0, 319, 239), clamped.Video.Crop);
        Assert.Equal(clamped.Video.CropRegion, ShotDetection.Detect(clips.ThreeShots, Content(new() { Crop = clamped.Video.Crop })).Video.CropRegion);
        Assert.Equal((0, 0, 319, 239), ShotDetection.Detect(clips.ThreeShots, Content(new())).Video.Crop);   // uncropped: the whole frame
    }

    // The TimeSpan options: the same detection as their strings; one form per option.
    [Fact]
    public void TimeSpanOptionsAreTheirStrings()
    {
        var text = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "1.5s", EndTime = "5s", MinSceneLength = "0.4s" }));
        var typed = ShotDetection.Detect(clips.ThreeShots, Content(new()
        {
            Start = TimeSpan.FromSeconds(1.5),
            End = TimeSpan.FromSeconds(5),
            MinSceneDuration = TimeSpan.FromSeconds(0.4),
        }));
        Assert.Equal(text.Shots, typed.Shots);
        Assert.Equal(
            ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "1.5s", RangeDuration = "3.5s" })).Shots,
            ShotDetection.Detect(clips.ThreeShots, Content(new() { Start = TimeSpan.FromSeconds(1.5), Duration = TimeSpan.FromSeconds(3.5) })).Shots);
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { StartTime = "1s", Start = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { MinSceneLength = "10", MinSceneDuration = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { End = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void CropOutsideTheFrameIsRefused() =>
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { Crop = (400, 0, 500, 100) }));
}
