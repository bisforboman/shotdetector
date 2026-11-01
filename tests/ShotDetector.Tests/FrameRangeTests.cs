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

    /// <summary><see cref="Frames"/> flagged interlaced, the field order changing every 7 frames (each frame's own, as yadif takes it).</summary>
    static List<YuvFrame> Interlaced(YuvVideo video, int count = Count) =>
        [.. Frames(video, count).Select((f, i) => f with { Interlaced = true, TopFieldFirst = i / 7 % 2 == 0 })];

    /// <summary>The frames <see cref="ShotDetection.RangeFrames"/> says to give ScoreRange for [<paramref name="first"/>, <paramref name="end"/>).</summary>
    static IEnumerable<YuvFrame> Given(YuvVideo video, DetectionOptions options, IReadOnlyList<YuvFrame> all, int first, int end)
    {
        var (from, to) = ShotDetection.RangeFrames(video, options, first, end);
        return all.Skip((int)from).Take((int)Math.Min(all.Count, to) - (int)from);
    }

    // RangeFrames: one frame on each side, two when deinterlacing (On, or Auto on an interlaced field order), never before
    // frame 0; the ranges given those frames merge into Detect's result exactly, deinterlaced or not.
    [Theory]
    [InlineData("content", false)]
    [InlineData("adaptive", true)]
    [InlineData("deinterlaced", true)]
    [InlineData("auto", true)]
    [InlineData("auto-progressive", true)]
    public void RangeFramesGiveTheFramesEachRangeNeeds(string name, bool stats)
    {
        var video = Video();
        var options = Options(name is "content" ? "content" : "adaptive", stats);
        options = name switch
        {
            "deinterlaced" => options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() },
            "auto" => options with { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() },
            "auto-progressive" => options with { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() },
            _ => options,
        };
        if (name == "auto")
            video = video with { FieldOrder = FrameReader.FieldOrder.BottomFirst };
        if (name == "auto-progressive")
            video = video with { FieldOrder = FrameReader.FieldOrder.Progressive };
        int margin = name is "deinterlaced" or "auto" ? 2 : 1;
        Assert.Equal((0L, 50L + margin), ShotDetection.RangeFrames(video, options, 0, 50));
        Assert.Equal((50L - margin, 100L + margin), ShotDetection.RangeFrames(video, options, 50, 100));
        Assert.Equal((0L, 3L + margin), ShotDetection.RangeFrames(video, options, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShotDetection.RangeFrames(video, options, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShotDetection.RangeFrames(video, options, -1, 5));

        var all = Interlaced(video);
        var expected = ShotDetection.Detect(video, all, options);
        Assert.Equal(margin == 2, expected.Video.Deinterlaced);
        foreach (var starts in Splits.Append([0, 1, 2]).Append([0, 2, 50]))
        {
            var ranges = new List<RangeScores>();
            for (int i = 0; i < starts.Length; i++)
            {
                int end = i + 1 < starts.Length ? starts[i + 1] : all.Count;
                ranges.Add(RangeScores.FromBytes(ShotDetection.ScoreRange(video, Given(video, options, all, starts[i], end), starts[i], options).ToBytes()));
            }

            AssertSame(expected, ShotDetection.Merge(video, ranges, options));
        }
    }

    // Deinterlaced, each range given yadif's neighbours (two frames before its first, two past its end): the ranges merge
    // into the sequential deinterlaced result exactly, with every split, whether deinterlacing is On or Auto (an interlaced
    // field order). One frame fewer past the end leaves the overlap frame without its final score: refused.
    [Theory]
    [InlineData("adaptive", true)]
    [InlineData("content", false)]
    [InlineData("histogram", true)]
    [InlineData("hash", false)]
    [InlineData("several", true)]
    [InlineData("untimed", true)]
    [InlineData("scale", true)]
    [InlineData("auto", true)]
    public void DeinterlacedRangesMergeIntoTheSequentialResult(string detector, bool stats)
    {
        var video = Video(timed: detector != "untimed");
        var options = Options(detector, stats) with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        if (detector == "auto")
            (video, options) = (video with { FieldOrder = FrameReader.FieldOrder.TopFirst }, options with { Deinterlace = DeinterlaceMode.Auto });
        var all = Interlaced(video);
        var expected = ShotDetection.Detect(video, all, options);
        Assert.True(expected.Video.Deinterlaced);
        Assert.NotEqual(ShotDetection.Detect(video, all, options with { Deinterlace = DeinterlaceMode.Off }).Stats?.Csv(expected.Video.Position) ?? "x",
            expected.Stats?.Csv(expected.Video.Position) ?? "");
        foreach (var starts in Splits.Append([0, 1, 2]).Append([0, 2, 50]))
        {
            var ranges = new List<RangeScores>();
            for (int i = 0; i < starts.Length; i++)
            {
                int end = i + 1 < starts.Length ? starts[i + 1] : all.Count;
                ranges.Add(RangeScores.FromBytes(ShotDetection.ScoreRange(video, Given(video, options, all, starts[i], end), starts[i], options).ToBytes()));
            }

            AssertSame(expected, ShotDetection.Merge(video, ranges, options));
        }

        // Given only one frame past the end, the overlap frame has no final score; given the frame before only (not
        // yadif's previous for it), the frames are numbered one off, which the merge's checks refuse.
        var short1 = ShotDetection.ScoreRange(video, Given(video, options, all, 0, 50).SkipLast(1), 0, options);
        var rest = ShotDetection.ScoreRange(video, Given(video, options, all, 50, Count), 50, options);
        Assert.Contains("without range 1's first frame", Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [short1, rest], options)).Message);
        var first = ShotDetection.ScoreRange(video, Given(video, options, all, 0, 50), 0, options);
        var late = ShotDetection.ScoreRange(video, RangeFrames(all, 50, Count), 50, options);
        Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [first, late], options));

        // Scored deinterlaced, merged without: refused.
        Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(video, [first, rest], options with { Deinterlace = DeinterlaceMode.Off }));
    }

    // EndFrame: one past the last final score. A range given RangeFrames' span for [first, end) ends at end + 1,
    // progressive or deinterlaced (whose last frame given isn't final); a stopped run resumes at EndFrame - 1, any frame
    // (as when every frame is a keyframe, MJPEG), and the resumed runs merge into Detect's result. A run resumed at
    // EndFrame is refused, and the refusal says where to start.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunsResumeAtTheEarlierRangesEndFrame(bool deinterlace)
    {
        var video = Video();
        var options = Options("adaptive", true);
        if (deinterlace)
            options = options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        var all = deinterlace ? Interlaced(video) : Frames(video);
        var expected = ShotDetection.Detect(video, all, options);
        Assert.Equal(deinterlace, expected.Video.Deinterlaced);

        var spanned = ShotDetection.ScoreRange(video, Given(video, options, all, 20, 50), 20, options);
        Assert.Equal(51, spanned.EndFrame);
        Assert.Equal(deinterlace ? 52 : 51, spanned.FirstFrame + spanned.FrameCount);
        Assert.Equal(51, RangeScores.FromBytes(spanned.ToBytes()).EndFrame);

        foreach (int[] stops in new[] { new[] { 39 }, [5, 6, 7], [31, 70], [3, 98] })
        {
            // Each run is given the frames from RangeFrames' start for its first frame to where it stops.
            var ranges = new List<RangeScores>();
            int first = 0;
            for (int i = 0; i <= stops.Length; i++)
            {
                int stop = i < stops.Length ? stops[i] : Count;
                long from = ShotDetection.RangeFrames(video, options, first, first + 1).From;
                var earlier = ranges.Select(r => RangeScores.FromBytes(r.ToBytes()));
                var scores = ShotDetection.ScoreRange(video, earlier, all.Skip((int)from).Take(stop - (int)from), first, options);
                Assert.Equal(stop - (deinterlace ? 1 : 0), scores.EndFrame); // the video's last range too: Merge keeps its last frame
                ranges.Add(scores);
                if (i < stops.Length)
                {
                    // One frame later than EndFrame - 1: no final score to compare, refused with where to start.
                    int late = scores.EndFrame;
                    long lateFrom = ShotDetection.RangeFrames(video, options, late, late + 1).From;
                    var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.ScoreRange(video, ranges, all.Skip((int)lateFrom).Take(Count - (int)lateFrom), late, options));
                    Assert.Contains($"start at frame {scores.EndFrame - 1} at the latest", e.Message);
                }

                first = scores.EndFrame - 1;
            }

            AssertSame(expected, ShotDetection.Merge(video, ranges, options));
        }
    }

    // Deinterlaced, a range from frame 0 tells the shots Detect tells, and runs that stop and continue from the earlier
    // ranges' scores tell the whole video's: a stopped run's last frame isn't final, so the next starts two frames
    // before where it stopped (its frames from four before), and the merge is the sequential result.
    [Theory]
    [InlineData("content", false)]
    [InlineData("adaptive", true)]
    [InlineData("several", true)]
    public async Task ADeinterlacedRangeTellsTheShotsDetectTells(string detector, bool stats)
    {
        var video = Video();
        var all = Interlaced(video);
        var options = Options(detector, stats) with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
        var whole = new Pulled();
        var expected = ShotDetection.Detect(video, whole.Counted(all), options with { OnShot = whole });
        var detected = new Pulled();
        await ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(detected.Counted(all)), options with { OnShot = detected });
        Assert.Equal(whole.Told, detected.Told);
        var told = new Pulled();
        ShotDetection.ScoreRange(video, told.Counted(all), 0, options with { OnShot = told });
        Assert.Equal(whole.Told, told.Told);

        foreach (int[] stops in new[] { new[] { 50 }, [35], [71], [30, 33, 60], [4, 7] })
        {
            var ranges = new List<RangeScores>();
            var resumed = new List<(Shot Shot, int Frame)>();
            int first = 0;
            for (int i = 0; i <= stops.Length; i++)
            {
                int end = i < stops.Length ? stops[i] : Count, from = Math.Max(0, first - 2);
                var run = new Pulled();
                var earlier = ranges.Select(r => RangeScores.FromBytes(r.ToBytes()));
                var frames = run.Counted(all.Skip(from).Take(end - from));
                var scores = i % 2 == 0
                    ? ShotDetection.ScoreRange(video, earlier, frames, first, options with { OnShot = run })
                    : await ShotDetection.ScoreRangeAsync(video, earlier, FrameSourceAsyncTests.Yielding(frames), first, options with { OnShot = run });
                resumed.AddRange(run.Told.Where(t => t.Pulled >= 0 || i == stops.Length).Select(t => (t.Shot, t.Pulled < 0 ? -1 : from + t.Pulled)));
                ranges.Add(scores);
                first = end - 2;
            }

            Assert.Equal(whole.Told, resumed);
            AssertSame(expected, ShotDetection.Merge(video, ranges, options));
        }
    }

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
        int[] starts = [0, 30, 70];
        var ranges = starts.Select((first, i) => Score(video, all, first, i + 1 < starts.Length ? starts[i + 1] : Count, options with { OnShot = null })).ToList();
        Assert.Empty(told.Values);
        var merged = ShotDetection.Merge(video, ranges, options);
        Assert.Equal(detected, told.Values);
        Assert.Equal(expected.Shots, told.Values);
        Assert.Equal(merged.Shots, told.Values);
    }

    /// <summary>
    /// A listener that notes with each shot how many frames had been pulled from <see cref="Counted"/>'s source, or -1
    /// once it had ended (the shots told when the frames stop).
    /// </summary>
    sealed class Pulled : IProgress<Shot>
    {
        int _frames;
        bool _ended;

        public List<(Shot Shot, int Pulled)> Told { get; } = [];

        public List<Shot> Shots => [.. Told.Select(t => t.Shot)];

        /// <summary>The shots told while frames were still coming: those the frames' end didn't decide.</summary>
        public List<Shot> Confirmed => [.. Told.Where(t => t.Pulled >= 0).Select(t => t.Shot)];

        public void Report(Shot value) => Told.Add((value, _ended ? -1 : _frames));

        public IEnumerable<YuvFrame> Counted(IEnumerable<YuvFrame> frames)
        {
            foreach (var frame in frames)
            {
                _frames++;
                yield return frame;
            }

            _ended = true;
        }
    }

    [Theory]
    [MemberData(nameof(Detectors))]
    public async Task ARangeFromFrameZeroTellsTheShotsDetectTells(string detector, bool stats)
    {
        var video = Video(timed: detector != "untimed");
        var all = Frames(video);
        var options = Options(detector, stats);
        foreach (int stop in new[] { Count, 50, 35 })
        {
            // The whole video, and a run stopped half-way: the shots, each told after the same frame as DetectAsync tells it.
            var frames = all.Take(stop).ToList();
            var detected = new Pulled();
            await ShotDetection.DetectAsync(video, FrameSourceAsyncTests.Yielding(detected.Counted(frames)), options with { OnShot = detected });
            Assert.Contains(detected.Told, t => t.Pulled < 0);
            var told = new Pulled();
            var scores = ShotDetection.ScoreRange(video, told.Counted(frames), 0, options with { OnShot = told });
            Assert.Equal(detected.Told, told.Told);
            told = new Pulled();
            var awaited = await ShotDetection.ScoreRangeAsync(video, FrameSourceAsyncTests.Yielding(told.Counted(frames)), 0, options with { OnShot = told });
            Assert.Equal(detected.Told, told.Told);
            told = new Pulled();
            var none = await ShotDetection.ScoreRangeAsync(video, [], FrameSourceAsyncTests.Yielding(told.Counted(frames)), 0, options with { OnShot = told });
            Assert.Equal(detected.Told, told.Told);

            // Telling changes no score.
            byte[] plain = ShotDetection.ScoreRange(video, frames, 0, options).ToBytes();
            Assert.Equal(plain, scores.ToBytes());
            Assert.Equal(plain, awaited.ToBytes());
            Assert.Equal(plain, none.ToBytes());
        }

        // A range from a later frame on its own tells nothing: Merge does.
        var later = new Pulled();
        ShotDetection.ScoreRange(video, RangeFrames(all, 50, Count), 50, options with { OnShot = later });
        Assert.Empty(later.Told);
    }

    [Theory]
    [MemberData(nameof(Detectors))]
    public async Task ARangeContinuingFromEarlierOnesTellsTheRestOfTheShots(string detector, bool stats)
    {
        var video = Video(timed: detector != "untimed");
        var all = Frames(video);
        var options = Options(detector, stats);
        var whole = new Pulled();
        var expected = ShotDetection.Detect(video, whole.Counted(all), options with { OnShot = whole });
        Assert.Equal(expected.Shots, whole.Shots);
        foreach (int[] stops in new[] { new[] { 50 }, [35], [71], [30, 31, 60], [2, 3] })
        {
            // Each run stops where its frames do, and the next continues from the last frame it scored (the overlap
            // frame), given the scores so far through their bytes: the shots told before each run's frames stopped, then
            // all of the last run's, are the whole video's, each told after the same frame as the whole run tells it.
            var ranges = new List<RangeScores>();
            var told = new List<(Shot Shot, int Frame)>();
            int first = 0;
            for (int i = 0; i <= stops.Length; i++)
            {
                int end = i < stops.Length ? stops[i] : Count, from = Math.Max(0, first - 1);
                var run = new Pulled();
                var earlier = ranges.Select(r => RangeScores.FromBytes(r.ToBytes()));
                var frames = run.Counted(all.Skip(from).Take(end - from));
                var scores = i % 2 == 0
                    ? ShotDetection.ScoreRange(video, earlier, frames, first, options with { OnShot = run })
                    : await ShotDetection.ScoreRangeAsync(video, earlier, FrameSourceAsyncTests.Yielding(frames), first, options with { OnShot = run });
                told.AddRange(run.Told.Where(t => t.Pulled >= 0 || i == stops.Length).Select(t => (t.Shot, t.Pulled < 0 ? -1 : from + t.Pulled)));
                ranges.Add(scores);
                first = end - 1;
            }

            Assert.Equal(whole.Told, told);
            AssertSame(expected, ShotDetection.Merge(video, ranges, options));
        }
    }

    [Fact]
    public void AContinuingRangeRefusesEarlierRangesThatDontLeadToIt()
    {
        var video = Video();
        var all = Frames(video);
        var options = Options("adaptive", true);
        RangeScores a = Score(video, all, 0, 50, options), b = Score(video, all, 50, 70, options);
        IEnumerable<YuvFrame> From(int first, IReadOnlyList<YuvFrame>? frames = null) => RangeFrames(frames ?? all, first, Count);
        var told = new Told<Shot>();
        var telling = options with { OnShot = told };
        Assert.Equal(ShotDetection.ScoreRange(video, From(60), 60, options).ToBytes(), ShotDetection.ScoreRange(video, [a, b], From(60), 60, telling).ToBytes());
        Assert.NotEmpty(told.Values);

        Assert.Contains("none were given", Refused(() => ShotDetection.ScoreRange(video, [], From(50), 50, telling)).Message);
        Assert.Contains("must start at frame 0, not 50", Refused(() => ShotDetection.ScoreRange(video, [b], From(60), 60, telling)).Message);
        Assert.Contains("starts at frame 40, not after the last earlier range's start, frame 50", Refused(() => ShotDetection.ScoreRange(video, [a, b], From(40), 40, telling)).Message);
        Assert.Contains("Frames 71 to 79 are in no range", Refused(() => ShotDetection.ScoreRange(video, [a, b], From(80), 80, telling)).Message);
        Assert.Contains("end at frame 70, without this range's first frame (71)", Refused(() => ShotDetection.ScoreRange(video, [a, b], From(71), 71, telling)).Message);
        var other = Score(video, all, 50, 70, options with { MinContentVal = 10 });
        Assert.Contains("Range 1 (from frame 50) was scored for another video or other options than this range's", Refused(() => ShotDetection.ScoreRange(video, [a, other], From(60), 60, telling)).Message);

        // A frame of both differs, with or without telling: its pts, or its score.
        var moved = all.Select((f, i) => i == 62 ? f with { Pts = f.Pts + 1 } : f).ToList();
        var changed = all.Select((f, i) => i == 65 ? f with { Y = f.Y.ToArray().Select(v => (byte)(255 - v)).ToArray() } : f).ToList();
        foreach (var o in new[] { options, telling })
        {
            Assert.Contains("Frame 62 is at pts 289 in the earlier ranges and 288 in this range", Refused(() => ShotDetection.ScoreRange(video, [a, Score(video, moved, 50, 70, options)], From(60), 60, o)).Message);
            Assert.Contains("Frame 65's score differs between the earlier ranges", Refused(() => ShotDetection.ScoreRange(video, [a, b], From(60, changed), 60, o)).Message);
        }

        Assert.Throws<ArgumentNullException>(() => ShotDetection.ScoreRange(video, null!, From(60), 60, options));
        Assert.Throws<ArgumentNullException>(() => ShotDetection.ScoreRange(video, [a, null!], From(60), 60, options));
        Assert.Throws<ArgumentNullException>(() => { _ = ShotDetection.ScoreRangeAsync(video, null!, FrameSourceAsyncTests.Yielding(From(60)), 60, options); }); // at once
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

        // The tone trim sets a time range: refused for ranges, even with the tone given.
        var toned = video with { ReferenceTone = [] };
        Assert.Contains("isn't available for ranges", Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(toned, all, 0, options with { TrimReferenceTone = true })).Message);
        Assert.Contains("isn't available for ranges", Assert.Throws<ArgumentException>(() => ShotDetection.Merge(toned, [ShotDetection.ScoreRange(toned, all, 0, options)], options with { TrimReferenceTone = true })).Message);

        // Deinterlacing needs yadif; ranges scored without it don't merge with it (their fingerprints differ).
        Assert.Throws<ArgumentException>(() => ShotDetection.ScoreRange(video, all, 0, options with { Deinterlace = DeinterlaceMode.On }));
        var interlaced = video with { FieldOrder = FrameReader.FieldOrder.TopFirst };
        Assert.Throws<ShotDetectionException>(() => ShotDetection.Merge(interlaced, [ShotDetection.ScoreRange(interlaced, all, 0, options)], options with { Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() }));
        Assert.Equal(ShotDetection.Merge(video, [ShotDetection.ScoreRange(video, all, 0, options)], options).Shots,
            ShotDetection.Merge(video, [ShotDetection.ScoreRange(video, all, 0, options with { Deinterlace = DeinterlaceMode.Auto })], options with { Deinterlace = DeinterlaceMode.Auto }).Shots);
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
        Assert.Contains("past the largest frame index", Message(new RangeScores("", int.MaxValue - 2, 2, [], [[1, 2]], null, []).ToBytes()));
        var huge = bytes[..^8].ToArray();
        int counts = 4 + 2 + 4 + BitConverter.ToInt32(huge, 6);
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, counts + 4);
        Assert.Contains("inconsistent", Message(Sealed(huge)));
    }

    [Fact]
    public void LoadScenesTakesTheVideosPropertiesAndAListAsText()
    {
        // No converter, no frames: the list's starts sorted, the first no cut, positions from FrameTimestamps (frame 3
        // is off the constant-rate grid, at pts 54 rather than 52), a list without the column refused.
        var video = Video() with { FrameTimestamps = [.. Enumerable.Range(0, Count).Select(i => 40L + (i * 4) + (i == 3 ? 2 : 0))] };
        string text = "Scene Number,Start Frame\n1,1\n2,31\n3,4\n";
        var r = ShotDetection.LoadScenes(video, new StringReader(text));
        Assert.Equal([3L, 30L], r.Cuts.Select(c => c.FrameNum));
        Assert.Equal([(0L, 3L), (3L, 30L), (30L, 100L)], r.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));
        Assert.Equal(Count, r.FrameCount);
        Assert.Equal(0.14, r.Video.Position(3).Seconds, 6);
        Assert.Null(r.VideoPath);
        Assert.Throws<ArgumentException>(() => ShotDetection.LoadScenes(video, new StringReader(text), "Start Timecode"));

        // Formats and sizes a converter wouldn't take are fine: nothing is converted.
        Assert.Equal(2, ShotDetection.LoadScenes(Video() with { PixelFormat = "yuv444p", Height = 47 }, new StringReader(text)).Cuts.Count);
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
        foreach (string name in ShotDetection.Fingerprinted.Except(["MinSceneDuration", "MinOutDuration", "Deinterlace", "Yadif"]))
            Assert.Contains("\n" + name + "=", "\n" + text);

        // Deinterlacing only when it deinterlaces: the ranges scored before it was taken keep their fingerprint.
        string deinterlaced = ShotDetection.Fingerprint(Video(), new DetectionOptions { Detectors = [new(DetectorKind.Content)], Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() }.Resolved());
        Assert.Contains("\nDeinterlace=", "\n" + deinterlaced);
        Assert.Contains("\nYadif=ShotDetector.FastYuv.Yadif\n", "\n" + deinterlaced);
        Assert.DoesNotContain("\nDeinterlace=", "\n" + text);
        Assert.Equal(text, ShotDetection.Fingerprint(Video(), new DetectionOptions { Detectors = [new(DetectorKind.Content)], Deinterlace = DeinterlaceMode.Auto, Yadif = new Yadif() }.Resolved()));

        // ChromaLocation only when set: the ranges scored before it existed keep their fingerprint.
        string sited = ShotDetection.Fingerprint(Video() with { ChromaLocation = "left" }, new DetectionOptions { Detectors = [new(DetectorKind.Content)] }.Resolved());

        // ReferenceTone isn't: ranges refuse the trim, so the tone changes no range's scores.
        foreach (var p in typeof(YuvVideo).GetProperties().Where(p => p.Name is not ("EqualityContract" or "ReferenceTone")))
            Assert.Contains("\n" + p.Name + "=", "\n" + sited);
        Assert.DoesNotContain("\nChromaLocation=", "\n" + text);
        foreach (var p in typeof(DetectorSettings).GetProperties().Where(p => p.Name is not ("EqualityContract" or "MinSceneDuration" or "MinOutDuration")))
            Assert.Contains("\nDetectors[0]." + p.Name + "=", "\n" + text);
        Assert.Equal(text, ShotDetection.Fingerprint(Video(), new DetectionOptions { Detectors = [new(DetectorKind.Content)] }.Resolved()));
        Assert.Equal(
            ShotDetection.Fingerprint(Video(), new DetectionOptions { MinSceneLength = "1.5s" }.Resolved()),
            ShotDetection.Fingerprint(Video(), new DetectionOptions { MinSceneDuration = TimeSpan.FromSeconds(1.5) }.Resolved()));
    }
}
