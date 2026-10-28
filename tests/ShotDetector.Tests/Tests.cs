using ShotDetector;
using ShotDetector.FastYuv;

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

public class DecodeThreadsTests
{
    [Theory]
    [InlineData(1920, 1080, 256, 144, true)]   // 7% of the pixels
    [InlineData(1280, 534, 256, 107, true)]    // 16%
    [InlineData(640, 360, 256, 144, false)]    // 64%: whole frames
    [InlineData(426, 240, 213, 120, false)]    // all of them
    public void SamplingOnlyWhenItReadsUnderAQuarter(int sw, int sh, int w, int h, bool sampled) =>
        Assert.Equal(sampled, VideoReader.SampledPays(sw, sh, w, h));

    // OpenCV's decoder threads (cap_ffmpeg's fill_codec_context): every CPU, at most 16; what damaged files need.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    [InlineData(64, 16)]
    public void DefaultIsOpenCvs(int cpus, int threads) =>
        Assert.Equal(threads, VideoReader.DefaultDecodeThreads(cpus));
}

public class ContentScorerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(4097)]
    [InlineData(36864)]   // 256 x 144, past 128 rounds of 32 bytes (the 16-bit lanes are flushed)
    public void AbsDiffSumMatchesTheScalarSum(int length)
    {
        var rnd = new Random(length);
        var (a, b) = (new byte[length], new byte[length]);
        rnd.NextBytes(a);
        rnd.NextBytes(b);
        a.AsSpan(0, length / 2).Fill(255);
        b.AsSpan(0, length / 2).Fill(0); // worst case: 255 per byte
        long expected = 0;
        for (int i = 0; i < length; i++)
            expected += Math.Abs(a[i] - b[i]);
        Assert.Equal(expected, ContentScorer.AbsDiffSum(a, b));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(1000)]
    [InlineData(70000)] // more than 128 rounds of 16: the 16-bit lanes are flushed
    public void AbsDiffSum128MatchesTheScalarSum(int length)
    {
        // The 128-bit path ARM takes, run here directly: the bytes it covers sum as the plain loop does.
        var rnd = new Random(length);
        var (a, b) = (new byte[length], new byte[length]);
        rnd.NextBytes(a);
        rnd.NextBytes(b);
        var (sum, done) = ContentScorer.AbsDiffSum128(a, b);
        Assert.Equal(length - length % 16, done);
        Assert.Equal(Enumerable.Range(0, done).Sum(i => (long)Math.Abs(a[i] - b[i])), sum);
    }

    [Fact]
    public void VResize128MatchesTheScalarFormula()
    {
        // The vertical pass ARM takes: row values as the horizontal pass makes them (up to 255 * 2048).
        var rnd = new Random(3);
        int n = 768;
        var (r0, r1, o) = (new int[n], new int[n], new byte[n]);
        for (int k = 0; k < n; k++)
            (r0[k], r1[k]) = (rnd.Next(255 * 2048 + 1), rnd.Next(255 * 2048 + 1));
        foreach (var (b0, b1) in new[] { (2048, 0), (1024, 1024), (100, 1948), (0, 2048) })
        {
            int done = CvResize.VResize128(ref r0[0], ref r1[0], ref o[0], n, b0, b1);
            Assert.Equal(n, done);
            for (int x = 0; x < n; x++)
                Assert.Equal((byte)Math.Clamp((((b0 * (r0[x] >> 4)) >> 16) + ((b1 * (r1[x] >> 4)) >> 16) + 2) >> 2, 0, 255), o[x]);
        }
    }

    [Fact]
    public void HsvPlanesMatchThePerPixelConversionForEveryColour()
    {
        // All 2^24 BGR values, a blue value at a time, through the planar (vectorised) conversion.
        var bgr = new byte[3 * 65536];
        var (h, s, v) = (new byte[65536], new byte[65536], new byte[65536]);
        for (int b = 0; b < 256; b++)
        {
            for (int k = 0; k < 65536; k++)
                (bgr[3 * k], bgr[3 * k + 1], bgr[3 * k + 2]) = ((byte)b, (byte)(k >> 8), (byte)k);
            Hsv.Convert(bgr, h, s, v);
            for (int k = 0; k < 65536; k++)
            {
                if ((h[k], s[k], v[k]) != Hsv.FromBgr((byte)b, (byte)(k >> 8), (byte)k))
                    Assert.Fail($"BGR {b},{k >> 8},{k & 255}: {(h[k], s[k], v[k])} != {Hsv.FromBgr((byte)b, (byte)(k >> 8), (byte)k)}");
            }
        }
    }

    [Fact]
    public void HsvPlanesMatchThePerPixelConversion()
    {
        var rnd = new Random(7);
        var bgr = new byte[3 * 5000];
        rnd.NextBytes(bgr);
        var (h, s, v) = (new byte[5000], new byte[5000], new byte[5000]);
        Hsv.Convert(bgr, h, s, v);
        for (int i = 0; i < 5000; i++)
            Assert.Equal(Hsv.FromBgr(bgr[3 * i], bgr[3 * i + 1], bgr[3 * i + 2]), (h[i], s[i], v[i]));
    }

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
    public static Func<int, FrameTime> Cfr(int fps) => i => i == 0
        ? FrameTime.Pts(0, fps, new Fps(fps, 1)) // frame-number fallback, since POS_MSEC is 0
        : FrameTime.Pts((long)Math.Round(i * 1_000_000.0 / fps), 1_000_000, new Fps(fps, 1));
}

