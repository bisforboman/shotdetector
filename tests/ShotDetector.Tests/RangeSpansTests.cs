using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Black and freeze spans for ranges (<see cref="DetectionOptions.BlackDetect"/>, <see cref="DetectionOptions.FreezeDetect"/>
/// with <see cref="ShotDetection.ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>
/// and <see cref="ShotDetection.Merge"/>): however the video is split, the merge's spans are Detect's on the whole video,
/// byte for byte. A range that starts inside a still picture is named by <see cref="ShotDetection.FirstRangeToScoreAgain"/>
/// and scored again given the ranges before it. A decoded file's frames are checked in FrameInputTests.
/// </summary>
public class RangeSpansTests
{
    const int Count = 130;

    static YuvVideo Video(bool timed = true, FrameReader.FieldOrder field = FrameReader.FieldOrder.Progressive) => new()
    {
        Width = 64,
        Height = 48,
        FrameRate = new(25, 1),
        TimeBase = timed ? new(1, 100) : null,
        StartPts = timed ? 40 : 0,
        FrameCount = Count,
        Codec = "h264",
        FieldOrder = field,
    };

    /// <summary>
    /// Noise at 0-9; black and still at 10-29; a still of noise at 30-49; noise at 50-59; at 60-109 a grey picture whose
    /// first rows brighten by one a frame (each frame within n of the last few, so freezedetect's still picture moves on
    /// every 19 frames: 60, 79, 98); noise at 110-114; black and still to the end from 115.
    /// </summary>
    internal static List<YuvFrame> Frames(YuvVideo video, int count = Count)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        var frames = new List<YuvFrame>();
        for (int i = 0; i < count; i++)
        {
            byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
            if (i is >= 10 and < 30 || i >= 115)
            {
                Array.Fill(y, (byte)16);
                Array.Fill(u, (byte)128);
                Array.Fill(v, (byte)128);
            }
            else if (i is >= 60 and < 110)
            {
                Array.Fill(y, (byte)100);
                Array.Fill(u, (byte)128);
                Array.Fill(v, (byte)128);
                int rows = Math.Min(h, i - 60);
                for (int r = 0; r < rows; r++)
                    Array.Fill(y, (byte)101, r * w, w);
            }
            else
            {
                var random = new Random(i is >= 30 and < 50 ? 30 : 1000 + i);
                random.NextBytes(y);
                random.NextBytes(u);
                random.NextBytes(v);
            }

            frames.Add(new YuvFrame(y, w, u, cw, v, cw) { Pts = video.StartPts + i * 4 });
        }

        return frames;
    }

    static DetectionOptions Options(double seconds = 0.2) => new()
    {
        Detector = DetectorKind.Content,
        Yuv420Converter = new SwscaleYuv420(),
        CollectStats = true,
        BlackDetect = new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(seconds) },
        FreezeDetect = new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(seconds) },
    };

    /// <summary>The frames <see cref="ShotDetection.RangeFrames(YuvVideo, DetectionOptions?, long, long)"/> (or its overload) names, from the video's.</summary>
    static IEnumerable<YuvFrame> Given(IReadOnlyList<YuvFrame> all, (long From, long To) frames) =>
        all.Skip((int)frames.From).Take((int)(Math.Min(all.Count, frames.To) - frames.From));

    /// <summary>
    /// The video scored in ranges starting at <paramref name="starts"/>, each on its own (a host's workers), through its
    /// bytes; then each range <see cref="ShotDetection.FirstRangeToScoreAgain"/> names scored again given the ranges
    /// before it, until none is; then merged. Also how many were scored again.
    /// </summary>
    internal static (DetectionResult Merged, int Again) Split(YuvVideo video, IReadOnlyList<YuvFrame> all, IReadOnlyList<int> starts, DetectionOptions options)
    {
        int End(int i) => i + 1 < starts.Count ? starts[i + 1] : all.Count;
        var ranges = new List<RangeScores>();
        for (int i = 0; i < starts.Count; i++)
        {
            var given = Given(all, ShotDetection.RangeFrames(video, options, starts[i], End(i)));
            ranges.Add(RangeScores.FromBytes(ShotDetection.ScoreRange(video, given, starts[i], options).ToBytes()));
        }

        int again = 0;
        while (ShotDetection.FirstRangeToScoreAgain(video, ranges, options) is { } stale)
        {
            Assert.True(again++ < starts.Count, "a range named twice");
            var earlier = ranges[..stale];
            var given = Given(all, ShotDetection.RangeFrames(video, earlier, options, starts[stale], End(stale)));
            ranges[stale] = RangeScores.FromBytes(ShotDetection.ScoreRange(video, earlier, given, starts[stale], options).ToBytes());
        }

        return (ShotDetection.Merge(video, ranges, options), again);
    }

    /// <summary>The video scored in ranges one after another, each given the earlier ones (a resumed run's way), then merged.</summary>
    internal static DetectionResult Resumed(YuvVideo video, IReadOnlyList<YuvFrame> all, IReadOnlyList<int> starts, DetectionOptions options)
    {
        var ranges = new List<RangeScores>();
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1] : all.Count;
            var given = Given(all, ShotDetection.RangeFrames(video, ranges, options, starts[i], end));
            ranges.Add(RangeScores.FromBytes((i == 0 ? ShotDetection.ScoreRange(video, given, 0, options) : ShotDetection.ScoreRange(video, ranges, given, starts[i], options)).ToBytes()));
        }

        Assert.Null(ShotDetection.FirstRangeToScoreAgain(video, ranges, options));
        return ShotDetection.Merge(video, ranges, options);
    }

    /// <summary>The spans equal, as records (every double's bits) and as blackdetect's and freezedetect's lines.</summary>
    internal static void AssertSameSpans(DetectionResult expected, DetectionResult actual)
    {
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        Assert.Equal(expected.BlackSpans, actual.BlackSpans);
        Assert.Equal(expected.FreezeSpans, actual.FreezeSpans);
        Assert.Equal(expected.BlackSpans?.Select(s => s.ToString()), actual.BlackSpans?.Select(s => s.ToString()));
        Assert.Equal(expected.FreezeSpans?.Select(s => s.ToString()), actual.FreezeSpans?.Select(s => s.ToString()));
        Assert.Equal(expected.BlackSpans?.Select(s => BitConverter.DoubleToInt64Bits(s.Duration)), actual.BlackSpans?.Select(s => BitConverter.DoubleToInt64Bits(s.Duration)));
        Assert.Equal(expected.Cuts, actual.Cuts);
        Assert.Equal(expected.Stats?.Csv(expected.Video.Position), actual.Stats?.Csv(actual.Video.Position));
    }

    /// <summary>
    /// Splits: every frame its own range, at each stretch's first and last frames and the ones either side, inside the
    /// stretches, a few at random, and none.
    /// </summary>
    internal static IEnumerable<int[]> Splits(int count, IEnumerable<int> edges)
    {
        yield return [0];
        yield return [.. Enumerable.Range(0, count)];
        var at = edges.SelectMany(e => new[] { e - 1, e, e + 1 }).Where(e => e > 0 && e < count).Distinct().Order().ToArray();
        yield return [0, .. at];
        foreach (int e in at)
            yield return [0, e];
        var random = new Random(7);
        for (int i = 0; i < 6; i++)
            yield return [0, .. Enumerable.Range(1, count - 1).Where(_ => random.Next(8) == 0)];
    }

    static readonly int[] Edges = [10, 20, 29, 30, 40, 49, 50, 60, 70, 78, 79, 80, 97, 98, 109, 110, 115, 120, 129];

    [Theory]
    [InlineData(true, false, 0.2)]
    [InlineData(false, false, 0.2)]
    [InlineData(true, true, 0.2)]
    [InlineData(true, false, 0.0)]
    [InlineData(true, false, 2.0)]
    public void SpansOfAnySplitAreDetects(bool timed, bool deinterlace, double seconds)
    {
        var video = Video(timed, deinterlace ? FrameReader.FieldOrder.TopFirst : FrameReader.FieldOrder.Progressive);
        var all = Frames(video);
        var options = Options(seconds);
        if (deinterlace)
            options = options with { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() };
        var expected = ShotDetection.Detect(video, all, options);
        if (seconds > 0 && seconds < 1 && !deinterlace)
        {
            // The clip's stretches: black at 10 and at the end, stills at 10, 30, 60, 79, 98 and to the end.
            Assert.Equal([10, 115], expected.BlackSpans!.Select(s => s.StartFrame));
            Assert.Equal([10, 30, 60, 79, 98, 115], expected.FreezeSpans!.Select(s => s.StartFrame));
            Assert.Null(expected.FreezeSpans![^1].End);
        }

        int again = 0;
        foreach (var split in Splits(Count, Edges))
        {
            // Deinterlacing, a range continuing from earlier ones can't start at the video's last frame (the earlier
            // ranges' last frame isn't final for them: RangeScores.EndFrame), so a last range of one frame joins the one before.
            var starts = deinterlace && split.Length > 1 && split[^1] == Count - 1 ? split[..^1] : split;
            var (merged, rescored) = Split(video, all, starts, options);
            AssertSameSpans(expected, merged);
            again += rescored;
            AssertSameSpans(expected, Resumed(video, all, starts, options));
        }

        Assert.True(again > 0); // some splits started inside a still
    }

    [Fact]
    public async Task TheAsynchronousOverloadsGiveTheSameFlags()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();
        var earlier = new List<RangeScores> { ShotDetection.ScoreRange(video, all.Take(41), 0, options) };
        var frames = ShotDetection.RangeFrames(video, earlier, options, 35, Count);
        Assert.Equal(30, frames.From); // inside the still from frame 30: from it
        var sync = ShotDetection.ScoreRange(video, earlier, Given(all, frames), 35, options);
        var awaited = await ShotDetection.ScoreRangeAsync(video, earlier, FrameSourceAsyncTests.Yielding(Given(all, frames)), 35, options);
        Assert.Equal(sync.ToBytes(), awaited.ToBytes());
        var alone = ShotDetection.ScoreRange(video, Given(all, ShotDetection.RangeFrames(video, options, 35, Count)), 35, options);
        var aloneAwaited = (await ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(Given(all, ShotDetection.RangeFrames(video, options, 35, Count))), 35, options)).ToBytes();
        Assert.Equal(alone.ToBytes(), aloneAwaited);
        AssertSameSpans(ShotDetection.Detect(video, all, options), ShotDetection.Merge(video, [earlier[0], sync], options));
    }

    [Fact]
    public void ARangeStartingInsideAStillIsRefusedUntilScoredAgain()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();
        RangeScores a = ShotDetection.ScoreRange(video, all.Take(41), 0, options), b = ShotDetection.ScoreRange(video, all.Skip(34), 35, options);
        Assert.Equal(1, ShotDetection.FirstRangeToScoreAgain(video, [a, b], options));
        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [a, b], options));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("Range 1 (from frame 35) starts inside a still picture from frame 30", e.Message);

        // Frames from the frame before the range, given with the earlier ranges: refused (they must start at the still's first).
        Assert.Contains("starts inside a still picture from frame 30", Assert.Throws<ShotDetectionException>(() => ShotDetection.ScoreRange(video, [a], all.Skip(34), 35, options)).Message);

        // An earlier range that must be scored again first: refused for the next one.
        Assert.Contains("Range 1 (from frame 35)", Assert.Throws<ShotDetectionException>(() => ShotDetection.ScoreRange(video, [a, b], all.Skip(29), 38, options)).Message);
        Assert.Contains("Range 1 (from frame 35)", Assert.Throws<ShotDetectionException>(() => ShotDetection.RangeFrames(video, [a, b], options, 38, Count)).Message);

        // Without FreezeDetect nothing is scored again, and the same ranges merge.
        var plain = options with { FreezeDetect = null };
        RangeScores pa = ShotDetection.ScoreRange(video, all.Take(41), 0, plain), pb = ShotDetection.ScoreRange(video, all.Skip(34), 35, plain);
        Assert.Null(ShotDetection.FirstRangeToScoreAgain(video, [pa, pb], plain));
        Assert.Equal(ShotDetection.Detect(video, all, plain).BlackSpans, ShotDetection.Merge(video, [pa, pb], plain).BlackSpans);
        Assert.Equal(ShotDetection.RangeFrames(video, plain, 35, Count), ShotDetection.RangeFrames(video, [pa], plain, 35, Count));
    }

    [Fact]
    public void TheFlagsAreInTheFingerprintAndTheBytes()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();

        // Ranges scored without the options, or with others, don't merge with them.
        var without = ShotDetection.ScoreRange(video, all, 0, options with { BlackDetect = null, FreezeDetect = null });
        Assert.Contains("BlackDetect", Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [without], options)).Message);
        var other = ShotDetection.ScoreRange(video, all, 0, options with { FreezeDetect = new FreezeDetectOptions { Noise = 0.01 } });
        Assert.Contains("FreezeDetect", Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [other], options)).Message);

        // Without either, the bytes stay version 1, as before them; with them, version 2, read back the same.
        Assert.Equal(1, without.ToBytes()[4]);
        var scores = ShotDetection.ScoreRange(video, all.Skip(59), 60, options);
        var bytes = scores.ToBytes();
        Assert.Equal(2, bytes[4]);
        var back = RangeScores.FromBytes(bytes);
        Assert.Equal(scores.Black, back.Black);
        Assert.Equal(scores.Frozen, back.Frozen);
        Assert.Equal(59, back.FreezeSeed);
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal(Count - 60, back.Frozen!.Length);
        Assert.Equal(Enumerable.Range(60, Count - 60).Select(k => k is >= 115), back.Black!);

        // Black alone, or freeze alone.
        foreach (var one in new[] { options with { BlackDetect = null }, options with { FreezeDetect = null } })
        {
            var only = ShotDetection.ScoreRange(video, all.Skip(59), 60, one);
            var read = RangeScores.FromBytes(only.ToBytes());
            Assert.Equal(only.Black, read.Black);
            Assert.Equal(only.Frozen, read.Frozen);
            Assert.Equal(only.FreezeSeed, read.FreezeSeed);
        }
    }
}
