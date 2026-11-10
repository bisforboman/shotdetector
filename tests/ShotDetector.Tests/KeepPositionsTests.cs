using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// The clips on which scenedetect's load-scenes (<see cref="ShotDetection.LoadScenes(string, string, string, DetectionOptions?, CancellationToken)"/>)
/// doesn't give back a run's result (docs/decisions/2026-11-02-load-scenes-keep-positions.md), made with the executable:
/// a variable frame rate, an edit list that ends before the media, fragmented MP4 (no frame count), and fragmented MP4
/// whose moov lists only the first fragment's samples.
/// </summary>
public sealed class KeepPositionsClips : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-keep-").FullName;

    public KeepPositionsClips()
    {
        string three = Path.Combine(_dir, "three.mp4");
        Ffmpeg("-f",
            "lavfi",
            "-i",
            "testsrc2=s=320x240:r=25:d=2",
            "-f",
            "lavfi",
            "-i",
            "smptebars=s=320x240:r=25:d=2",
            "-f",
            "lavfi",
            "-i",
            "mandelbrot=s=320x240:r=25",
            "-filter_complex",
            "[2]trim=duration=2,setpts=PTS-STARTPTS[m];[0][1][m]concat=n=3:v=1:a=0,format=yuv420p",
            "-r",
            "25",
            "-c:v",
            "mpeg4",
            "-q:v",
            "3",
            three);
        Paths["three"] = three;

        // Frames 0-59 at 25 fps, the rest at 12.5: cuts at frames 50 (2.000 s) and 100 (5.600 s).
        Paths["vfr"] = Path.Combine(_dir, "vfr.mp4");
        Ffmpeg("-i", three, "-vf", "setpts='if(lt(N,60),N,60+(N-60)*2)/25/TB'", "-fps_mode", "vfr", "-c:v", "mpeg4", "-q:v", "3", Paths["vfr"]);

        // The edit list's one entry shortened to 3 s: 75 of the 150 frames play.
        string edits = Path.Combine(_dir, "edits.mp4");
        Ffmpeg("-i", three, "-c", "copy", "-use_editlist", "1", edits);
        var bytes = File.ReadAllBytes(edits);
        int elst = bytes.AsSpan().IndexOf("elst"u8);
        Assert.True(elst > 0, "no edit list written");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(elst + 12), 3000); // segment_duration, ms (version 0)
        Paths["edit-end"] = Path.Combine(_dir, "edit-end.mp4");
        File.WriteAllBytes(Paths["edit-end"], bytes);

        // With audio past the video's end, as a film's often is: no frame count, so the duration × rate counts 160.
        Paths["fragmented"] = Path.Combine(_dir, "fragmented.mp4");
        Ffmpeg("-i", three, "-f", "lavfi", "-i", "sine=d=6.4", "-c:v", "copy", "-c:a", "aac", "-movflags", "frag_keyframe+empty_moov", Paths["fragmented"]);

        // A keyframe every 38 frames, the first fragment's samples in the moov: nb_frames 38 of 150.
        Paths["fragmented-moov"] = Path.Combine(_dir, "fragmented-moov.mp4");
        Ffmpeg("-i", three, "-c:v", "mpeg4", "-q:v", "3", "-g", "38", "-movflags", "frag_keyframe", Paths["fragmented-moov"]);
    }

    public Dictionary<string, string> Paths { get; } = [];

    static void Ffmpeg(params string[] args) => VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. args], default);

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

/// <summary>
/// <see cref="ShotDetection.LoadScenes(DetectionResult, IEnumerable{int}, DetectionOptions?)"/>: an edited cut list at a
/// run's own frame times and length. A run's cuts given back are its result, shots, scene list and exports, on every clip
/// where scenedetect's load-scenes isn't; an edited list's cuts are where the run had those frames.
/// </summary>
public class KeepPositionsTests(KeepPositionsClips clips) : IClassFixture<KeepPositionsClips>
{
    static readonly DetectionOptions Content = new() { Detector = DetectorKind.Content };

    // scenedetect 0.7.3 (#561): a run that reads to the end ends its last shot a frame after the last frame's own time.
    // 0.7.2 read the position after the failed read, OpenCV's 0, so the frame-number fallback: 150 frames at the
    // average rate, not the last frame's 9.52 s plus one frame (frames 60 on are 12.5 fps).
    [Fact]
    public void TheLastShotEndsAfterTheLastFramesOwnTimeFrom073()
    {
        var run = ShotDetection.Detect(clips.Paths["vfr"], Content);
        var last = run.Video.Position(run.FrameCount - 1);
        Assert.Equal(150, run.FrameCount);
        Assert.Equal(9.52, last.Seconds, 6);
        Assert.Equal(last.PlusFrames(1).Seconds, run.Shots[^1].End.Seconds);
        Assert.Equal(last.PlusFrames(1).Timecode(), run.Shots[^1].End.Timecode());

        var before = ShotDetection.Detect(clips.Paths["vfr"], Content with { Compatibility = PySceneDetectVersion.V0_7_2 });
        var fps = before.Video.Reader.Fps;
        Assert.Equal(150.0 * fps.Den / fps.Num, before.Shots[^1].End.Seconds, 6);
        Assert.NotEqual(run.Shots[^1].End.Timecode(), before.Shots[^1].End.Timecode());
        Assert.Equal(run.Cuts.Select(c => c.Seconds), before.Cuts.Select(c => c.Seconds));
    }