public class DetectorTests
{
    static List<int> RunContent(double[] scores, int minSceneLen = 15, int fps = 25, bool frameCounts = false)
    {
        var d = new ContentDetector(new ContentScorer(), Positions.Cfr(fps), new Fps(fps, 1), 27, minSceneLen, frameCounts: frameCounts);
        return scores.Select((s, i) => d.ProcessScore(i, s)).OfType<int>().ToList();
    }

    static List<int> RunAdaptive(double[] scores, int minSceneLen = 15, Func<int, FrameTime>? position = null, bool fromTarget = false)
    {
        var d = new AdaptiveDetector(new ContentScorer(), position ?? Positions.Cfr(25), minSceneLen: minSceneLen, fromTarget: fromTarget);
        return scores.Select((s, i) => d.ProcessScore(i, s)).OfType<int>().ToList();
    }

    static double[] Scores(int count, double baseline, params (int Frame, double Score)[] spikes)
    {
        var a = Enumerable.Repeat(baseline, count).ToArray();
        a[0] = 0; // first frame always scores 0
        foreach (var (f, s) in spikes)
            a[f] = s;
        return a;
    }

    [Fact]
    public void ContentCutsAboveThreshold() => Assert.Equal([20, 50], RunContent(Scores(80, 2, (20, 40), (50, 27))));

    [Fact]
    public void ContentIgnoresCutTooCloseToStart() => Assert.Empty(RunContent(Scores(80, 2, (10, 40))));

    [Fact]
    public void ContentSuppressFilterDropsCutsUntilTheMinimumLengthPassed()
    {
        // Above the threshold at 2, 4 (too soon after 2) and 30: suppress keeps 2 and 30, not a merged cut.
        var fps = new Fps(25, 1);
        var d = new ContentDetector(ContentScorer.LumaOnly(), i => FrameTime.Frame(i, fps), fps, 27, minSceneLen: 15, suppress: true);
        var cuts = Enumerable.Range(0, 40).Select(i => d.ProcessScore(i, i is 2 or 4 or 30 ? 50 : 0)).OfType<int>().ToList();
        Assert.Equal([30], cuts); // 2 is within 15 frames of the start (frame 0), 4 within 15 of 0, 30 is 30 after
    }

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
    public void ContentMinLengthInFramesComparesFrameCountsFrom072()
    {
        // scenedetect 0.7.2 (#577): the gap's frame count against min_scene_len, so 672 - 658 = 14 frames counts.
        Assert.Equal([658, 672], RunContent(Scores(700, 2, (658, 40), (672, 40)), minSceneLen: 14, fps: 24, frameCounts: true));
        Assert.Equal([658], RunContent(Scores(700, 2, (658, 40), (671, 40)), minSceneLen: 14, fps: 24, frameCounts: true));
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
    public void AdaptiveMinLengthIsMeasuredFromTheTargetFrameFrom072()
    {
        // scenedetect 0.7.2 (#408): from the frame it would cut at, so a spike needs 15 frames before it itself.
        Assert.Empty(RunAdaptive(Scores(40, 1, (13, 40)), fromTarget: true));
        Assert.Equal([15], RunAdaptive(Scores(40, 1, (15, 40)), fromTarget: true));
        Assert.Equal([20], RunAdaptive(Scores(60, 1, (20, 40), (33, 40)), fromTarget: true));
        Assert.Equal([20, 35], RunAdaptive(Scores(60, 1, (20, 40), (35, 40)), fromTarget: true));
    }

    [Fact]
    public void AdaptiveMinLengthIsMeasuredInTimeForVariableFrameRate()
    {
        // 24 fps for frames 0-19, then 48 fps. A spike at 40 is evaluated at frame 42, which is 22
        // frames but only 0.458 s (round(0.458 * 24) = 11 frames) after the cut at 20, so it is skipped.
        var fps = new Fps(24, 1);
        Func<int, FrameTime> vfr = i => FrameTime.Pts(
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
            {
                if (d.ProcessFrame(i, i < 30 ? black : white) is { } c)
                    cuts.Add(c.FrameNum);
            }

            Assert.Equal([30], cuts);
        }
    }
}

public class ThresholdDetectorTests
{
    [Fact]
    public void FadeOutsShorterThanMinOutLengthMakeNoCutFrom072()
    {
        // scenedetect 0.7.2's min_out_length: dark for 2 frames (20-21) is too short for 3; 3 frames (20-22) cut.
        static List<long> Cuts(double[] levels, int minOut)
        {
            var d = new ThresholdDetector(Positions.Cfr(25), Fps25, 12, minSceneLen: 0, minOutLength: minOut);
            var cuts = levels.Select((v, i) => d.ProcessAverage(i, v)).OfType<FrameTime>().Select(c => c.FrameNum).ToList();
            if (d.PostProcess(FrameTime.Pts(levels.Length - 1, 25, Fps25)) is { } last)
                cuts.Add(last.FrameNum);
            return cuts;
        }

        Assert.Empty(Cuts(Fade(60, 20, 22), 3));
        Assert.Equal([22], Cuts(Fade(60, 20, 23), 3)); // midway: 20 + round(3 / 2) = 22

        // Ending faded out: the last frame counts, so 57-59 (3 frames) is long enough and 58-59 isn't.
        Assert.Equal([57], Cuts(Fade(60, 57, 60), 3));
        Assert.Empty(Cuts(Fade(60, 58, 60), 3));
    }

