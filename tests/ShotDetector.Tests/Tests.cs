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

/// <summary>Frame positions as PySceneDetect's OpenCV backend reports them for constant frame rate video.</summary>
static class Positions
{
    public static Func<int, PyTime> Cfr(int fps) => i => i == 0
        ? PyTime.Pts(0, fps, new Fps(fps, 1)) // frame-number fallback, since POS_MSEC is 0
        : PyTime.Pts((long)Math.Round(i * 1_000_000.0 / fps), 1_000_000, new Fps(fps, 1));
}

public class DetectorTests
{
    static List<int> RunContent(double[] scores, int minSceneLen = 15, int fps = 25)
    {
        var d = new ContentDetector(new ContentScorer(), Positions.Cfr(fps), new Fps(fps, 1), 27, minSceneLen);
        return scores.Select((s, i) => d.ProcessScore(i, s)).OfType<int>().ToList();
    }

    static List<int> RunAdaptive(double[] scores, int minSceneLen = 15, Func<int, PyTime>? position = null)
    {
        var d = new AdaptiveDetector(new ContentScorer(), position ?? Positions.Cfr(25), minSceneLen: minSceneLen);
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
    public void AdaptiveMinLengthIsMeasuredInTimeForVariableFrameRate()
    {
        // 24 fps for frames 0-19, then 48 fps. A spike at 40 is evaluated at frame 42, which is 22
        // frames but only 0.458 s (round(0.458 * 24) = 11 frames) after the cut at 20, so it is skipped.
        var fps = new Fps(24, 1);
        Func<int, PyTime> vfr = i => PyTime.Pts(
            (long)Math.Round((i < 20 ? i / 24.0 : 20 / 24.0 + (i - 20) / 48.0) * 1_000_000), 1_000_000, fps);
        Assert.Equal([20], RunAdaptive(Scores(80, 1, (20, 40), (40, 40)), position: vfr));
        Assert.Equal([20, 40], RunAdaptive(Scores(80, 1, (20, 40), (40, 40)), position: Positions.Cfr(24)));
    }

    [Fact]
    public void DetectorsFindCutInSyntheticFrames()
    {
        byte[] black = new byte[8 * 8 * 3], white = Enumerable.Repeat((byte)255, 8 * 8 * 3).ToArray();
        var position = Positions.Cfr(25);
        foreach (IDetector d in new IDetector[]
                 {
                     new ContentDetector(new ContentScorer(), position, new Fps(25, 1)),
                     new AdaptiveDetector(new ContentScorer(), position),
                 })
        {
            var cuts = new List<long>();
            for (int i = 0; i < 60; i++)
                if (d.ProcessFrame(i, i < 30 ? black : white) is { } c) cuts.Add(c.FrameNum);
            Assert.Equal([30], cuts);
        }
    }
}

public class ThresholdDetectorTests
{
    // Mean pixel level per frame: bright, then dark for frames [from, to), then bright again.
    static double[] Fade(int count, int from, int to) =>
        Enumerable.Range(0, count).Select(i => i >= from && i < to ? 3.0 : 100.0).ToArray();

    static readonly Fps Fps25 = new(25, 1);

    static List<long> Run(double[] levels, double fadeBias = 0, int minSceneLen = 15)
    {
        var d = new ThresholdDetector(Positions.Cfr(25), Fps25, 12, minSceneLen, fadeBias);
        var cuts = levels.Select((v, i) => d.ProcessAverage(i, v)).OfType<PyTime>().Select(c => c.FrameNum).ToList();
        if (d.PostProcess(PyTime.Pts(levels.Length - 1, 25, Fps25)) is { } last) cuts.Add(last.FrameNum);
        return cuts;
    }

    [Theory]
    [InlineData(0.0, 25)]   // midway between fade-out (20) and fade-in (30)
    [InlineData(-1.0, 20)]
    [InlineData(1.0, 30)]
    [InlineData(0.1, 26)]   // 20 + round(10 * 1.1 / 2) = 20 + round(5.5) = 26 (half to even)
    public void CutIsPlacedByFadeBias(double bias, long expected) =>
        Assert.Equal([expected], Run(Fade(60, 20, 30), bias));

    [Fact]
    public void FadeInBeforeMinLengthIsIgnored() => Assert.Empty(Run(Fade(60, 5, 10)));

    [Fact]
    public void EndingFadedOutAddsFadeOutFrameAsCut() => Assert.Equal([40], Run(Fade(60, 40, 60)));

    [Fact]
    public void ThresholdIsTruncatedAndComparedStrictly()
    {
        var d = new ThresholdDetector(Positions.Cfr(25), Fps25, 12.9, minSceneLen: 0);
        d.ProcessAverage(0, 50);
        d.ProcessAverage(1, 12.0);              // not < 12, so no fade-out
        Assert.Null(d.ProcessAverage(2, 50));
        d.ProcessAverage(3, 11.99);             // fade-out
        Assert.Equal(3, d.ProcessAverage(4, 12.0)?.FrameNum); // >= 12 fades in; round(1 / 2) = 0 → cut at 3
    }

