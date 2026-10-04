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

public class ThresholdDetectorTests
{
    // Mean pixel level per frame: bright, then dark for frames [from, to), then bright again.
    static double[] Fade(int count, int from, int to) =>
        Enumerable.Range(0, count).Select(i => i >= from && i < to ? 3.0 : 100.0).ToArray();

    static List<int> Run(double[] levels, double fadeBias = 0, int minSceneLen = 15)
    {
        var d = new ThresholdDetector(12, minSceneLen, fadeBias);
        var cuts = levels.Select((v, i) => d.ProcessAverage(i, v)).OfType<int>().ToList();
        if (d.PostProcess(levels.Length - 1) is int last) cuts.Add(last);
        return cuts;
    }

    [Theory]
    [InlineData(0.0, 25)]   // midway between fade-out (20) and fade-in (30)
    [InlineData(-1.0, 20)]
    [InlineData(1.0, 30)]
    [InlineData(0.1, 26)]   // 20 + round(10 * 1.1 / 2) = 20 + round(5.5) = 26 (half to even)
    public void CutIsPlacedByFadeBias(double bias, int expected) =>
        Assert.Equal([expected], Run(Fade(60, 20, 30), bias));

    [Fact]
    public void FadeInBeforeMinLengthIsIgnored() => Assert.Empty(Run(Fade(60, 5, 10)));

    [Fact]
    public void EndingFadedOutAddsFadeOutFrameAsCut() => Assert.Equal([40], Run(Fade(60, 40, 60)));

    [Fact]
    public void ThresholdIsTruncatedAndComparedStrictly()
    {
        var d = new ThresholdDetector(12.9, minSceneLen: 0);
        d.ProcessAverage(0, 50);
        d.ProcessAverage(1, 12.0);              // not < 12, so no fade-out
        Assert.Null(d.ProcessAverage(2, 50));
        d.ProcessAverage(3, 11.99);             // fade-out
        Assert.Equal(3, d.ProcessAverage(4, 12.0)); // >= 12 fades in; round(1 / 2) = 0 → cut at 3
    }

    [Fact]
    public void AverageIsMeanOfAllBytes() => Assert.Equal(2.5, ThresholdDetector.Average([0, 1, 2, 3, 4, 5]));
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

// Expected values come from scenedetect's own FrameTimecode.
public class PyTimeTests
{
    static readonly Fps Ntsc = new(30000, 1001), Fps24 = new(24, 1);
    static PyTime Micros(long us, Fps fps) => PyTime.Pts(us, 1_000_000, fps);

    [Theory]
    [InlineData(0, 30000, 1001, "00:00:00.000")]
    [InlineData(90, 30000, 1001, "00:00:03.003")]
    [InlineData(1798, 30000, 1001, "00:00:59.993")]
    [InlineData(1499, 25, 1, "00:00:59.960")]
    [InlineData(90000, 25, 1, "01:00:00.000")]
    [InlineData(59999600, 1000000, 1, "00:01:00.000")] // 59.9996s rounds up into the next minute
    public void FrameNumberTimecode(long frame, int num, int den, string expected) =>
        Assert.Equal(expected, PyTime.Frame(frame, new Fps(num, den)).Timecode());

    [Theory]
    [InlineData(3503500, "00:00:03.503")] // 3.5035 is really 3.50349999..., so it rounds down
    [InlineData(4504500, "00:00:04.505")] // 4.5045 is really 4.50450000...1, so it rounds up
    public void HalfMillisecondsRoundOnTheExactBinaryValue(long us, string expected) =>
        Assert.Equal(expected, Micros(us, Ntsc).Timecode());

    [Theory]
    [InlineData(0.0625, 62)]  // exact tie → even
    [InlineData(0.1875, 188)] // exact tie → even
    [InlineData(3.5035, 3503)]
    public void RoundScaledMatchesPythonRound(double x, long expected) =>
        Assert.Equal(expected, PyTime.RoundScaled(x, 1000));

    [Fact]
    public void TimestampDifference()
    {
        var d = Micros(28000000, Fps24).Minus(Micros(27416667, Fps24));
        Assert.Equal((14L, "00:00:00.583", "0.583"), (d.FrameNum, d.Timecode(), d.SecondsText()));
    }

    [Fact]
    public void EndPlusOneFrameInFrameTimeBase()
    {
        var end = PyTime.Pts(1252, 24, Fps24).PlusFrames(1);
        Assert.Equal((1253L, "00:00:52.208"), (end.FrameNum, end.Timecode()));
        var d = end.Minus(Micros(52125000, Fps24)); // mixed time bases → the finer one (µs)
        Assert.Equal((83333L, 2L, "00:00:00.083"), (d.Value, d.FrameNum, d.Timecode()));
    }

    [Fact]
    public void MixedFrameNumberAndTimestamp()
    {
        var frame = PyTime.Frame(100, Ntsc);
        var ts = Micros(3003000, Ntsc);
        Assert.Equal((333667L, "00:00:00.334"), (frame.Minus(ts).Value, frame.Minus(ts).Timecode()));
        Assert.Equal(0L, ts.Minus(frame).Value); // clamped at 0
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.3862395109953702, "1.3862395109953702")]
    [InlineData(255.0, "255.0")]
    public void StatsFloatsPrintLikePythonRepr(double x, string expected) => Assert.Equal(expected, Stats.PyFloat(x));
}

public class ShotsTests
{
    static readonly Fps Ntsc = new(30000, 1001);

    static PyTime F(long n) => PyTime.Frame(n, Ntsc);

    [Fact]
    public void ShotsFromCutsAreContiguous() =>
        Assert.Equal([new(1, F(0), F(30)), new(2, F(30), F(60)), new(3, F(60), F(100))],
            Shots.FromCuts([F(60), F(30), F(30)], F(0), F(100)));

    [Fact]
    public void NoCutsGivesOneShot() => Assert.Equal([new Shot(1, F(0), F(100))], Shots.FromCuts([], F(0), F(100)));

    [Fact]
    public void TableUsesOneBasedStartAndInclusiveEnd() =>
        Assert.Contains(" |      2  |          31 | 00:00:01.001 |          60 | 00:00:02.002 |",
            Shots.Table(Shots.FromCuts([F(30)], F(0), F(60))));

    [Theory]
    [InlineData(1920, 1080, 256, 144)]
    [InlineData(640, 360, 256, 144)]
    [InlineData(720, 480, 256, 171)]
    [InlineData(200, 100, 200, 100)]
    public void DownscaleMatchesPySceneDetect(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), VideoReader.DownscaledSize(w, h));
}