    [Theory]
    [InlineData(null, true, 25, 2)]   // the CLI's 0.1 s: round(2.5) = 2, half to even
    [InlineData(null, true, 30, 3)]
    [InlineData(null, false, 25, 0)]  // 0.7.1 had none
    [InlineData("5", true, 25, 5)]
    [InlineData("0.2s", false, 25, 5)]
    public void MinOutLengthDefaultsToTheClisFrom072(string? value, bool v072, int fps, int expected) =>
        Assert.Equal(expected, ShotDetection.MinOutLengthFrames(value, v072, new Fps(fps, 1)));

    // Mean pixel level per frame: bright, then dark for frames [from, to), then bright again.
    static double[] Fade(int count, int from, int to) =>
        Enumerable.Range(0, count).Select(i => i >= from && i < to ? 3.0 : 100.0).ToArray();

    static readonly Fps Fps25 = new(25, 1);

    static List<long> Run(double[] levels, double fadeBias = 0, int minSceneLen = 15)
    {
        var d = new ThresholdDetector(Positions.Cfr(25), Fps25, 12, minSceneLen, fadeBias);
        var cuts = levels.Select((v, i) => d.ProcessAverage(i, v)).OfType<FrameTime>().Select(c => c.FrameNum).ToList();
        if (d.PostProcess(FrameTime.Pts(levels.Length - 1, 25, Fps25)) is { } last)
            cuts.Add(last.FrameNum);
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

    [Fact]
    public void RepeatedPositionReusesTheStoredAverageWithStats()
    {
        // Frames 0-1 and 2-3 share positions (timestamps starting over); scenedetect reuses the stored average.
        Func<int, FrameTime> position = i => Positions.Cfr(25)(i % 2);
        var d = new ThresholdDetector(position, Fps25, 12, minSceneLen: 0) { Stats = new Stats() };
        d.ProcessFrame(0, [100, 100]);
        d.ProcessFrame(1, [100, 100]);
        d.ProcessFrame(2, [0, 0]);             // dark, but position 0 already holds 100: no fade-out
        Assert.Null(d.ProcessFrame(3, [100, 100]));
        Assert.Equal("Frame Number,Timecode,average_rgb\n1,00:00:00.000,100.0\n2,00:00:00.040,100.0\n", d.Stats.Csv(position));

        var plain = new ThresholdDetector(position, Fps25, 12, minSceneLen: 0);
        plain.ProcessFrame(0, [100, 100]);
        plain.ProcessFrame(1, [100, 100]);
        plain.ProcessFrame(2, [0, 0]);         // without stats every frame's own average counts
        Assert.NotNull(plain.ProcessFrame(3, [100, 100]));
    }

    [Fact]
    public void StatsAtTheSamePositionMergeKeyByKey()
    {
        Func<int, FrameTime> position = i => Positions.Cfr(25)(i % 2);
        var stats = new Stats();
        stats.Set(0, "a", 1);
        stats.Set(0, "b", 2);
        stats.Set(2, "a", 3);                  // later frame at position 0: overwrites a, keeps b
        stats.Set(1, "a", 4);
        Assert.Equal("Frame Number,Timecode,a,b\n1,00:00:00.000,3.0,2.0\n2,00:00:00.040,4.0,None\n", StatsTests.Csv(stats, position));
    }
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
        {
            for (int x = 4; x < 8; x++)
                img[y * 8 + x] = 200;
        }

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
        new SwscaleYuv420().RowToBgr(yuv, 2, 2, 0, [0, 1], row);
        Assert.Equal([b, g, r, b, g, r], row);
    }

    // Expected BGR from ffmpeg with in_color_matrix / in_range set accordingly.
    [Theory]
    [InlineData("bt709", false, 81, 90, 240, 0, 23, 255)]
    [InlineData("bt709", false, 200, 100, 150, 154, 207, 253)]
    [InlineData("smpte170m", true, 81, 90, 240, 13, 14, 238)]
    [InlineData("unknown", true, 200, 100, 150, 150, 193, 230)]
    public void FollowsColourMatrixAndRange(string colorSpace, bool fullRange, byte y, byte u, byte v, byte b, byte g, byte r)
    {
        var row = new byte[6];
        new SwscaleYuv420().ForColor(colorSpace, fullRange)!.RowToBgr([y, y, y, y, u, v], 2, 2, 0, [0, 1], row);
        Assert.Equal([b, g, r, b, g, r], row);
    }

    [Fact]
    public void UnsupportedColourMatrixFallsBack() => Assert.Null(new SwscaleYuv420().ForColor("ycgco", false));