    [Fact]
    public void AverageIsMeanOfAllBytes() => Assert.Equal(2.5, ThresholdDetector.Average([0, 1, 2, 3, 4, 5]));
}

// Expected values from cv2.Canny / cv2.dilate / numpy.median / _estimated_kernel_size.
public class EdgeDetectorTests
{
    static string[] Rows(byte[] img, int w) =>
        img.Chunk(w).Select(r => string.Concat(r.Select(v => v == 255 ? '1' : '0'))).ToArray();

    static byte[] StepImage()
    {
        var img = new byte[6 * 8];
        for (int y = 0; y < 6; y++)
            for (int x = 4; x < 8; x++)
                img[y * 8 + x] = 200;
        img[0] = 90;          // (0, 0)
        img[5 * 8 + 1] = 120; // (1, 5)
        return img;
    }

    [Fact]
    public void CannyMatchesOpenCv()
    {
        var edges = new byte[6 * 8];
        new EdgeDetector(8, 6).Canny(StepImage(), 50, 100, edges);
        Assert.Equal(["10010000", "00010000", "00010000", "00010000", "01010000", "10010000"], Rows(edges, 8));
    }

    [Fact]
    public void DilateMatchesOpenCv()
    {
        var det = new EdgeDetector(8, 6, kernelSize: 3);
        byte[] canny = new byte[6 * 8], dilated = new byte[6 * 8];
        det.Canny(StepImage(), 50, 100, canny);
        det.Dilate(canny, dilated);
        Assert.Equal(["11111000", "11111000", "00111000", "11111000", "11111000", "11111000"], Rows(dilated, 8));
    }

    [Fact]
    public void MedianMatchesNumpy()
    {
        Assert.Equal(5.0, EdgeDetector.Median([5, 1, 9]));
        Assert.Equal(2.5, EdgeDetector.Median([1, 2, 3, 10]));
    }

    [Theory]
    [InlineData(256, 144, 5)]
    [InlineData(256, 109, 5)]
    [InlineData(1920, 1080, 13)]
    [InlineData(64, 36, 5)]
    public void KernelSizeFromResolution(int w, int h, int expected) =>
        Assert.Equal(expected, EdgeDetector.EstimatedKernelSize(w, h));
}

public class Yuv420Tests
{
    // Expected BGR from ffmpeg (scale=in_color_matrix=bt601:flags=bicubic,format=bgr24) on yuv420p.
    [Theory]
    [InlineData(16, 128, 128, 0, 0, 0)]
    [InlineData(235, 128, 128, 255, 255, 255)]
    [InlineData(0, 0, 0, 0, 135, 0)]
    [InlineData(255, 255, 255, 255, 124, 255)]
    [InlineData(81, 90, 240, 0, 0, 253)]
    [InlineData(145, 54, 34, 0, 254, 0)]
    [InlineData(41, 240, 110, 254, 0, 0)]
    [InlineData(128, 60, 200, 0, 97, 244)]
    public void MatchesSwscale(byte y, byte u, byte v, byte b, byte g, byte r)
    {
        byte[] yuv = [y, y, y, y, u, v]; // 2x2 frame: 4 luma, 1 U, 1 V
        var row = new byte[6];
        Yuv420.RowToBgr(yuv, 2, 2, 0, [0, 1], row);
        Assert.Equal([b, g, r, b, g, r], row);
    }

    [Fact]
    public void FusedYuvResizeEqualsConvertingEverythingFirst()
    {
        const int w = 37, h = 22, dw = 11, dh = 6; // odd width, chroma width 19
        var yuv = new byte[Yuv420.FrameSize(w, h)];
        new Random(7).NextBytes(yuv);
        var bgr = new byte[w * h * 3];
        int[] all = Enumerable.Range(0, w).ToArray();
        for (int y = 0; y < h; y++)
            Yuv420.RowToBgr(yuv, w, h, y, all, bgr.AsSpan(y * w * 3, w * 3));

        byte[] expected = new byte[dw * dh * 3], actual = new byte[dw * dh * 3];
        var resize = new CvResize(w, h, dw, dh);
        resize.Resize(bgr, expected);
        resize.ResizeYuv420(yuv, actual);
        Assert.Equal(expected, actual);
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
    [InlineData(24.0, 24, 1)]
    [InlineData(29.97002997002997, 30000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(6.993006993006993, 1000, 143)] // NTSC fraction, reduced
    [InlineData(36.36275695284159, 30072, 827)] // average rate of a variable frame rate clip
    [InlineData(23.5, 47, 2)]
    [InlineData(12.345678, 122679, 9937)]     // limit_denominator(10000)
    public void FpsFromFloatMatchesFramerateToFraction(double fps, int num, int den) =>
        Assert.Equal(new Fps(num, den), Fps.FromFloat(fps));

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
