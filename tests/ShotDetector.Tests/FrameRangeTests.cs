using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Frame-source detection in ranges (<see cref="ShotDetection.ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>
/// and <see cref="ShotDetection.Merge"/>): the ranges' scores, through their bytes, merge into exactly the sequential
/// frame-source Detect's result, and anything that would make them differ is refused. A file's decoded frames split at its
/// keyframes are compared in FrameInputTests (with SHOTDETECTOR_FFMPEG_LIBS); these need nothing.
/// </summary>
public class FrameRangeTests
{
    const int Count = 100;

    static YuvVideo Video(bool timed = true) => new()
    {
        Width = 64,
        Height = 48,
        FrameRate = new(25, 1),
        TimeBase = timed ? new(1, 100) : null,
        StartPts = timed ? 40 : 0,
        FrameCount = Count,
        Codec = "h264",
    };

    /// <summary>
    /// Shots of noise with a fade to black at frames 30-39 (a cut at 30 for the content detectors, a fade for threshold), a
    /// flash at frame 70 and a dimmer stretch from 85; timed at 1/100 s from pts 40.
    /// </summary>
    internal static List<YuvFrame> Frames(YuvVideo video, int count = Count)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        var frames = new List<YuvFrame>();
        for (int i = 0; i < count; i++)
        {
            int seed = i < 30 ? 1 : i < 40 ? 0 : i < 60 ? 2 : i == 70 ? 4 : i < 85 ? 3 : 5;
            var random = new Random(seed);
            byte[] Plane(int width, int height, bool luma)
            {
                var plane = new byte[(width + 3) * height];
                random.NextBytes(plane);
                if (seed == 0 && luma)
                    Array.Fill(plane, (byte)(16 + (i - 30)));
                else if (seed == 0)
                    Array.Fill(plane, (byte)128);
                else if (seed == 5 && luma)
                    plane = [.. plane.Select(b => (byte)(b / 2))];
                else if (luma)
                    plane = [.. plane.Select(b => (byte)Math.Min(255, b + (i % 4)))]; // a little change within a shot
                return plane;
            }

            frames.Add(new YuvFrame(Plane(w, h, true), w + 3, Plane(cw, ch, false), cw + 3, Plane(cw, ch, false), cw + 3) { Pts = video.StartPts + i * 4 });
        }

        return frames;
    }

    /// <summary>The frames ScoreRange takes for the range [<paramref name="first"/>, <paramref name="end"/>): from the frame before to one past the end.</summary>
    internal static IEnumerable<YuvFrame> RangeFrames(IReadOnlyList<YuvFrame> all, int first, int end, int extra = 1) =>
        all.Skip(Math.Max(0, first - 1)).Take(Math.Min(all.Count, end + extra) - Math.Max(0, first - 1));

    /// <summary>Scores the video in ranges starting at <paramref name="starts"/>, each through its bytes, and merges them.</summary>
    internal static DetectionResult InRanges(YuvVideo video, IReadOnlyList<YuvFrame> all, int[] starts, DetectionOptions options, int extra = 1)
    {
        var ranges = new List<RangeScores>();
        for (int i = 0; i < starts.Length; i++)
        {
            int end = i + 1 < starts.Length ? starts[i + 1] : all.Count;
            var scores = ShotDetection.ScoreRange(video, RangeFrames(all, starts[i], end, extra), starts[i], options);
            ranges.Add(RangeScores.FromBytes(scores.ToBytes()));
        }

        return ShotDetection.Merge(video, ranges, options);
    }

    internal static void AssertSame(DetectionResult expected, DetectionResult actual)
    {
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        Assert.Equal(expected.MinSceneLengthFrames, actual.MinSceneLengthFrames);
        Assert.Equal(expected.Cuts, actual.Cuts);
        Assert.Equal(expected.Shots, actual.Shots);
        Assert.Equal(Shots.Csv(expected.Shots, cuts: expected.Cuts), Shots.Csv(actual.Shots, cuts: actual.Cuts));
        Assert.Equal(expected.Stats?.Csv(expected.Video.Position), actual.Stats?.Csv(actual.Video.Position));
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(1, actual.Video.Chunks);
        Assert.Null(actual.Video.ChunkFallback);
        Assert.Equal((expected.Video.ProcessedWidth, expected.Video.ProcessedHeight, expected.Video.FrameRate, expected.Video.OpenCvFrameCount),
            (actual.Video.ProcessedWidth, actual.Video.ProcessedHeight, actual.Video.FrameRate, actual.Video.OpenCvFrameCount));
        for (int k = 0; k <= expected.FrameCount; k++)
            Assert.Equal(expected.Video.Position(k), actual.Video.Position(k));
    }

    public static TheoryData<string, bool> Detectors()
    {
        var data = new TheoryData<string, bool>();
        foreach (string d in new[] { "adaptive", "content", "threshold", "histogram", "hash", "several", "drop", "v071", "untimed" })
        {
            data.Add(d, false);
            data.Add(d, true);
        }

        return data;
    }

    static DetectionOptions Options(string name, bool stats)
    {
        var o = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = stats, MinSceneLength = "5" };
        return name switch
        {
            "content" => o with { Detector = DetectorKind.Content, Threshold = 20 },
            "threshold" => o with { Detector = DetectorKind.Threshold, Threshold = 40 },
            "histogram" => o with { Detector = DetectorKind.Histogram },
            "hash" => o with { Detector = DetectorKind.Hash },
            "several" => o with
            {
                Detectors = [new(DetectorKind.Content) { Threshold = 20 }, new(DetectorKind.Threshold) { Threshold = 40 }, new(DetectorKind.Hash)],
                MergeLastScene = true,
            },
            "drop" => o with { Detector = DetectorKind.Content, Threshold = 20, DropShortScenes = true, MinSceneLength = "12" },
            "v071" => o with { Compatibility = PySceneDetectVersion.V0_7_1 },
            _ => o,
        };
    }

    static readonly int[][] Splits =
    [
        [0],
        [0, 50],
        [0, 33, 66],
        [0, 7, 93],
        [0, 40, 41], // a one-frame range
        [0, 30, 70], // ranges starting at a cut and at the flash
        [0, 99], // the last range one frame long
    ];

    [Theory]
    [MemberData(nameof(Detectors))]
    public void RangesMergeIntoTheSequentialResult(string detector, bool stats)
    {
        var video = Video(timed: detector != "untimed");
        var all = Frames(video);
        var options = Options(detector, stats);
        var expected = ShotDetection.Detect(video, all, options);
        Assert.True(expected.Shots.Count > 1, "the clip has cuts");
        foreach (var split in Splits)
            AssertSame(expected, InRanges(video, all, split, options));
    }

    [Fact]
    public void MoreOverlapFramesAreCheckedToo()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", true);
        AssertSame(ShotDetection.Detect(video, all, options), InRanges(video, all, [0, 30, 60], options, extra: 5));
    }
}
