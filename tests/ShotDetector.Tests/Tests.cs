using ShotDetector;

public class HsvTests
{
    // Expected values from cv2.cvtColor(..., COLOR_BGR2HSV).
    [Theory]
    [InlineData(0, 0, 255, 0, 255, 255)]     // red
    [InlineData(0, 255, 0, 60, 255, 255)]    // green
    [InlineData(255, 0, 0, 120, 255, 255)]   // blue
    [InlineData(128, 128, 128, 0, 0, 128)]   // gray
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(50, 100, 200, 10, 191, 200)]
    [InlineData(200, 30, 90, 131, 217, 200)]
    [InlineData(13, 250, 251, 30, 242, 251)]
    [InlineData(255, 0, 255, 150, 255, 255)]
    [InlineData(1, 2, 3, 15, 170, 3)]
    public void MatchesOpenCv(byte b, byte g, byte r, byte h, byte s, byte v) =>
        Assert.Equal((h, s, v), Hsv.FromBgr(b, g, r));
}

public class ContentScorerTests
{
    static byte[] Solid(byte b, byte g, byte r, int pixels = 16) =>
        Enumerable.Range(0, pixels).SelectMany(_ => new[] { b, g, r }).ToArray();

    [Fact]
    public void FirstFrameScoresZero() => Assert.Equal(0, new ContentScorer().Score(Solid(10, 20, 30)));

    [Fact]
    public void BlackToWhiteIsLumaDeltaOverThree()
    {
        var s = new ContentScorer();
        s.Score(Solid(0, 0, 0));
        Assert.Equal(85.0, s.Score(Solid(255, 255, 255)), 9); // (0 + 0 + 255) / 3
    }

    [Fact]
    public void RedToBlueIsHueDeltaOverThree()
    {
        var s = new ContentScorer();
        s.Score(Solid(0, 0, 255));
        Assert.Equal(40.0, s.Score(Solid(255, 0, 0)), 9); // hue 0 → 120
    }

    [Fact]
    public void LumaOnlyIgnoresHue()
    {
        var s = ContentScorer.LumaOnly();
        s.Score(Solid(0, 0, 255));
        Assert.Equal(0.0, s.Score(Solid(255, 0, 0)));
    }

    [Fact]
    public void MeanPixelDistanceIsPerPixel() =>
        Assert.Equal(2.5, ContentScorer.MeanPixelDistance([0, 10, 5, 5], [0, 0, 5, 5]));
}

public class DetectorTests
{
    static List<int> RunContent(double[] scores, int minSceneLen = 15, int fps = 25)
    {
        var d = new ContentDetector(new ContentScorer(), new Fps(fps, 1), 27, minSceneLen);
        return scores.Select((s, i) => d.ProcessScore(i, s)).OfType<int>().ToList();
    }

    static List<int> RunAdaptive(double[] scores, int minSceneLen = 15)
    {
        var d = new AdaptiveDetector(new ContentScorer(), minSceneLen: minSceneLen);
        return scores.Select((s, i) => d.ProcessScore(i, s)).OfType<int>().ToList();
    }

    static double[] Scores(int count, double baseline, params (int Frame, double Score)[] spikes)
    {
        var a = Enumerable.Repeat(baseline, count).ToArray();
        a[0] = 0; // first frame always scores 0
        foreach (var (f, s) in spikes) a[f] = s;
        return a;
    }

    [Fact]
    public void ContentCutsAboveThreshold() => Assert.Equal([20, 50], RunContent(Scores(80, 2, (20, 40), (50, 27))));

    [Fact]
    public void ContentIgnoresCutTooCloseToStart() => Assert.Empty(RunContent(Scores(80, 2, (10, 40))));

    [Fact]
    public void ContentMergeFilterSwallowsFlashThenEmitsLaterCut() =>
        // 25 is too close to 20, which arms merging; 45 is emitted once 15 quiet frames follow it.
        Assert.Equal([20, 45], RunContent(Scores(80, 2, (20, 40), (25, 40), (45, 40))));

    [Fact]
    public void ContentMinLengthUsesMicrosecondRoundedSeconds()
    {
        // Sintel trailer, 24 fps, min length 14: 672 - 658 is exactly 14 frames, but in µs-rounded
        // seconds 28.0 - 27.416667 = 0.583333 < 14/24, so PySceneDetect rejects it. 20→34 rounds the other way.
        Assert.Equal([658], RunContent(Scores(700, 2, (658, 40), (672, 40)), minSceneLen: 14, fps: 24));
        Assert.Equal([20, 34], RunContent(Scores(60, 2, (20, 40), (34, 40)), minSceneLen: 14, fps: 24));
    }