    [Fact]
    public void FusedYuvResizeEqualsConvertingEverythingFirst()
    {
        const int w = 37, h = 22, dw = 11, dh = 6; // odd width, chroma width 19
        var yuv = new byte[IYuv420Converter.FrameSize(w, h)];
        new Random(7).NextBytes(yuv);
        var bgr = new byte[w * h * 3];
        int[] all = Enumerable.Range(0, w).ToArray();
        for (int y = 0; y < h; y++)
            new SwscaleYuv420().RowToBgr(yuv, w, h, y, all, bgr.AsSpan(y * w * 3, w * 3));

        byte[] expected = new byte[dw * dh * 3], actual = new byte[dw * dh * 3];
        var resize = new CvResize(w, h, dw, dh);
        resize.Resize(bgr, expected);
        resize.ResizeYuv420(yuv, actual, new SwscaleYuv420());
        Assert.Equal(expected, actual);
    }
}

// Expected values from scenedetect's _generate_timecode_list / FrameTimecode.
public class ExportTests
{
    [Theory]
    [InlineData(false, false, null, null, null, "-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset veryfast -crf 22 -c:a aac")]
    [InlineData(false, true, null, null, null, "-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset slow -crf 17 -c:a aac")]
    [InlineData(false, true, 30, null, null, "-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset slow -crf 30 -c:a aac")]
    [InlineData(false, false, null, "fast", null, "-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset fast -crf 22 -c:a aac")]
    [InlineData(false, false, 30, null, "-c:v libx265", "-c:v libx265")]              // args win over crf/preset
    [InlineData(true, false, null, null, "-c:v libx265", "-map 0:v:0 -map 0:a? -map 0:s? -c:v copy -c:a copy")] // copy wins over args
    [InlineData(false, false, null, null, "-vf \\\"scale=640:-2\\\"", "-vf \"scale=640:-2\"")] // \" becomes "
    public void SplitArgumentsAreScenedetects(bool copy, bool hq, int? crf, string? preset, string? args, string expected) =>
        Assert.Equal(expected, string.Join(' ', new SplitOptions { Copy = copy, HighQuality = hq, RateFactor = crf, Preset = preset, Args = args }.FfmpegArgs()));

    [Fact]
    public void CopyRefusesEncodingSettings() =>
        Assert.Throws<ArgumentException>(() => new SplitOptions { Copy = true, RateFactor = 20 }.FfmpegArgs());

    static readonly Fps Fps25 = new(25, 1);

    static FrameTime F(long n) => FrameTime.Frame(n, Fps25);

    [Fact]
    public void ImageTimesMatchScenedetect()
    {
        Shot[] shots = [new(1, F(0), F(100)), new(2, F(100), F(101)), new(3, F(101), F(101))];
        var times = Export.ImageTimes(shots, Fps25, 3, 1);
        Assert.Equal([0.04, 2.0, 3.96], times[0]);                                 // 1-frame margins at both ends
        Assert.Equal([4.013333333333334, 4.02, 4.026666666666666], times[1]);      // margin capped by the segment
        Assert.Equal([4.04, 4.04, 4.04], times[2]);                                // empty shot: its start
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

    // The in-process pipeline hands the source rows over packed (ResizePackedRows on the caller's thread): the same bytes
    // as resizing from the frame (ResizeRows, with a crop's offset) and from the whole image (Resize). Sizes for
    // downscaling by less and more than 2, upscaling (rows reused), and widths whose last pixels take the scalar tail.
    [Theory]
    [InlineData(426, 240, 256, 144)]
    [InlineData(1280, 534, 256, 107)]
    [InlineData(200, 100, 256, 144)]
    [InlineData(37, 22, 11, 6)]
    [InlineData(5, 3, 2, 2)]
    [InlineData(3, 2, 7, 5)]
    public void PackedRowsResizeAsTheFrame(int w, int h, int dw, int dh)
    {
        const int cropX = 3, cropY = 2;
        int fw = w + cropX + 4, fh = h + cropY + 1, stride = fw * 3 + 5;
        var frame = new byte[stride * fh];
        new Random(w * 31 + h).NextBytes(frame);
        var resize = new CvResize(w, h, dw, dh);
        var image = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            frame.AsSpan((cropY + y) * stride + cropX * 3, w * 3).CopyTo(image.AsSpan(y * w * 3));
        var packed = new byte[resize.SourceRows.Length * w * 3];
        for (int i = 0; i < resize.SourceRows.Length; i++)
            frame.AsSpan((cropY + resize.SourceRows[i]) * stride + cropX * 3, w * 3).CopyTo(packed.AsSpan(i * w * 3));

        byte[] expected = new byte[dw * dh * 3], fromFrame = new byte[dw * dh * 3], fromPacked = new byte[dw * dh * 3];
        resize.Resize(image, expected);
        resize.ResizeRows(frame, stride, cropX, cropY, fromFrame);
        resize.ResizePackedRows(packed, fromPacked);
        Assert.Equal(expected, fromFrame);
        Assert.Equal(expected, fromPacked);
    }
}

// Expected values come from scenedetect's own FrameTimecode.
public class FrameTimeTests
{
    static readonly Fps Ntsc = new(30000, 1001), Fps24 = new(24, 1);

    static FrameTime Micros(long us, Fps fps) => FrameTime.Pts(us, 1_000_000, fps);

