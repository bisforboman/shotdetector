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
        var r = ShotDetection.Detect(clips.ThreeShots, Content(new() { StartTime = "1s", Duration = "4s" }));
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
        Assert.Equal((160, 240), (r.Video.Width, r.Video.Height)); // smaller than 256: no downscale
        Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
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

    [Fact]
    public void CropOutsideTheFrameIsRefused() =>
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(clips.ThreeShots, new() { Crop = (400, 0, 500, 100) }));
}