    [Fact]
    public void ContentWithoutMinLengthCutsEveryFrameAbove() =>
        Assert.Equal([20, 21], RunContent(Scores(40, 2, (20, 40), (21, 40)), minSceneLen: 0));

    [Fact]
    public void AdaptiveCutsOnSpikeRelativeToNeighbours() => Assert.Equal([20], RunAdaptive(Scores(40, 5, (20, 20))));

    [Fact]
    public void AdaptiveNeedsMinContentVal() => Assert.Empty(RunAdaptive(Scores(40, 1, (20, 14)))); // ratio 14, but < 15

    [Fact]
    public void AdaptiveNeedsRatioAboveThreshold() => Assert.Empty(RunAdaptive(Scores(40, 10, (20, 29)))); // ratio 2.9

    [Fact]
    public void AdaptiveZeroAverageCountsAsMaxRatio() => Assert.Equal([20], RunAdaptive(Scores(40, 0, (20, 15))));

    [Fact]
    public void AdaptiveMinLengthIsMeasuredFromCurrentFrame()
    {
        // Spike at 13 is evaluated at frame 15, and 15 - 0 >= 15, so it counts.
        Assert.Equal([13], RunAdaptive(Scores(40, 1, (13, 40))));
        Assert.Empty(RunAdaptive(Scores(40, 1, (12, 40))));
        // After a cut, the next must come >= 15 frames after it (minus the window lag).
        Assert.Equal([20], RunAdaptive(Scores(60, 1, (20, 40), (32, 40))));
        Assert.Equal([20, 33], RunAdaptive(Scores(60, 1, (20, 40), (33, 40))));
    }

    [Fact]
    public void DetectorsFindCutInSyntheticFrames()
    {
        byte[] black = new byte[8 * 8 * 3], white = Enumerable.Repeat((byte)255, 8 * 8 * 3).ToArray();
        foreach (IDetector d in new IDetector[] { new ContentDetector(new ContentScorer(), new Fps(25, 1)),new AdaptiveDetector(new ContentScorer()) })
        {
            var cuts = new List<int>();
            for (int i = 0; i < 60; i++)
                if (d.ProcessFrame(i, i < 30 ? black : white) is int c) cuts.Add(c);
            Assert.Equal([30], cuts);
        }
    }
}

public class CvResizeTests
{
    [Fact]
    public void MatchesOpenCvInterLinear()
    {
        // 5x1 → 2x1; expected from cv2.resize(img, (2, 1), interpolation=cv2.INTER_LINEAR).
        byte[] src = [0, 0, 0, 100, 100, 100, 200, 200, 200, 255, 255, 255, 10, 20, 30];
        var dst = new byte[6];
        CvResize.Linear(src, 5, 1, dst, 2, 1);
        Assert.Equal([75, 75, 75, 194, 196, 199], dst);
    }
}

public class ShotsTests
{
    static readonly Fps Ntsc = new(30000, 1001);

    [Theory]
    [InlineData(0, 30000, 1001, "00:00:00.000")]
    [InlineData(90, 30000, 1001, "00:00:03.003")]
    [InlineData(1798, 30000, 1001, "00:00:59.993")]
    [InlineData(1499, 25, 1, "00:00:59.960")]
    [InlineData(90000, 25, 1, "01:00:00.000")]
    [InlineData(59999600, 1000000, 1, "00:01:00.000")] // 59.9996s rounds up into the next minute
    public void TimecodeFormatting(long frame, int num, int den, string expected) =>
        Assert.Equal(expected, Shots.Timecode(frame, new Fps(num, den)));

    [Fact]
    public void ShotsFromCutsAreContiguous() =>
        Assert.Equal([new(1, 0, 30), new(2, 30, 60), new(3, 60, 100)], Shots.FromCuts([60, 30, 30], 100));

    [Fact]
    public void NoCutsGivesOneShot() => Assert.Equal([new Shot(1, 0, 100)], Shots.FromCuts([], 100));

    [Fact]
    public void TableUsesOneBasedStartAndInclusiveEnd() =>
        Assert.Contains(" |      2  |          31 | 00:00:01.001 |          60 | 00:00:02.002 |",
            Shots.Table(Shots.FromCuts([30], 60), Ntsc));

    [Theory]
    [InlineData(1920, 1080, 256, 144)]
    [InlineData(640, 360, 256, 144)]
    [InlineData(720, 480, 256, 171)]
    [InlineData(200, 100, 200, 100)]
    public void DownscaleMatchesPySceneDetect(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), VideoReader.DownscaledSize(w, h));
}