    [Theory]
    [InlineData(0, 30000, 1001, "00:00:00.000")]
    [InlineData(90, 30000, 1001, "00:00:03.003")]
    [InlineData(1798, 30000, 1001, "00:00:59.993")]
    [InlineData(1499, 25, 1, "00:00:59.960")]
    [InlineData(90000, 25, 1, "01:00:00.000")]
    [InlineData(59999600, 1000000, 1, "00:01:00.000")] // 59.9996s rounds up into the next minute
    public void FrameNumberTimecode(long frame, int num, int den, string expected) =>
        Assert.Equal(expected, FrameTime.Frame(frame, new Fps(num, den)).Timecode());

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
        Assert.Equal(expected, FrameTime.RoundScaled(x, 1000));

    [Fact]
    public void TimestampDifference()
    {
        var d = Micros(28000000, Fps24).Minus(Micros(27416667, Fps24));
        Assert.Equal((14L, "00:00:00.583", "0.583"), (d.FrameNum, d.Timecode(), d.SecondsText()));
    }

    [Fact]
    public void EndPlusOneFrameInFrameTimeBase()
    {
        var end = FrameTime.Pts(1252, 24, Fps24).PlusFrames(1);
        Assert.Equal((1253L, "00:00:52.208"), (end.FrameNum, end.Timecode()));
        var d = end.Minus(Micros(52125000, Fps24)); // mixed time bases → the finer one (µs)
        Assert.Equal((83333L, 2L, "00:00:00.083"), (d.Value, d.FrameNum, d.Timecode()));
    }

    [Fact]
    public void MixedFrameNumberAndTimestamp()
    {
        var frame = FrameTime.Frame(100, Ntsc);
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

    [Fact]
    public void FpsAndFrameReadersRationalConvert()
    {
        Fps fps = new FrameReader.Rational(30000, 1001);
        FrameReader.Rational rate = fps;
        Assert.Equal(new Fps(30000, 1001), fps);
        Assert.Equal(new FrameReader.Rational(30000, 1001), rate);
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.3862395109953702, "1.3862395109953702")]
    [InlineData(255.0, "255.0")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(-1.5e16, "-1.5e+16")]
    public void StatsFloatsPrintLikePythonRepr(double x, string expected) => Assert.Equal(expected, Stats.PyFloat(x));
}

public class SceneListTests
{
    static readonly Fps Fps25 = new(25, 1);

    static FrameTime F(long frame) => FrameTime.Frame(frame, Fps25);

    static FrameTime P(long micros) => FrameTime.Pts(micros, 1_000_000, Fps25);

    static (long, long)[] Spans(List<Shot> shots) => [.. shots.Select(s => (s.Start.FrameNum, s.End.FrameNum))];

    [Fact]
    public void CutsFromSeveralDetectorsAreSortedAndUnique()
    {
        // A frame-number cut and a timestamp at the same frame are the same cut (the first one is kept);
        // two timestamps are compared exactly.
        var cuts = ShotDetection.Unique([P(4_000_000), F(50), P(2_000_000), F(100), P(2_000_000)]);
        Assert.Equal([50L, 100L], cuts.Select(c => c.FrameNum));
        Assert.True(cuts[0].TbDen == 0 && cuts[1].TbDen > 0); // whichever came first: F(50) before P(2s), P(4s) before F(100)

        // Two different timestamps that round to the same frame number (VFR) are two cuts, as in scenedetect.
        Assert.Equal(2, ShotDetection.Unique([P(2_000_000), P(2_010_000)]).Count);
    }

    [Fact]
    public void SecondsPrintTheirNearestFrameButKeepTheirValue()
    {
        // FrameTimecode("5s") at 29.97 fps: timecode of frame 150 (5.005), seconds 5.000.
        var ntsc = new Fps(30000, 1001);
        var t = FrameTime.FromSeconds(5.0, ntsc);
        Assert.Equal((150L, "00:00:05.005", "5.000"), (t.FrameNum, t.Timecode(), t.SecondsText()));

        // Seconds minus a frame number stay seconds; a frame number minus seconds counts frames.
        Assert.Equal(5.0 - 1.001, t.Minus(FrameTime.Frame(30, ntsc)).Seconds); // frame 30 is exactly 30030/30000 s; 3.999 as in scenedetect
        Assert.Equal(30L, FrameTime.Frame(180, ntsc).Minus(t).FrameNum);
        Assert.Equal(5.0 + 2 / ntsc.Value, t.PlusFrames(2).Seconds, 12);
    }