    // A time range that stops before the end ends at the last frame read, in both releases.
    [Fact]
    public void ARunStoppedBeforeTheEndEndsAtItsLastFrameInBothReleases()
    {
        var o = Content with { EndTime = "6s" };
        var now = ShotDetection.Detect(clips.Paths["vfr"], o);
        var before = ShotDetection.Detect(clips.Paths["vfr"], o with { Compatibility = PySceneDetectVersion.V0_7_2 });
        Assert.Equal(now.Shots[^1].End.Seconds, before.Shots[^1].End.Seconds);
    }

    /// <summary>What a result gives: its shots and cuts as positions, its scene list, and the exports that need no file.</summary>
    static string[] Outputs(DetectionResult r) =>
    [
        string.Join(';', r.Shots.Select(s => $"{s.Number} {s.Start.Timecode()} {s.Start.SecondsText()} {s.Start.FrameNum} {s.End.Timecode()} {s.End.SecondsText()} {s.End.FrameNum}")),
        string.Join(';', r.Cuts.Select(c => $"{c.Timecode()} {c.SecondsText()} {c.FrameNum}")),
        Shots.Csv(r.Shots, cuts: r.Cuts),
        Shots.Json(r.Shots),
        Shots.Html(r.Shots),
        Timeline.Edl(r),
        Timeline.Qp(r),
        Timeline.ShotChanges(r),

        r.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        r.MinSceneLengthFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
        .. r.VideoPath is null ? [] : new[] { Timeline.Fcpx(r), Timeline.Fcp7(r), Timeline.Otio(r) },
    ];

    [Theory]
    [InlineData("three")]
    [InlineData("vfr")]
    [InlineData("edit-end")]
    [InlineData("fragmented")]
    [InlineData("fragmented-moov")]
    public void ARunsOwnCutsGiveItsResult(string clip)
    {
        string path = clips.Paths[clip];
        foreach (var options in new[]
        {
            Content,
            new DetectionOptions { CollectStats = true },
            Content with { MinSceneLength = "30", DropShortScenes = true },
            Content with { MinSceneLength = "30", MergeLastScene = true },
            Content with { StartTime = "1s", EndTime = "5s" },
        })
        {
            var run = ShotDetection.Detect(path, options);
            Assert.True(run.Cuts.Count > 0, $"{clip}: no cuts with {options}");
            var back = ShotDetection.LoadScenes(run, run.CutFrames, options with { StartTime = null, EndTime = null });
            Assert.Equal(Outputs(run), Outputs(back));
            Assert.Same(run.Stats, back.Stats);

            // In any order, repeated.
            back = ShotDetection.LoadScenes(run, [.. run.CutFrames.Reverse(), .. run.CutFrames], options with { StartTime = null, EndTime = null });
            Assert.Equal(Outputs(run), Outputs(back));
        }
    }

    [Fact]
    public void TheClipsAreTheShapesLoadScenesDoesntGiveBack()
    {
        // What makes these clips worth testing: scenedetect's load-scenes of the run's own list differs on each.
        foreach (string clip in new[] { "vfr", "edit-end", "fragmented", "fragmented-moov" })
        {
            string path = clips.Paths[clip];
            var run = ShotDetection.Detect(path, Content);
            string csv = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.csv");
            try
            {
                File.WriteAllText(csv, Shots.Csv(run.Shots, cuts: run.Cuts));
                Assert.NotEqual(Outputs(run)[..3], Outputs(ShotDetection.LoadScenes(path, csv, options: Content))[..3]);
            }
            finally
            {
                File.Delete(csv);
            }
        }
    }

    [Fact]
    public void VariableFrameRateCutsStayAtTheirFramesTimes()
    {
        var run = ShotDetection.Detect(clips.Paths["vfr"], Content);
        Assert.Equal(["2.000", "5.600"], run.Cuts.Select(c => c.SecondsText()));
        Assert.Equal([50, 100], run.CutFrames);
        Assert.NotEqual([50L, 100L], run.Cuts.Select(c => c.FrameNum)); // the average rate's frame numbers aren't the frames

        // The first cut moved to frame 40, one added at 120, the one at 100 removed: each at its frame's time, the
        // end the run's.
        var edited = ShotDetection.LoadScenes(run, [120, 40], Content);
        Assert.Equal(["1.600", "7.200"], edited.Cuts.Select(c => c.SecondsText()));
        Assert.Equal([40, 120], edited.CutFrames);
        Assert.Equal([run.Video.Position(40), run.Video.Position(120)], edited.Cuts);
        Assert.Equal(run.Shots[0].Start, edited.Shots[0].Start);
        Assert.Equal(run.Shots[^1].End, edited.Shots[^1].End);
        Assert.Equal(["0.000-1.600", "1.600-7.200", $"7.200-{run.Shots[^1].End.SecondsText()}"], edited.Shots.Select(s => $"{s.Start.SecondsText()}-{s.End.SecondsText()}"));

        // A frame of the run cut where it cut gives the run's cut, not a new one: the cut at 50 kept, 100 as before.
        Assert.Equal(Outputs(run), Outputs(ShotDetection.LoadScenes(run, [100, 50], Content)));
    }

