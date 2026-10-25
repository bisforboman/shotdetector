using System.Globalization;
using System.Text;

namespace ShotDetector;

/// <summary>
/// Frame-source detection in ranges, for a host that splits a long video across runtimes with no shared memory (browser
/// Web Workers): <see cref="ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>
/// scores one range of frames, <see cref="Merge"/> runs the decisions over every range's scores, in frame order, as a
/// chunked file's (docs/parallel-detection.md, "Frame-source ranges (for hosts)").
/// </summary>
public static partial class ShotDetection
{
    /// <summary>
    /// Scores one range of the frames of <paramref name="video"/>, for <see cref="Merge"/>: each detector's per-frame
    /// score and stats, the expensive half of a detection (conversion, resize, the detectors' metrics), with nothing
    /// decided yet. Several ranges, each scored on its own runtime (a browser Web Worker, no shared memory), merge into
    /// exactly the result <see cref="Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/> gives
    /// for the whole video: shots, cuts, stats and their CSVs, byte for byte. Nothing here loads FFmpeg.
    /// </summary>
    /// <param name="video">The video's properties, the same for every range (<see cref="Merge"/> refuses ranges of another).</param>
    /// <param name="frames">
    /// The range's frames in presentation order, starting one frame early: frame <paramref name="firstFrame"/> - 1 (the
    /// previous frame, which each score compares with), unless <paramref name="firstFrame"/> is 0; then every frame to one
    /// frame past the range's end, the first frame of the next range (its overlap frame), or to the video's end for the
    /// last range. The range ends where the frames stop: <see cref="Merge"/> takes the frames from the next range's start
    /// on as overlap. They must be exactly the frames a sequential decode of the video gives at those positions. A real
    /// decoder starts at a keyframe, so pick each range's start there: give the keyframe as the first frame (and pass
    /// <paramref name="firstFrame"/> one past it), or decode from an earlier keyframe and drop the frames before
    /// <paramref name="firstFrame"/> - 1.
    /// </param>
    /// <param name="firstFrame">The 0-based index in the video of the range's first frame.</param>
    /// <param name="options">The detection's settings, the same for every range and for <see cref="Merge"/>; see the remarks for which apply.</param>
    /// <param name="cancellationToken">Checked before each frame is pulled.</param>
    /// <returns>The range's scores; <see cref="RangeScores.ToBytes"/> carries them to where the ranges are merged.</returns>
    /// <exception cref="ArgumentException">Invalid or unavailable options, an unsupported video, or a frame's plane smaller than the frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstFrame"/> is negative or too large.</exception>
    /// <exception cref="ShotDetectionException">The frames end before frame <paramref name="firstFrame"/> (<see cref="ShotDetectionError.InvalidInput"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// The options of <see cref="Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/> apply, with
    /// its refusals, except those a range can't honour on its own, which are refused too (an <see cref="ArgumentException"/>):
    /// the time range (<see cref="DetectionOptions.StartTime"/>, <see cref="DetectionOptions.EndTime"/>,
    /// <see cref="DetectionOptions.RangeDuration"/> and their typed forms), <see cref="DetectionOptions.FrameSkip"/> (its
    /// phase runs across the ranges) and <see cref="DetectionMode.Parallel"/> (the host does the chunking).
    /// <see cref="DetectionOptions.Progress"/> is told the range's frames (<see cref="DetectionProgress.FramesProcessed"/>
    /// counts those from <paramref name="firstFrame"/> on; <see cref="DetectionProgress.ExpectedFrames"/> is 0, as the range
    /// ends where the frames stop), the last report once they have. <see cref="DetectionOptions.OnShot"/> isn't: shots are
    /// decided in <see cref="Merge"/>. Every frame's position comes from its pts (<see cref="YuvFrame.Pts"/>), and
    /// <see cref="Merge"/> checks every range's frames against frame 0's on the constant-rate grid, which also catches a
    /// wrong <paramref name="firstFrame"/>.
    /// </remarks>
    public static RangeScores ScoreRange(YuvVideo video,
        IEnumerable<YuvFrame> frames,
        long firstFrame,
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(frames);
        CheckFirstFrame(firstFrame);
        using var activity = Tracing.StartActivity("ShotDetection.ScoreRange");
        try
        {
            using var e = frames.GetEnumerator();
            var feed = new FrameFeed();
            RangeScores result;
            using (var steps = RangeSteps(video, feed, (int)firstFrame, options, cancellationToken).GetEnumerator())
            {
                while (!Stepped(steps, out result))
                {
                    if (feed.NeedsFrame)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // before each range frame is pulled
                        if (e.MoveNext())
                            feed.Put(e.Current);
                        else
                            feed.End();
                    }
                }
            }

            return result;
        }
        catch (Exception x)
        {
            Failed(activity, x);
            throw;
        }
    }

    /// <summary>
    /// <see cref="ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/> on frames from
    /// an asynchronous source, as <see cref="DetectAsync(YuvVideo, IAsyncEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>
    /// is the synchronous Detect's: the same frames, options, refusals and scores, the frames pulled one at a time on the
    /// caller's own awaits (no other thread), so a single-threaded host's event loop runs whenever the source yields.
    /// </summary>
    /// <param name="video">The video's properties, the same for every range.</param>
    /// <param name="frames">The range's frames, as for the synchronous overload; enumerated once, with
    /// <paramref name="cancellationToken"/>, and disposed when the scoring ends, fails or is cancelled.</param>
    /// <param name="firstFrame">The 0-based index in the video of the range's first frame.</param>
    /// <param name="options">The detection's settings, as for the synchronous overload.</param>
    /// <param name="cancellationToken">Checked before each frame is awaited.</param>
    /// <returns>The range's scores.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="video"/> or <paramref name="frames"/> is null (thrown at once).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstFrame"/> is negative or too large (thrown at once).</exception>
    /// <exception cref="ArgumentException">Invalid or unavailable options, an unsupported video, or a frame's plane smaller than the frame (in the task).</exception>
    /// <exception cref="ShotDetectionException">The frames end before frame <paramref name="firstFrame"/> (in the task).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (in the task).</exception>
    public static Task<RangeScores> ScoreRangeAsync(YuvVideo video,
        IAsyncEnumerable<YuvFrame> frames,
        long firstFrame,
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(frames);
        CheckFirstFrame(firstFrame);
        return ScoreRangeAwaited(video, frames, (int)firstFrame, options, cancellationToken);
    }

    static async Task<RangeScores> ScoreRangeAwaited(YuvVideo video, IAsyncEnumerable<YuvFrame> frames, int firstFrame, DetectionOptions? options, CancellationToken cancellationToken)
    {
        using var activity = Tracing.StartActivity("ShotDetection.ScoreRange");
        try
        {
            var feed = new FrameFeed();
            RangeScores result;
            var source = frames.GetAsyncEnumerator(cancellationToken);
            await using (source.ConfigureAwait(false))
            {
                using var steps = RangeSteps(video, feed, firstFrame, options, cancellationToken).GetEnumerator();
                while (!Stepped(steps, out result))
                {
                    if (feed.NeedsFrame)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // before each range frame is awaited
                        if (await source.MoveNextAsync().ConfigureAwait(false))
                            feed.Put(source.Current);
                        else
                            feed.End();
                    }
                }
            }

            return result;
        }
        catch (Exception x)
        {
            Failed(activity, x);
            throw;
        }
    }

    static void CheckFirstFrame(long firstFrame)
    {
        if (firstFrame is < 0 or >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(firstFrame), firstFrame, "A range's first frame must be at least 0 and below 2^31 - 1.");
    }

    /// <summary>
    /// The scoring of a range, as steps (<see cref="Steps"/>' way): null where it takes the next frame from the caller (the
    /// driver puts it in the <see cref="FrameFeed"/> first), then the scores.
    /// </summary>
    static IEnumerable<RangeScores?> RangeSteps(YuvVideo video, FrameFeed feed, int firstFrame, DetectionOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var o = (options ?? new DetectionOptions()).Resolved();
        CheckOptions(o, null);
        CheckRangeOptions(o);
        int from = Math.Max(0, firstFrame - 1);
        var reader = new VideoReader(video, feed, o, from);
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, reader.Fps);
        var settings = Settings(o);
        var stats = o.CollectStats ? new Stats() : null;
        var detectors = settings.Select(st => Build(st, o, reader, minSceneLen, stats)).ToList();
        var metrics = settings.Select(_ => new List<double?>()).ToArray();
        DetectionProgress Progress(int done, int expected) => new() { FramesProcessed = done, ExpectedFrames = expected };
        o.Progress?.Report(Progress(0, 0));
        long lastReport = Environment.TickCount64;
        int k = from;
        yield return null; // the first frame is taken next
        foreach (var frame in reader.Frames(from, null, cancellationToken))
        {
            for (int d = 0; d < detectors.Count; d++)
            {
                // The frame before the range only gives the scores their previous frame, as a chunk's (ScoreChunked).
                var m = detectors[d].Metric(k, frame);
                if (k >= firstFrame)
                    metrics[d].Add(m);
            }

            k++;
            if (o.Progress is not null && o.ProgressStep is null && Environment.TickCount64 - lastReport >= 100)
            {
                o.Progress.Report(Progress(k - firstFrame, 0));
                lastReport = Environment.TickCount64;
            }

            yield return null; // the next frame is taken next
        }

        if (k <= firstFrame && firstFrame > 0)
        {
            throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                string.Create(CultureInfo.InvariantCulture,
                    $"The range from frame {firstFrame} was given {k - from} frames: it needs frame {firstFrame - 1} (the previous frame, which the scores compare with) and at least frame {firstFrame}."));
        }

        int scored = k - firstFrame;
        o.Progress?.Report(Progress(scored, scored));
        yield return new RangeScores(Fingerprint(video, o),
            firstFrame,
            scored,
            reader.FramesTimed ? [.. reader.FramePts] : [],
            [.. metrics.Select(m => m.ToArray())],
            stats?.Entries(firstFrame, int.MaxValue).ToArray(),
            []);
    }

    /// <summary>The options a range can't honour on its own (an <see cref="ArgumentException"/>), for <see cref="ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/> and <see cref="Merge"/>.</summary>
    static void CheckRangeOptions(DetectionOptions o)
    {
        if (o.StartTime is not null || o.EndTime is not null || o.RangeDuration is not null)
            throw new ArgumentException("A time range (StartTime, EndTime, Duration) isn't available for ranges: score the whole video, from frame 0, in ranges.");
        if (o.FrameSkip != 0)
            throw new ArgumentException("FrameSkip isn't available for ranges: which frames it analyses runs across them.");
        if (o.Mode == DetectionMode.Parallel)
            throw new ArgumentException("DetectionMode.Parallel isn't available for ranges: the host splits the video; leave Mode at Auto or Sequential.");
    }

    /// <summary>
    /// Merges the scores of a video's ranges (<see cref="ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>)
    /// into the detection's result: the decisions (thresholds, minimum lengths, the flash filter, adaptive's window,
    /// threshold's fades) run once over every score in frame order, then MergeLastScene and DropShortScenes, as for a
    /// chunked file. The result is the one
    /// <see cref="Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/> gives for the whole video:
    /// shots, cuts, stats and their CSVs, the frame count and every position, byte for byte. Nothing here loads FFmpeg or
    /// reads a frame.
    /// </summary>
    /// <param name="video">The video's properties, as every range was given them.</param>
    /// <param name="ranges">Every range's scores, in order: the first from frame 0, each next one starting at or before
    /// the frame after the previous one's last (the previous one's overlap frames), the last to the video's end.</param>
    /// <param name="options">The detection's settings, as every range was scored with (callbacks aside); see the remarks.</param>
    /// <returns>The detection's result, as Detect's (one chunk; no <see cref="DetectionResult.VideoPath"/>).</returns>
    /// <exception cref="ArgumentException">Invalid or unavailable options (as for ScoreRange), or no ranges.</exception>
    /// <exception cref="ShotDetectionException">The ranges don't make up the video's frames (<see cref="ShotDetectionError.InvalidInput"/>,
    /// the reason names the range and the frame): another video or options than this merge's, not from frame 0, out of
    /// order, a gap or a missing overlap frame, an overlap frame that differs, or frames off the constant-rate grid.</exception>
    /// <remarks>
    /// <para>
    /// The overlap check: each range's frames from the next range's first frame on are the next range's too, and their
    /// scores (and pts) must be equal in both. The next range decoded them from its own start, with its frame before as
    /// the previous frame, so a decoder that gives other frames there than a sequential decode (a range not starting at a
    /// keyframe, an open GOP's leading frames, a decoder that depends on earlier frames) shows up there. Without a sequential
    /// pass to fall back on, as a chunked file has, a mismatch is an error. With <see cref="YuvVideo.TimeBase"/> set, every
    /// frame's pts must also lie on the constant-rate grid from frame 0's, as for Detect, which catches frames given under
    /// another <c>firstFrame</c> than theirs; without it frames are identified by <c>firstFrame</c> alone.
    /// </para>
    /// <para>
    /// <see cref="DetectionOptions.OnShot"/> is told the shots here, as Detect tells them (as each cut is confirmed with
    /// one detector and neither DropShortScenes nor MergeLastScene, else all at the end), all before Merge returns.
    /// <see cref="DetectionOptions.Progress"/> isn't told (ScoreRange reports each range's frames), and
    /// <see cref="DetectionOptions.Warnings"/> is told the ranges' warnings, which the result lists once each.
    /// </para>
    /// </remarks>
    public static DetectionResult Merge(YuvVideo video, IEnumerable<RangeScores> ranges, DetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(ranges);
        using var activity = Tracing.StartActivity("ShotDetection.Merge");
        try
        {
            var list = ranges.ToList();
            if (list.Count == 0)
                throw new ArgumentException("Merge needs at least one range.", nameof(ranges));
            var o = (options ?? new DetectionOptions()).Resolved();
            CheckOptions(o, null);
            CheckRangeOptions(o);
            string fingerprint = Fingerprint(video, o);
            for (int i = 0; i < list.Count; i++)
            {
                ArgumentNullException.ThrowIfNull(list[i], nameof(ranges));
                if (list[i].Fingerprint != fingerprint)
                {
                    throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                        $"Range {i} (from frame {list[i].FirstFrame}) was scored for another video or other options than this merge's: {FirstDifference(list[i].Fingerprint, fingerprint)}.");
                }
            }

            var (pts, scores) = Joined(list);
            if (pts.Length > 0)
            {
                // The sequential run's check of every frame, from frame 0's pts; at a range's first frames, says which.
                var reader = new VideoReader(video, Enumerable.Empty<YuvFrame>().GetEnumerator(), o);
                for (int k = 1; k < pts.Length; k++)
                {
                    int owner = list.FindLastIndex(r => r.FirstFrame <= k);
                    string where = string.Create(CultureInfo.InvariantCulture,
                        $" (frame {k} of the range from frame {list[owner].FirstFrame}: are its frames the ones from that frame?)");
                    VideoReader.CheckConstantRate(pts[0], pts[k], k, reader.FramesTimeBase, reader.FramesRate, where);
                }
            }

            var warnings = list.SelectMany(r => r.Warnings).Distinct().ToList();

            // The decisions as Detect makes them: Steps with the scores in place of the frames (the chunked file's path).
            var steps = Steps(null,
                null,
                (options ?? new DetectionOptions()) with { Progress = null },
                null,
                CancellationToken.None,
                ro =>
                {
                    var loaded = new VideoReader(video, Enumerable.Empty<YuvFrame>().GetEnumerator(), ro);
                    loaded.LoadFrames(pts, scores.Frames);
                    return loaded;
                },
                new Prescored(scores, warnings));
            DetectionResult result;
            using (var e = steps.GetEnumerator())
            {
                while (!Stepped(e, out result))
                {
                }
            }

            Traced(activity, result);
            return result;
        }
        catch (Exception x)
        {
            Failed(activity, x);
            throw;
        }
    }

    /// <summary>
    /// The ranges joined: every frame's pts (none when untimed) and every detector's scores, each frame from the range it
    /// belongs to (the last that starts at or before it), with the stats each range's frames give. Checks that they start at
    /// frame 0 and follow each other with an overlap, and that the overlap frames' scores and pts are the same in both.
    /// </summary>
    static (long[] Pts, (List<double?>[] Metrics, int Frames, (Stats? Stats, int From, int To)[] Stats) Scores) Joined(List<RangeScores> list)
    {
        if (list[0].FirstFrame != 0)
            throw Invalid($"The first range must start at frame 0, not {list[0].FirstFrame}.");
        int detectors = list[0].Metrics.Length;
        bool timed = list.Any(r => r.Pts.Length > 0);
        for (int i = 1; i < list.Count; i++)
        {
            RangeScores a = list[i - 1], b = list[i];
            if (b.FirstFrame <= a.FirstFrame)
                throw Invalid($"The ranges are out of order: range {i} starts at frame {b.FirstFrame}, not after range {i - 1}'s start, frame {a.FirstFrame}.");
            long aEnd = (long)a.FirstFrame + a.FrameCount, bEnd = (long)b.FirstFrame + b.FrameCount; // exclusive
            if (aEnd < b.FirstFrame)
                throw Invalid($"Frames {aEnd} to {b.FirstFrame - 1} are in no range: range {i - 1} ends at frame {aEnd - 1}, range {i} starts at frame {b.FirstFrame}.");
            if (aEnd == b.FirstFrame)
                throw Invalid($"Range {i - 1} ends at frame {aEnd - 1}, without range {i}'s first frame ({b.FirstFrame}) to compare: a range's frames go to one frame past its end.");
            for (long k = b.FirstFrame; k < Math.Min(aEnd, bEnd); k++)
            {
                for (int d = 0; d < detectors; d++)
                {
                    double? x = a.Metrics[d][k - a.FirstFrame], y = b.Metrics[d][k - b.FirstFrame];
                    if (!Equals(x, y))
                    {
                        throw Invalid($"Frame {k}'s score differs between range {i - 1} ({Score(x)}) and range {i} ({Score(y)}, detector {d}): range {i}'s frames aren't "
                            + "the ones a sequential decode gives (a range not starting at a keyframe, an open GOP's leading frames, a decoder that depends on earlier frames).");
                    }
                }
            }

            if (timed)
            {
                // b's frames from its frame before (b.FirstFrame - 1), a's from its own (a.FirstFrame - 1, or 0).
                int aFrom = Math.Max(0, a.FirstFrame - 1), bFrom = b.FirstFrame - 1;
                for (long k = bFrom; k < Math.Min(aEnd, bEnd); k++)
                {
                    long? x = k - aFrom < a.Pts.Length ? a.Pts[k - aFrom] : null, y = k - bFrom < b.Pts.Length ? b.Pts[k - bFrom] : null;
                    if (x != y)
                        throw Invalid($"Frame {k} is at pts {x?.ToString(CultureInfo.InvariantCulture) ?? "none"} in range {i - 1} and {y?.ToString(CultureInfo.InvariantCulture) ?? "none"} in range {i}: not the same frame.");
                }
            }
        }

        var last = list[^1];
        int total = checked(last.FirstFrame + last.FrameCount);
        var metrics = Enumerable.Range(0, detectors).Select(_ => new List<double?>(total)).ToArray();
        var pts = new List<long>(timed ? total : 0);
        var stats = new (Stats? Stats, int From, int To)[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            int to = i + 1 < list.Count ? list[i + 1].FirstFrame : total;
            for (int d = 0; d < detectors; d++)
                metrics[d].AddRange(r.Metrics[d].Take(to - r.FirstFrame));
            if (timed)
            {
                int skip = r.FirstFrame > 0 ? 1 : 0; // the frame before
                if (r.Pts.Length < skip + (to - r.FirstFrame))
                    throw Invalid($"Range {i} (from frame {r.FirstFrame}) has no pts for some of its frames: every range of a timed video carries its frames' pts.");
                pts.AddRange(r.Pts.Skip(skip).Take(to - r.FirstFrame));
            }

            stats[i] = (r.Stats is { } entries ? StatsOf(entries) : null, r.FirstFrame, i + 1 < list.Count ? to : int.MaxValue);
        }

        return ([.. pts], (metrics, total, stats));

        static string Score(double? s) => s is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "none";
        static ShotDetectionException Invalid(string message) => new(ShotDetectionError.InvalidInput, message);
    }

    static Stats StatsOf((int Frame, string Key, double Value)[] entries)
    {
        var stats = new Stats();
        foreach (var (frame, key, value) in entries)
            stats.Set(frame, key, value);
        return stats;
    }

    /// <summary>The first line in which two fingerprints differ, both ways, for the merge's refusal.</summary>
    static string FirstDifference(string range, string merge)
    {
        var a = range.Split('\n');
        var b = merge.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "", y = i < b.Length ? b[i] : "";
            if (x != y)
                return $"the range has \"{x}\", the merge \"{y}\"";
        }

        return "no line differs";
    }

    /// <summary>
    /// The options of <see cref="DetectionOptions"/> that make a range's scores or the merge's decisions what they are,
    /// in the fingerprint every range carries (<see cref="Fingerprint"/>). A test checks that every option is here or in
    /// <see cref="NotFingerprinted"/>.
    /// </summary>
    internal static readonly string[] Fingerprinted =
    [
        nameof(DetectionOptions.Compatibility), nameof(DetectionOptions.Detector), nameof(DetectionOptions.Threshold),
        nameof(DetectionOptions.MinSceneLength), nameof(DetectionOptions.MinContentVal), nameof(DetectionOptions.FrameWindow),
        nameof(DetectionOptions.Weights), nameof(DetectionOptions.LumaOnly), nameof(DetectionOptions.KernelSize),
        nameof(DetectionOptions.Bins), nameof(DetectionOptions.HashSize), nameof(DetectionOptions.HashLowpass),
        nameof(DetectionOptions.FadeBias), nameof(DetectionOptions.MinOutLength), nameof(DetectionOptions.MinSceneDuration),
        nameof(DetectionOptions.MinOutDuration), nameof(DetectionOptions.Crop), nameof(DetectionOptions.FullFrames),
        nameof(DetectionOptions.AlwaysBt601), nameof(DetectionOptions.Yuv420Converter), nameof(DetectionOptions.Detectors),
        nameof(DetectionOptions.FilterMode), nameof(DetectionOptions.AddLastScene), nameof(DetectionOptions.Downscale),
        nameof(DetectionOptions.DropShortScenes), nameof(DetectionOptions.MergeLastScene), nameof(DetectionOptions.FrameRate),
        nameof(DetectionOptions.CollectStats),
    ];

    /// <summary>
    /// The options left out of the fingerprint: refused for ranges (the time range, frame skip, deinterlacing, the tone,
    /// ffmpeg's resize), callbacks, and what only decoding reads (frames from the caller ignore it).
    /// </summary>
    internal static readonly string[] NotFingerprinted =
    [
        nameof(DetectionOptions.StartTime), nameof(DetectionOptions.EndTime), nameof(DetectionOptions.RangeDuration),
        nameof(DetectionOptions.Start), nameof(DetectionOptions.End), nameof(DetectionOptions.Duration),
        nameof(DetectionOptions.FrameSkip), nameof(DetectionOptions.Deinterlace), nameof(DetectionOptions.Yadif),
        nameof(DetectionOptions.FfmpegResize), nameof(DetectionOptions.TrimReferenceTone), nameof(DetectionOptions.ReferenceTone),
        nameof(DetectionOptions.Progress), nameof(DetectionOptions.Warnings), nameof(DetectionOptions.OnShot),
        nameof(DetectionOptions.ProgressStep), nameof(DetectionOptions.OnProbe), nameof(DetectionOptions.Streaming),
        nameof(DetectionOptions.ProbeBytes), nameof(DetectionOptions.DecodeThreads), nameof(DetectionOptions.Mode),
        "Parallel", nameof(DetectionOptions.Chunks), nameof(DetectionOptions.FrameBuffers),
        nameof(DetectionOptions.ConvertThreads), nameof(DetectionOptions.FfmpegDirectory), nameof(DetectionOptions.Decoder),
    ];

    /// <summary>
    /// What a range's scores depend on, as text (one "name=value" line each): every property of <paramref name="video"/>
    /// and the resolved options in <see cref="Fingerprinted"/> (the converter by its type). Equal for equal inputs on any
    /// machine (invariant culture, doubles round-tripped).
    /// </summary>
    internal static string Fingerprint(YuvVideo video, DetectionOptions o)
    {
        var sb = new StringBuilder();
        void Line(string name, object? value) => sb.Append(name).Append('=').Append(Text(value)).Append('\n');
        Line("Width", video.Width);
        Line("Height", video.Height);
        Line("FrameRate", video.FrameRate);
        Line("PixelFormat", video.PixelFormat);
        Line("ColorSpace", video.ColorSpace);
        Line("FullRange", video.FullRange);
        Line("TimeBase", video.TimeBase is { } tb ? $"{tb.Num}/{tb.Den}" : null);
        Line("StartPts", video.StartPts);
        Line("FrameCount", video.FrameCount);
        Line("Duration", video.Duration?.Ticks);
        Line("Codec", video.Codec);
        Line("Container", video.Container);
        Line(nameof(o.Compatibility), o.Compatibility);
        Line(nameof(o.Detector), o.Detector);
        Line(nameof(o.Threshold), o.Threshold);
        Line(nameof(o.MinSceneLength), o.MinSceneLength);
        Line(nameof(o.MinContentVal), o.MinContentVal);
        Line(nameof(o.FrameWindow), o.FrameWindow);
        Line(nameof(o.Weights), o.Weights);
        Line(nameof(o.LumaOnly), o.LumaOnly);
        Line(nameof(o.KernelSize), o.KernelSize);
        Line(nameof(o.Bins), o.Bins);
        Line(nameof(o.HashSize), o.HashSize);
        Line(nameof(o.HashLowpass), o.HashLowpass);
        Line(nameof(o.FadeBias), o.FadeBias);
        Line(nameof(o.MinOutLength), o.MinOutLength); // MinSceneDuration and MinOutDuration are in these strings once resolved
        Line(nameof(o.Crop), o.Crop);
        Line(nameof(o.FullFrames), o.FullFrames);
        Line(nameof(o.AlwaysBt601), o.AlwaysBt601);
        Line(nameof(o.Yuv420Converter), o.Yuv420Converter?.GetType().FullName);
        Line(nameof(o.FilterMode), o.FilterMode);
        Line(nameof(o.AddLastScene), o.AddLastScene);
        Line(nameof(o.Downscale), o.Downscale);
        Line(nameof(o.DropShortScenes), o.DropShortScenes);
        Line(nameof(o.MergeLastScene), o.MergeLastScene);
        Line(nameof(o.FrameRate), o.FrameRate);
        Line(nameof(o.CollectStats), o.CollectStats);
        Line(nameof(o.Detectors), o.Detectors?.Count);
        foreach (var (st, i) in (o.Detectors ?? []).Select((st, i) => (st, i)))
        {
            string p = $"Detectors[{i}].";
            Line(p + nameof(st.Kind), st.Kind);
            Line(p + nameof(st.Threshold), st.Threshold);
            Line(p + nameof(st.MinSceneLength), st.MinSceneLength);
            Line(p + nameof(st.FilterMode), st.FilterMode);
            Line(p + nameof(st.MinContentVal), st.MinContentVal);
            Line(p + nameof(st.FrameWindow), st.FrameWindow);
            Line(p + nameof(st.Weights), st.Weights);
            Line(p + nameof(st.LumaOnly), st.LumaOnly);
            Line(p + nameof(st.KernelSize), st.KernelSize);
            Line(p + nameof(st.Bins), st.Bins);
            Line(p + nameof(st.HashSize), st.HashSize);
            Line(p + nameof(st.HashLowpass), st.HashLowpass);
            Line(p + nameof(st.FadeBias), st.FadeBias);
            Line(p + nameof(st.AddLastScene), st.AddLastScene);
            Line(p + nameof(st.MinOutLength), st.MinOutLength);
        }

        return sb.ToString();

        static string Text(object? value) => value switch
        {
            null => "none",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            (double, double, double, double) w => $"{Text(w.Item1)},{Text(w.Item2)},{Text(w.Item3)},{Text(w.Item4)}",
            (int, int, int, int) c => $"{c.Item1},{c.Item2},{c.Item3},{c.Item4}",
            Fps f => $"{f.Num}/{f.Den}",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>Scores computed elsewhere (<see cref="Merge"/>) for <see cref="Steps"/>, in place of the frames', with the warnings that came with them.</summary>
    internal sealed record Prescored((List<double?>[] Metrics, int Frames, (Stats? Stats, int From, int To)[] Stats) Scores, IReadOnlyList<string> Warnings);
}
