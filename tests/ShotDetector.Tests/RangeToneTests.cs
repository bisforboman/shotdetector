using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// <see cref="DetectionOptions.TrimReferenceTone"/> for ranges, with the caller's tone (<see cref="YuvVideo.ReferenceTone"/>):
/// however the video is split, scored on its own or resumed, the merge is Detect's on the whole video with the same
/// tone, byte for byte: shots, cuts, the scene list, the trimmed range's stats, the black and freeze spans and the tone.
/// A stopped run's merge is Detect's on frames that end there. The clip is shotdetector-web's tone-silence-320x240-25.mp4
/// in shape (tone at 0-6 s and 9-15 s, so the run is 6-9 s), smaller; a decoded file's frames are checked in FrameInputTests.
/// </summary>
public class RangeToneTests
{
    const int Count = 375; // 15 s at 25 fps
    const int Start = 150; // the frame at 6 s, after the leading tone
    const int End = 225;   // the frame at 9 s, where the trailing tone starts: the run's frames are 150-224

    internal static readonly IReadOnlyList<FrameReader.ToneSegment> Tone =
    [
        new(TimeSpan.Zero, TimeSpan.FromSeconds(6), -20),
        new(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15), -20),
    ];

    static YuvVideo Video(bool timed = true, FrameReader.FieldOrder field = FrameReader.FieldOrder.Progressive) => new()
    {
        Width = 64,
        Height = 48,
        FrameRate = new(25, 1),
        TimeBase = timed ? new(1, 100) : null,
        StartPts = timed ? 40 : 0,
        FrameCount = Count,
        Duration = TimeSpan.FromSeconds(15),
        Codec = "h264",
        FieldOrder = field,
        ReferenceTone = Tone,
    };

    /// <summary>
    /// Shots of 13 frames, each a picture of noise whose first rows brighten by one a frame (a still to freezedetect,
    /// cuts between); a still across the run's start (140-159), black across its end (215-234), noise frame by frame at
    /// 180-189.
    /// </summary>
    static List<YuvFrame> Frames(YuvVideo video, int count = Count)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        var frames = new List<YuvFrame>();
        for (int i = 0; i < count; i++)
        {
            byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
            if (i is >= 215 and < 235)
            {
                Array.Fill(y, (byte)16);
                Array.Fill(u, (byte)128);
                Array.Fill(v, (byte)128);
            }
            else
            {
                var random = new Random(i is >= 140 and < 160 ? 140 : i is >= 180 and < 190 ? 1000 + i : i / 13);
                random.NextBytes(y);
                random.NextBytes(u);
                random.NextBytes(v);
                if (i is < 140 or >= 160)
                {
                    for (int x = 0; x < w * Math.Min(h, i % 13); x++)
                        y[x] = (byte)Math.Min(255, y[x] + 1);
                }
            }

            frames.Add(new YuvFrame(y, w, u, cw, v, cw) { Pts = video.StartPts + i * 4 });
        }

        return frames;
    }

    static DetectionOptions Options(DetectorKind detector = DetectorKind.Content) => new()
    {
        Detector = detector,
        MinSceneLength = "5",
        Yuv420Converter = new SwscaleYuv420(),
        CollectStats = true,
        TrimReferenceTone = true,
        BlackDetect = new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.2) },
        FreezeDetect = new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(0.2) },
    };

    /// <summary>Everything a page shows of a run: <see cref="FrameRangeTests.AssertSame"/>'s, the spans and the tone.</summary>
    static void AssertSame(DetectionResult expected, DetectionResult actual)
    {
        FrameRangeTests.AssertSame(expected, actual);
        RangeSpansTests.AssertSameSpans(expected, actual);
        Assert.Equal(expected.FirstFrame, actual.FirstFrame);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.ReferenceTone, actual.ReferenceTone);
    }

    /// <summary>Splits inside the leading and trailing tone, at the run's edges and around them, and those of RangeSpansTests.</summary>
    static IEnumerable<int[]> Splits(int count) =>
        RangeSpansTests.Splits(count, [1, 75, 140, 149, 150, 151, 152, 159, 160, 200, 215, 223, 224, 225, 226, 300, count - 1]);

    [Theory]
    [InlineData(true, false, DetectorKind.Content)]
    [InlineData(false, false, DetectorKind.Content)]
    [InlineData(true, true, DetectorKind.Content)]
    [InlineData(true, false, DetectorKind.Adaptive)]
    [InlineData(true, false, DetectorKind.Threshold)]
    public void AnySplitIsDetects(bool timed, bool deinterlace, DetectorKind detector)
    {
        var video = Video(timed, deinterlace ? FrameReader.FieldOrder.TopFirst : FrameReader.FieldOrder.Progressive);
        var all = Frames(video);
        var options = Options(detector);
        if (deinterlace)
            options = options with { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() };
        var expected = ShotDetection.Detect(video, all, options);
        Assert.Equal(Start, expected.FirstFrame);
        Assert.Equal(End - Start, expected.FrameCount);
        Assert.Equal(Tone, expected.ReferenceTone);
        if (detector == DetectorKind.Content && !deinterlace)
        {
            Assert.NotEmpty(expected.Cuts);
            Assert.Equal([215], expected.BlackSpans!.Select(s => s.StartFrame));
            Assert.Contains(expected.FreezeSpans!, s => s.StartFrame == Start);
        }

        foreach (var split in Splits(Count))
        {
            // Deinterlacing, a range continuing from earlier ones can't start at the video's last frame (RangeSpansTests).
            var starts = deinterlace && split.Length > 1 && split[^1] == Count - 1 ? split[..^1] : split;
            AssertSame(expected, RangeSpansTests.Split(video, all, starts, options).Merged);
            AssertSame(expected, RangeSpansTests.Resumed(video, all, starts, options));
        }
    }

    [Theory]
    [InlineData(200)] // inside the run
    [InlineData(151)] // just after its start
    [InlineData(224)] // at its last frame
    [InlineData(260)] // inside the trailing tone
    public void AStoppedRunIsDetectOnFramesEndingThere(int stop)
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();
        var expected = ShotDetection.Detect(video, all.Take(stop), options);
        foreach (var split in new int[][] { [0], [0, 100], [0, 150, 151], [0, 149, 152, stop - 1] })
        {
            var starts = split.Where(s => s < stop).Distinct().ToArray();
            var given = all.Take(stop).ToList();
            AssertSame(expected, RangeSpansTests.Split(video, given, starts, options).Merged);
            AssertSame(expected, RangeSpansTests.Resumed(video, given, starts, options));
        }

        // Frames that end in the leading tone: refused, as Detect refuses them (no frame where the run starts).
        var early = all.Take(100).ToList();
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, early, options));
        Assert.Throws<ArgumentException>(() => ShotDetection.Merge(video, [ShotDetection.ScoreRange(video, early, 0, options)], options));
    }

    [Fact]
    public void OnShotTellsTheRunsShots()
    {
        var video = Video();
        var all = Frames(video);
        var told = new List<Shot>();
        var options = Options() with { BlackDetect = null, FreezeDetect = null };
        var expected = ShotDetection.Detect(video, all, options);

        // One range from frame 0 (a one-worker page): its shots as Detect tells them.
        ShotDetection.ScoreRange(video, all, 0, options with { OnShot = new Inline<Shot>(told.Add) });
        Assert.Equal(expected.Shots, told);

        // A range ending in the leading tone tells nothing; the one continuing from it tells every shot.
        told.Clear();
        var first = ShotDetection.ScoreRange(video, all.Take(101), 0, options with { OnShot = new Inline<Shot>(told.Add) });
        Assert.Empty(told);
        ShotDetection.ScoreRange(video, [first], all.Skip(99), 100, options with { OnShot = new Inline<Shot>(told.Add) });
        Assert.Equal(expected.Shots, told);
    }

    [Fact]
    public async Task TheAsynchronousOverloadsScoreTheSame()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();
        foreach (int first in new[] { 0, 100, Start, Start + 1, 200, 300 })
        {
            var given = all.Skip(Math.Max(0, first - 1)).ToList();
            var sync = ShotDetection.ScoreRange(video, given, first, options).ToBytes();
            Assert.Equal(sync, (await ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(given), first, options)).ToBytes());
        }

        var earlier = new List<RangeScores> { ShotDetection.ScoreRange(video, all.Take(156), 0, options) };
        var frames = ShotDetection.RangeFrames(video, earlier, options, 155, Count);
        Assert.Equal(Start, frames.From); // inside the still from the run's start: from it
        var rest = all.Skip((int)frames.From).ToList();
        var continued = ShotDetection.ScoreRange(video, earlier, rest, 155, options);
        Assert.Equal(continued.ToBytes(), (await ShotDetection.ScoreRangeAsync(video, earlier, FrameSourceAsyncTests.Yielding(rest), 155, options)).ToBytes());
        AssertSame(ShotDetection.Detect(video, all, options), ShotDetection.Merge(video, [earlier[0], continued], options));
    }

    [Fact]
    public void RangesBeforeTheRunScoreNothing()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();
        var lead = ShotDetection.ScoreRange(video, all.Take(101), 0, options);
        Assert.Equal(101, lead.FrameCount);
        Assert.All(lead.Metrics, m => Assert.All(m, s => Assert.Null(s)));
        Assert.Empty(lead.Stats!);
        Assert.All(lead.Black!, b => Assert.False(b));

        // The range from the run's start scores its first frame as Detect's first (no previous frame).
        var run = ShotDetection.ScoreRange(video, all.Skip(Start - 1), Start, options);
        var alone = ShotDetection.ScoreRange(video with { ReferenceTone = null }, all.Skip(Start), 0, options with { TrimReferenceTone = false });
        Assert.Equal(alone.Metrics[0][0], run.Metrics[0][0]);
        Assert.Equal(-1, run.FreezeSeed);
    }

    [Fact]
    public void TheToneIsInTheFingerprint()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options();

        // Without the tone given, refused as Detect refuses it.
        var untoned = video with { ReferenceTone = null };
        Assert.Contains("YuvVideo.ReferenceTone", Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(untoned, all, 0, options)).Message);
        Assert.Contains("YuvVideo.ReferenceTone", Assert.Throws<ArgumentException>(() => ShotDetection.Merge(untoned, [ShotDetection.ScoreRange(untoned, all, 0, options with { TrimReferenceTone = false })], options)).Message);

        // Ranges scored without the trim, or with another tone, don't merge with it.
        var plain = ShotDetection.ScoreRange(video, all, 0, options with { TrimReferenceTone = false });
        Assert.Contains("TrimReferenceTone", Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [plain], options)).Message);
        var other = video with { ReferenceTone = [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), -20)] };
        Assert.Contains("TrimReferenceTone", Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [ShotDetection.ScoreRange(other, all, 0, options)], options)).Message);

        // Only when set: the fingerprint without it is as before.
        string with = ShotDetection.Fingerprint(video, options.Resolved()), without = ShotDetection.Fingerprint(video, (options with { TrimReferenceTone = false }).Resolved());
        Assert.Contains("\nTrimReferenceTone=0,60000000,-20;90000000,150000000,-20\n", "\n" + with);
        Assert.DoesNotContain("TrimReferenceTone", without);
        Assert.Equal(without, ShotDetection.Fingerprint(untoned, (options with { TrimReferenceTone = false }).Resolved()));
    }

    sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