    [Fact]
    public void ScenesMergeTheLastShortShotAndDropShortOnes()
    {
        List<FrameTime> cuts = [F(50), F(60), F(140)];
        Assert.Equal([(0L, 50L), (50L, 60L), (60L, 140L), (140L, 150L)], Spans(ShotDetection.Scenes(cuts, F(0), F(150), false, 0, 15)));
        Assert.Equal([(0L, 50L), (50L, 60L), (60L, 150L)], Spans(ShotDetection.Scenes(cuts, F(0), F(150), true, 0, 15)));
        Assert.Equal([(0L, 50L), (60L, 140L)], Spans(ShotDetection.Scenes(cuts, F(0), F(150), false, 15, 15)));
        Assert.Equal([1, 2], ShotDetection.Scenes(cuts, F(0), F(150), false, 15, 15).Select(s => s.Number));
    }
}

public class UrlOptionTests
{
    // An input reads only the protocols it needs: a file as a file (FFmpeg's pseudo-protocol strings included, so they
    // fail), an http(s) or rtsp URL its own family, another network URL as before.
    [Theory]
    [InlineData("http://example.com/play.m3u8", "-protocol_whitelist http,https,tcp,tls,crypto,httpproxy -rw_timeout 30000000")]
    [InlineData("HTTPS://example.com/a.mp4", "-protocol_whitelist http,https,tcp,tls,crypto,httpproxy -rw_timeout 30000000 -tls_verify 1")]
    [InlineData("rtsp://127.0.0.1:8554/shots", "-protocol_whitelist rtsp,rtsps,rtp,srtp,udp,tcp,tls")]
    [InlineData(@"C:\videos\a.mp4", "-protocol_whitelist file,crypto")]
    [InlineData(@"\\server\share\a.mp4", "-protocol_whitelist file,crypto")]
    [InlineData("/videos/a.mp4", "-protocol_whitelist file,crypto")]
    [InlineData("frames/%04d.png", "-protocol_whitelist file,crypto")]
    [InlineData("concat:a.mp4|b.mp4", "-protocol_whitelist file,crypto")]
    [InlineData("subfile,,start,0,end,100,,:a.mp4", "-protocol_whitelist file,crypto")]
    [InlineData("srt://host:9000", "")]
    [InlineData("udp://239.0.0.1:1234", "")]
    public void InputsReadOnlyTheProtocolsTheyNeed(string path, string expected) =>
        Assert.Equal(expected, string.Join(' ', VideoReader.InputOptions(path)));

    // ffprobe's timestamp pass (MPEG-PS without pts) reads with the input's options and the frame size cap like every
    // other read, and takes the path with -i, so a file named like an option stays a file.
    [Fact]
    public void TheTimestampPassReadsAsTheOthersDo() =>
        Assert.Equal("-v error -protocol_whitelist file,crypto -max_pixels 67108864 -select_streams v:0 -show_entries frame=pts,pkt_dts -of default=nw=1 -i -report",
            string.Join(' ', VideoReader.FrameTimestampsArgs("-report", VideoReader.InputOptions("-report"))));

    // MPEG-1/2 and MJPEG decode with the C IDCT, which x86's default matches and ARM's doesn't (ci.yml's arch-diff).
    [Theory]
    [InlineData("mpeg1video", "-idct simple")]
    [InlineData("mpeg2video", "-idct simple")]
    [InlineData("mjpeg", "-idct simple")]
    [InlineData("h264", "")]
    [InlineData("mpeg4", "")]
    public void MpegAndMjpegDecodeWithTheCIdct(string codec, string expected) =>
        Assert.Equal(expected, string.Join(' ', VideoReader.IdctOptions(codec)));

    // DetectionResult.Warnings: an http or https HLS playlist through the executable, whose https segments go unchecked,
    // and nothing else (a local playlist can't reach https: its whitelist is file,crypto).
    [Theory]
    [InlineData("https://example.com/play.m3u8", "hls", false, true)]
    [InlineData("HTTPS://example.com/play.m3u8", "hls", false, true)]
    [InlineData("https://example.com/play.m3u8", "hls", true, false)]
    [InlineData("http://example.com/play.m3u8", "hls", false, true)]
    [InlineData("HTTP://example.com/play.m3u8", "hls", false, true)]
    [InlineData("http://example.com/play.m3u8", "hls", true, false)]
    [InlineData("http://example.com/a.mp4", "mov,mp4,m4a,3gp,3g2,mj2", false, false)]
    [InlineData("file:///videos/play.m3u8", "hls", false, false)]
    [InlineData("rtsp://example.com/play.m3u8", "hls", false, false)]
    [InlineData("https://example.com/a.mp4", "mov,mp4,m4a,3gp,3g2,mj2", false, false)]
    [InlineData("/videos/play.m3u8", "hls", false, false)]
    [InlineData(@"C:\videos\play.m3u8", "hls", false, false)]
    [InlineData(null, "hls", false, false)]
    public void AnHttpOrHttpsPlaylistThroughTheExecutableWarns(string? path, string container, bool inProcess, bool warns)
    {
        var warning = VideoReader.ExecutableHlsWarning(path, container, inProcess);
        if (warns)
            Assert.StartsWith("decoded by ffmpeg, which fetches any https segments, keys and variants without checking", warning);
        else
            Assert.Null(warning);
    }

    // An image sequence is what FFmpeg's image2 reads as one (av_filename_number_test, checked against ffprobe 8.1): one
    // number, '%' then digits then 'd'; "%%" is a literal percent; any other '%' (a URL-encoded name) makes a plain file.
    [Theory]
    [InlineData("frames/%04d.png", true)]
    [InlineData("x%d.png", true)]
    [InlineData("x%5d.png", true)]
    [InlineData("100%%_%03d.png", true)]
    [InlineData("x%%03d.png", false)]
    [InlineData("x%d%d.png", false)]
    [InlineData("x%3%d.png", false)]
    [InlineData("My%20Video.mp4", false)]
    [InlineData("My%20Video_%03d.png", false)]
    [InlineData("a%b.mp4", false)]
    [InlineData("a%", false)]
    [InlineData("a%3", false)]
    [InlineData("frames.mp4", false)]
    public void ImageSequencePatternsAreImage2s(string path, bool pattern) =>
        Assert.Equal(pattern, VideoReader.IsImageSequencePattern(path));