    [Theory]
    [InlineData("edit-end", 75)]
    [InlineData("fragmented", 150)]
    [InlineData("fragmented-moov", 150)]
    public void TheLastShotEndsWhereTheRunsDid(string clip, int played)
    {
        var run = ShotDetection.Detect(clips.Paths[clip], Content);
        Assert.Equal(played, run.FrameCount);
        var edited = ShotDetection.LoadScenes(run, [10, 70, 140], Content);

        // Frames the run didn't read are left out; the rest are kept, also past the moov's 38 samples.
        Assert.Equal(new[] { 10, 70, 140 }.Where(f => f < played), edited.CutFrames);
        Assert.Equal(run.Shots[^1].End, edited.Shots[^1].End);
        Assert.Equal(edited.Cuts.Count + 1, edited.Shots.Count);
    }

    [Fact]
    public void FramesFromTheCallerKeepTheirTimestamps()
    {
        // Frames given with variable timestamps (each 4 or, from frame 50, 8 hundredths apart): a cut at its frame's time.
        var video = new YuvVideo { Width = 64, Height = 48, FrameRate = new(25, 1), TimeBase = new(1, 100), StartPts = 40, FrameCount = 100, Codec = "h264" };
        long[] pts = [.. Enumerable.Range(0, 100).Select(i => 40L + (i < 50 ? i * 4 : 200 + ((i - 50) * 8)))];
        video = video with { FrameTimestamps = pts };
        var frames = FrameRangeTests.Frames(video);
        for (int i = 0; i < frames.Count; i++)
            frames[i] = frames[i] with { Pts = pts[i] };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), Detector = DetectorKind.Content, MinSceneLength = "5" };
        var run = ShotDetection.Detect(video, frames, options);
        Assert.NotEmpty(run.Cuts);
        Assert.Equal(Outputs(run), Outputs(ShotDetection.LoadScenes(run, run.CutFrames, options)));

        var edited = ShotDetection.LoadScenes(run, [60], options);
        Assert.Equal("2.800", edited.Cuts.Single().SecondsText());
        Assert.Equal(run.Shots[^1].End, edited.Shots[^1].End);
    }

    [Fact]
    public void FadeCutsMapToTheirFrameAndBack()
    {
        // ThresholdDetector's cuts are frame numbers, not a frame's time: given back, the run's cut is kept as it was.
        var video = new YuvVideo { Width = 64, Height = 48, FrameRate = new(25, 1), TimeBase = new(1, 100), StartPts = 40, FrameCount = 100, Codec = "h264" };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), Detector = DetectorKind.Threshold, MinSceneLength = "5" };
        var run = ShotDetection.Detect(video, FrameRangeTests.Frames(video), options);
        Assert.NotEmpty(run.Cuts);
        Assert.Equal(Outputs(run), Outputs(ShotDetection.LoadScenes(run, run.CutFrames, options)));
        Assert.Equal(run.CutFrames, run.Cuts.Select(run.FrameOf));
    }

    [Fact]
    public void ItNeedsADetectionsResultAndKeepsItsRange()
    {
        var run = ShotDetection.Detect(clips.Paths["three"], Content);
        Assert.Throws<ArgumentException>(() => ShotDetection.LoadScenes(run, [50], Content with { StartTime = "1s" }));
        Assert.Throws<ArgumentException>(() => ShotDetection.LoadScenes(run, [50], Content with { EndTime = "5s" }));
        Assert.Throws<ArgumentException>(() => ShotDetection.LoadScenes(run, [50], Content with { RangeDuration = "5s" }));

        string csv = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(csv, Shots.Csv(run.Shots));
            var loaded = ShotDetection.LoadScenes(clips.Paths["three"], csv);
            Assert.Throws<ArgumentException>(() => ShotDetection.LoadScenes(loaded, [50]));
            Assert.Throws<InvalidOperationException>(() => loaded.CutFrames);
        }
        finally
        {
            File.Delete(csv);
        }

        // A run from 1 s to 5 s: cuts outside it are left out, the shots stay within it.
        var ranged = ShotDetection.Detect(clips.Paths["three"], Content with { StartTime = "1s", EndTime = "5s" });
        var edited = ShotDetection.LoadScenes(ranged, [10, 30, 100, 140], Content);
        Assert.Equal([30, 100], edited.CutFrames);
        Assert.Equal(ranged.Shots[0].Start, edited.Shots[0].Start);
        Assert.Equal(ranged.Shots[^1].End, edited.Shots[^1].End);

        // No cuts: one shot, the run's whole range.
        var none = ShotDetection.LoadScenes(run, [], Content);
        Assert.Equal([(run.Shots[0].Start, run.Shots[^1].End)], none.Shots.Select(s => (s.Start, s.End)));
    }
}
