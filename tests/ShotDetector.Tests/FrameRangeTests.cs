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
        foreach (string d in new[] { "adaptive", "content", "threshold", "histogram", "hash", "several", "drop", "v071", "untimed", "crop", "scale" })
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
            "crop" => o with { Detector = DetectorKind.Content, Threshold = 20, Crop = (5, 6, 50, 40) },
            "scale" => o with { Downscale = 1 },
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

    [Fact]
    public async Task TheAsynchronousOverloadScoresAsTheSynchronousOne()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("several", true);
        foreach (int first in new[] { 0, 33 })
        {
            var frames = RangeFrames(all, first, first + 40).ToList();
            var sync = ShotDetection.ScoreRange(video, frames, first, options);
            var awaited = await ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(frames), first, options);
            Assert.Equal(sync.ToBytes(), awaited.ToBytes());
            Assert.Equal((first, 41), (awaited.FirstFrame, awaited.FrameCount));
        }
    }

    [Fact]
    public async Task ScoringARangeCancelsBetweenFrames()
    {
        var video = Video();
        using var cancel = new CancellationTokenSource();
        int pulled = 0;
        IEnumerable<YuvFrame> Source()
        {
            foreach (var frame in RangeFrames(Frames(video), 20, 60))
            {
                if (++pulled == 10)
                    cancel.Cancel();
                yield return frame;
            }
        }

        var options = Options("adaptive", false);
        Assert.ThrowsAny<OperationCanceledException>(() => ShotDetection.ScoreRange(video, Source(), 20, options, cancel.Token));
        Assert.Equal(10, pulled);
        pulled = 0;
        using var again = new CancellationTokenSource();
        async IAsyncEnumerable<YuvFrame> Awaited()
        {
            foreach (var frame in RangeFrames(Frames(video), 20, 60))
            {
                pulled++;
                yield return frame;
                await Task.Yield();
                if (pulled == 10)
                    await again.CancelAsync();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShotDetection.ScoreRangeAsync(video, Awaited(), 20, options, again.Token));
        Assert.InRange(pulled, 10, 11);
    }

    sealed class Told<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("several")]
    public void MergeTellsTheShotsAsDetectDoes(string detector)
    {
        var video = Video();
        var all = Frames(video);
        var told = new Told<Shot>();
        var options = Options(detector, false) with { OnShot = told };
        var expected = ShotDetection.Detect(video, all, options);
        var detected = told.Values.ToList();
        told.Values.Clear();
        var merged = InRanges(video, all, [0, 30, 70], options);
        Assert.Equal(detected, told.Values);
        Assert.Equal(expected.Shots, told.Values);
        Assert.Equal(merged.Shots, told.Values);
    }

    [Fact]
    public void ScoringReportsTheRangesFramesAndMergeNothing()
    {
        var video = Video();
        var all = Frames(video);
        var progress = new Told<DetectionProgress>();
        var options = Options("adaptive", false) with { Progress = progress };
        var scores = ShotDetection.ScoreRange(video, RangeFrames(all, 30, 70), 30, options);
        Assert.Equal(new DetectionProgress { FramesProcessed = 0, ExpectedFrames = 0 }, progress.Values[0]);
        Assert.Equal(new DetectionProgress { FramesProcessed = 41, ExpectedFrames = 41 }, progress.Values[^1]);
        progress.Values.Clear();
        var whole = ShotDetection.ScoreRange(video, all, 0, options);
        progress.Values.Clear();
        ShotDetection.Merge(video, [whole], options);
        Assert.Empty(progress.Values);
    }

    static ShotDetectionException Refused(Func<object> merge)
    {
        var e = Assert.Throws<ShotDetectionException>(merge);
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        return e;
    }

    static RangeScores Score(YuvVideo video, IReadOnlyList<YuvFrame> all, int first, int end, DetectionOptions options, int extra = 1) =>
        ShotDetection.ScoreRange(video, RangeFrames(all, first, end, extra), first, options);

    [Fact]
    public void MergeRefusesRangesThatDontMakeUpTheVideo()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", true);
        RangeScores a = Score(video, all, 0, 33, options), b = Score(video, all, 33, 66, options), c = Score(video, all, 66, Count, options);
        ShotDetection.Merge(video, [a, b, c], options);

        Assert.Contains("must start at frame 0, not 33", Refused(() => ShotDetection.Merge(video, [b, c], options)).Message);
        Assert.Contains("out of order: range 2 starts at frame 20, not after range 1's start, frame 33", Refused(() => ShotDetection.Merge(video, [a, b, Score(video, all, 20, Count, options)], options)).Message);
        Assert.Contains("Frames 34 to 65 are in no range", Refused(() => ShotDetection.Merge(video, [a, c], options)).Message);
        var noOverlap = Score(video, all, 0, 33, options, extra: 0);
        Assert.Contains("Range 0 ends at frame 32, without range 1's first frame (33)", Refused(() => ShotDetection.Merge(video, [noOverlap, b, c], options)).Message);
        Assert.Throws<ArgumentException>(() => ShotDetection.Merge(video, [], options));

        // Bytes of the right fingerprint whose detector counts differ (only forged ones can).
        string fingerprint = ShotDetection.Fingerprint(video, options.Resolved());
        var one = new RangeScores(fingerprint, 0, 2, [40, 44], [[1, 2]], null, []);
        var two = new RangeScores(fingerprint, 1, 1, [40, 44], [[2], [3]], null, []);
        Assert.Contains("different numbers of detectors", Refused(() => ShotDetection.Merge(video, [one, two], options)).Message);
    }

    [Fact]
    public void MergeRefusesAnOverlapFrameThatDiffers()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", true);
        var a = Score(video, all, 0, 50, options);

        // Range 1 given another frame 50 (its first), or another frame before it: the score of frame 50 differs.
        foreach (int changed in new[] { 50, 49 })
        {
            var frames = all.Select((f, i) => i == changed ? f with { Y = f.Y.ToArray().Select(v => (byte)(255 - v)).ToArray() } : f).ToList();
            var b = Score(video, frames, 50, Count, options);
            Assert.Contains("Frame 50's score differs between range 0", Refused(() => ShotDetection.Merge(video, [a, b], options)).Message);
        }

        // A frame later in the overlap differs too (a range given several frames past the next one's start).
        var longer = Score(video, all, 0, 50, options, extra: 5);
        var late = Score(video, all.Select((f, i) => i == 53 ? f with { Y = new byte[f.Y.Length] } : f).ToList(), 50, Count, options);
        Assert.Contains("Frame 53's score differs", Refused(() => ShotDetection.Merge(video, [longer, late], options)).Message);
    }

    [Fact]
    public void MergeRefusesFramesNotWhereTheirPtsPutThem()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", false);
        var a = Score(video, all, 0, 50, options);

        // The overlap frames' pts differ: range 1 was given another frame 49 (the one before its first).
        var moved = all.Select((f, i) => i == 49 ? f with { Pts = f.Pts + 1 } : f).ToList();
        Assert.Contains("Frame 49 is at pts 236 in range 0 and 237 in range 1", Refused(() => ShotDetection.Merge(video, [a, Score(video, moved, 50, Count, options)], options)).Message);

        // The frames of 40 on, given as the range from 50: the same frames, the wrong firstFrame.
        var wrong = ShotDetection.ScoreRange(video, all.Skip(39), 50, options);
        Assert.Contains("not the same frame", Refused(() => ShotDetection.Merge(video, [a, wrong], options)).Message);

        // A range missing a frame (a decoder dropped frame 60): its later frames are off the grid from frame 0's.
        var dropped = ShotDetection.ScoreRange(video, all.Skip(49).Where((_, i) => i != 11), 50, options);
        var e = Refused(() => ShotDetection.Merge(video, [a, dropped], options));
        Assert.Contains("Frame 60 is 2.44 s after the first, not 2.4 s", e.Message);
        Assert.Contains("(frame 60 of the range from frame 50", e.Message);

        // Range 0's own frames are checked as Detect checks them, as they come.
        var early = all.Select((f, i) => i == 20 ? f with { Pts = f.Pts + 3 } : f).ToList();
        Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => Score(video, early, 0, 50, options)).Reason);
    }

    [Fact]
    public void MergeRefusesAnotherVideoOrOtherOptions()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", true);
        RangeScores a = Score(video, all, 0, 50, options), b = Score(video, all, 50, Count, options);
        Assert.Contains("the range has \"Codec=h264\", the merge \"Codec=hevc\"", Refused(() => ShotDetection.Merge(video with { Codec = "hevc" }, [a, b], options)).Message);
        Assert.Contains("the range has \"Threshold=none\", the merge \"Threshold=3.5\"", Refused(() => ShotDetection.Merge(video, [a, b], options with { Threshold = 3.5 })).Message);
        var other = Score(video, all, 50, Count, options with { MinContentVal = 10 });
        Assert.Contains("Range 1 (from frame 50)", Refused(() => ShotDetection.Merge(video, [a, other], options)).Message);

        // Callbacks and what only decoding reads aren't part of it.
        ShotDetection.Merge(video, [a, b], options with { Progress = new Told<DetectionProgress>(), Decoder = VideoDecoder.FfmpegProcess, Mode = DetectionMode.Sequential });
    }

    [Fact]
    public async Task ScoringRefusesWhatARangeCantHonour()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", false);
        foreach (var refused in new[] { options with { StartTime = "1" }, options with { End = TimeSpan.FromSeconds(3) }, options with { FrameSkip = 1 }, options with { Mode = DetectionMode.Parallel } })
        {
            Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(video, all, 0, refused));
            await Assert.ThrowsAsync<ArgumentException>(() => ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(all), 0, refused));
            Assert.Throws<ArgumentException>(() => ShotDetection.Merge(video, [ShotDetection.ScoreRange(video, all, 0, options)], refused));
        }

        Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(video, all, 0, options with { Yuv420Converter = null }));
        Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(video, all, 0, options with { Deinterlace = DeinterlaceMode.Auto }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShotDetection.ScoreRange(video, all, -1, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(all), int.MaxValue, options); }); // at once, not in the task

        // The frames end before the range's first: only the frame before it, or nothing.
        Assert.Contains("needs frame 49", Refused(() => ShotDetection.ScoreRange(video, all.Skip(49).Take(1), 50, options)).Message);
        Assert.Contains("given 0 frames", Refused(() => ShotDetection.ScoreRange(video, [], 50, options)).Message);
        Assert.Equal(0, ShotDetection.ScoreRange(video, [], 0, options).FrameCount); // an empty video
    }

    [Fact]
    public void BytesRoundTripEveryDoubleExactly()
    {
        double payloadNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_1234), subnormal = double.Epsilon * 3;
        var scores = new RangeScores("Width=1\n",
            5,
            4,
            [10, 11, 12, 13, 14],
            [[payloadNaN, -0.0, subnormal, null], [1.5, double.NegativeInfinity, null, 0.1 + 0.2]],
            [(5, "a", payloadNaN), (6, "b", -0.0), (8, "a", subnormal)],
            ["one", "twö"]);
        var back = RangeScores.FromBytes(scores.ToBytes());
        Assert.Equal((5, 4), (back.FirstFrame, back.FrameCount));
        Assert.Equal(scores.Fingerprint, back.Fingerprint);
        Assert.Equal(scores.Pts, back.Pts);
        Assert.Equal(scores.Warnings, back.Warnings);
        static long?[] Bits(double?[] m) => [.. m.Select(v => v is { } d ? BitConverter.DoubleToInt64Bits(d) : (long?)null)];
        Assert.Equal(scores.Metrics.Select(Bits), back.Metrics.Select(Bits));
        Assert.Equal(scores.Stats!.Select(s => (s.Frame, s.Key, BitConverter.DoubleToInt64Bits(s.Value))), back.Stats!.Select(s => (s.Frame, s.Key, BitConverter.DoubleToInt64Bits(s.Value))));
        Assert.Equal(scores.ToBytes(), back.ToBytes());
        Assert.Null(RangeScores.FromBytes(new RangeScores("", 0, 0, [], [[]], null, []).ToBytes()).Stats);
    }

    [Fact]
    public void FromBytesRefusesBytesItCantRead()
    {
        var video = Video();
        var bytes = Score(video, Frames(video), 30, 70, Options("adaptive", true)).ToBytes();
        static string Message(byte[] b)
        {
            var e = Assert.Throws<ShotDetectionException>(() => RangeScores.FromBytes(b));
            Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
            return e.Message;
        }

        Assert.Contains("aren't RangeScores", Message([.. "PK\u0003\u0004 a zip file"u8]));
        Assert.Contains("aren't RangeScores", Message([]));
        var newer = bytes.ToArray();
        newer[4] = 2;
        Assert.Contains("format version 2; this ShotDetector reads version 1", Message(newer));
        Assert.Contains("truncated", Message(bytes[..10]));
        Assert.Contains("checksum", Message(bytes[..^1]));
        Assert.Contains("checksum", Message(bytes[..(bytes.Length / 2)]));
        var damaged = bytes.ToArray();
        damaged[bytes.Length / 2] ^= 0x10;
        Assert.Contains("checksum", Message(damaged));

        // Well-formed bytes that don't hold together, with their checksum right: refused too, never read past.
        static byte[] Sealed(byte[] body) => [.. body, .. BitConverter.GetBytes(RangeScores.Checksum(body))];
        Assert.Contains("left over", Message(Sealed([.. bytes[..^12], 0, 0, 0, 0, 0])));
        Assert.Contains("detector count", Message(new RangeScores("", 0, 0, [], [], null, []).ToBytes()));
        var huge = bytes[..^8].ToArray();
        int counts = 4 + 2 + 4 + BitConverter.ToInt32(huge, 6);
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, counts + 4);
        Assert.Contains("inconsistent", Message(Sealed(huge)));
    }

    [Fact]
    public void TheFingerprintCoversEveryOption()
    {
        // Every option is either in the fingerprint or left out on purpose: a new one must be put in one of them.
        var names = typeof(DetectionOptions).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Order()
            .ToList();
        Assert.Equal(names, ShotDetection.Fingerprinted.Concat(ShotDetection.NotFingerprinted).Order().ToList());
        Assert.Empty(ShotDetection.Fingerprinted.Intersect(ShotDetection.NotFingerprinted));

        // Each fingerprinted one is written (the typed times inside their strings), every YuvVideo property and every
        // detector setting too.
        string text = ShotDetection.Fingerprint(Video(), new DetectionOptions { Detectors = [new(DetectorKind.Content)] }.Resolved());
        foreach (string name in ShotDetection.Fingerprinted.Except(["MinSceneDuration", "MinOutDuration"]))
            Assert.Contains("\n" + name + "=", "\n" + text);
        foreach (var p in typeof(YuvVideo).GetProperties().Where(p => p.Name != "EqualityContract"))
            Assert.Contains("\n" + p.Name + "=", "\n" + text);
        foreach (var p in typeof(DetectorSettings).GetProperties().Where(p => p.Name is not ("EqualityContract" or "MinSceneDuration" or "MinOutDuration")))
            Assert.Contains("\nDetectors[0]." + p.Name + "=", "\n" + text);
        Assert.Equal(text, ShotDetection.Fingerprint(Video(), new DetectionOptions { Detectors = [new(DetectorKind.Content)] }.Resolved()));
        Assert.Equal(
            ShotDetection.Fingerprint(Video(), new DetectionOptions { MinSceneLength = "1.5s" }.Resolved()),
            ShotDetection.Fingerprint(Video(), new DetectionOptions { MinSceneDuration = TimeSpan.FromSeconds(1.5) }.Resolved()));
    }
}