    // The executable's raw-YUV pipe labels frames with the first frame's colour tags, in names setparams takes: an unknown
    // matrix or range is "unknown", and a yuvj format is full range whatever its tag says.
    [Theory]
    [InlineData("yuv420p", "bt709", "tv", "setparams=colorspace=bt709:range=tv")]
    [InlineData("yuv420p", "bt2020nc", "pc", "setparams=colorspace=bt2020nc:range=pc")]
    [InlineData("yuv420p", "reserved", "unknown", "setparams=colorspace=unknown:range=unknown")]
    [InlineData("yuv420p", null, null, "setparams=colorspace=unknown:range=unknown")]
    [InlineData("yuv420p", "smpte170m", "mpeg", "setparams=colorspace=smpte170m:range=unknown")]
    [InlineData("yuvj420p", "bt470bg", "tv", "setparams=colorspace=bt470bg:range=pc")]
    [InlineData("yuvj422p", "bt709", null, "setparams=colorspace=bt709:range=pc")]
    public void TheYuvPipesLabelsAreTheFirstFramesTags(string pixelFormat, string? colorSpace, string? colorRange, string labels) =>
        Assert.Equal(labels, VideoReader.YuvLabels(pixelFormat, colorSpace, colorRange));

    // ffmpeg and ffprobe are started by full path (next to the app, else from PATH), never by a bare name, which would
    // also find one in the current folder.
    [Fact]
    public void ExecutablesStartByFullPath()
    {
        Assert.True(Path.IsPathFullyQualified(VideoReader.Executable(null, "ffmpeg")));
        Assert.True(Path.IsPathFullyQualified(VideoReader.Executable(null, "no-such-tool")));
    }

    // A spilled stream goes in a folder of its own, only the user's on Unix, deleted with it.
    [Fact]
    public void TempFilesAreTheUsersOnly()
    {
        string dir;
        using (var file = new TempFile("spill.mp4"))
        {
            File.WriteAllText(file.Path, "x");
            dir = Path.GetDirectoryName(file.Path)!;
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }

        Assert.False(Directory.Exists(dir));
    }
}

public class TimeSpanOptionTests
{
    // A TimeSpan becomes seconds exactly, to the tick; the detectors' own settings too.
    [Theory]
    [InlineData(125_000_000L, "12.5s")]
    [InlineData(1L, "0.0000001s")]
    [InlineData(0L, "0s")]
    public void WrittenAsExactSeconds(long ticks, string expected)
    {
        Assert.Equal(expected, DetectionOptions.Either(null, null, TimeSpan.FromTicks(ticks), "Text", "Time"));
        var o = new DetectionOptions { Detectors = [new(DetectorKind.Content) { MinOutDuration = TimeSpan.FromTicks(ticks) }] }.Resolved();
        Assert.Equal(expected, o.Detectors![0].MinOutLength);
    }
}

public class TemplateTests
{
    [Theory]
    [InlineData("$VIDEO_NAME-Scene-$SCENE_NUMBER", "clip-Scene-007")]
    [InlineData("${VIDEO_NAME}x-$VIDEO_NAMEx", "clipx-$VIDEO_NAMEx")]   // an identifier runs as far as it can
    [InlineData("$$VIDEO_NAME $5 $ end$", "$VIDEO_NAME $5 $ end$")]      // "$$" is "$"; stray '$' stays
    [InlineData("$UNKNOWN/${SCENE_NUMBER}", "$UNKNOWN/007")]
    public void TemplatesSubstituteLikePythonsSafeSubstitute(string template, string expected) =>
        Assert.Equal(expected, Export.Substitute(template, new Dictionary<string, string> { ["VIDEO_NAME"] = "clip", ["SCENE_NUMBER"] = "007" }));

    // save-images --quality/--compression: the defaults are scenedetect's (JPEG 95 as -q:v 2, PNG 3, WebP 100). WebP names
    // libwebp: ffmpeg's default for .webp where it's built, libwebp_anim, wrote one animation for a run's several images.
    [Theory]
    [InlineData("jpg", null, null, "-q:v 2")]
    [InlineData("jpg", 100, null, "-q:v 2")]
    [InlineData("jpg", 0, null, "-q:v 31")]
    [InlineData("jpg", 50, null, "-q:v 16")]
    [InlineData("png", null, null, "-compression_level 3")]
    [InlineData("png", null, 9, "-compression_level 9")]
    [InlineData("webp", null, null, "-c:v libwebp -quality 100")]
    [InlineData("webp", 80, null, "-c:v libwebp -quality 80")]
    public void ImageQualityAsFfmpegsOptions(string format, int? quality, int? compression, string expected) =>
        Assert.Equal(expected, string.Join(' ', new ImageOptions { Format = format, Quality = quality, Compression = compression }.EncoderOptions()));

    [Fact]
    public void ImageQualityOutOfRangeThrows()
    {
        Assert.Throws<ArgumentException>(() => new ImageOptions { Quality = 101 }.EncoderOptions());
        Assert.Throws<ArgumentException>(() => new ImageOptions { Format = "png", Compression = -1 }.EncoderOptions());
    }
}

public class ShotsTests
{
    static string Reference(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    [Fact]
    public void HtmlIsScenedetectsPageByteForByte()
    {
        var fps = new Fps(25, 1);
        Shot S(int n, long a, long b) => new(n, FrameTime.Frame(a, fps), FrameTime.Frame(b, fps));
        Assert.Equal(Reference("scenedetect-two-shots-images.html"),
            Shots.Html([S(1, 0, 50), S(2, 50, 100)], [["a b-Scene-001-01.jpg"], ["x.jpg"]], imageWidth: 160));
        Assert.Equal(Reference("scenedetect-one-shot.html"), Shots.Html([S(1, 0, 150)]));
    }

    [Fact]
    public void CsvStartsWithTheCutListLikeScenedetect()
    {
        var fps = new Fps(25, 1);
        Shot S(int n, long a, long b) => new(n, FrameTime.Frame(a, fps), FrameTime.Frame(b, fps));
        string csv = Shots.Csv([S(1, 0, 50), S(2, 50, 100), S(3, 100, 150)]);
        Assert.StartsWith("Timecode List:,00:00:02.000,00:00:04.000\nScene Number,", csv);

        // No cuts: scenedetect's fallback row is empty, so the file starts with a blank line.
        Assert.StartsWith("\nScene Number,", Shots.Csv([S(1, 0, 150)]));
        Assert.StartsWith("Scene Number,", Shots.Csv([S(1, 0, 50), S(2, 50, 100)], includeCutList: false));
    }

    static readonly Fps Ntsc = new(30000, 1001);

    static FrameTime F(long n) => FrameTime.Frame(n, Ntsc);

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

// Expected values from cv2 (calcHist on the Y channel, normalize, compareHist HISTCMP_CORREL).
public class HistogramDetectorTests
{
    static readonly byte[] Image =
    [
        34, 32, 204, 127, 151, 153, 182, 7, 124, 37, 102, 237, 140, 18, 138, 33, 193, 242, 250, 159, 222, 94, 37, 130,
        113, 169, 254, 70, 219, 35, 89, 201, 63, 171, 117, 131, 240, 209, 214, 140, 251, 251, 34, 52, 78, 141, 210, 123,
        251, 90, 237, 151, 184, 60, 151, 205, 226, 222, 247, 32, 199, 119, 175, 70, 3, 21, 249, 229, 77, 110, 61, 37,
        219, 172, 19, 51, 144, 230, 254, 55, 156, 8, 44, 51, 113, 88, 186, 120, 86, 231, 160, 178, 190, 86, 228, 4,
        78, 40, 1, 255, 22, 117, 209, 176, 219, 13, 127, 8, 49, 216, 18, 150, 5, 79, 238, 81, 79, 22, 198, 44,
        127, 6, 5, 214, 2, 119, 109, 32, 165, 189, 252, 50, 236, 15, 36, 153, 222, 229, 189, 6, 106, 206, 135, 48,
    ];

    static byte[] Brighter() => Image.Select(b => (byte)Math.Min(255, b + 30)).ToArray();

    [Fact]
    public void HistogramMatchesOpenCv()
    {
        float[] expected = [0.10050377994775772f, 0.45226702094078064f, 0.30151134729385376f, 0.251259446144104f,
            0.6532745957374573f, 0.4020151197910309f, 0.20100755989551544f, 0.05025188997387886f];
        Assert.Equal(expected, HistogramDetector.Histogram(Image, 8));
    }

    [Fact]
    public void CorrelationMatchesCompareHist()
    {
        var a = HistogramDetector.Histogram(Image, 8);
        var b = HistogramDetector.Histogram(Brighter(), 8);
        Assert.Equal(0.160816861389227, HistogramDetector.Correlation(a, b), 15);
        Assert.Equal(1.0, HistogramDetector.Correlation(a, a));
    }

    [Fact]
    public void CutsWhenTheHistogramChanges()
    {
        var fps = new Fps(25, 1);
        var d = new HistogramDetector(i => FrameTime.Frame(i, fps), minSceneLen: 2);
        var cuts = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            if (d.ProcessFrame(i, i < 5 ? Image : Brighter()) is { } cut)
                cuts.Add(cut.FrameNum);
        }

        Assert.Equal([5], cuts);
    }

    [Fact]
    public void CountsTheMinimumLengthFromFrameZero()
    {
        // scenedetect 0.7.1 keeps frame 0 as the last cut (a FrameTimecode is always truthy): a cut at 2 is long enough.
        var fps = new Fps(25, 1);
        var d = new HistogramDetector(i => FrameTime.Frame(i, fps), minSceneLen: 2);
        var cuts = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            if (d.ProcessFrame(i, i < 2 ? Image : Brighter()) is { } cut)
                cuts.Add(cut.FrameNum);
        }

        Assert.Equal([2], cuts);
    }
}
