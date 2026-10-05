using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ShotDetector;

/// <summary>
/// Which PySceneDetect release to reproduce. Each is verified in CI to give identical cuts, scene
/// list CSVs and per-frame stats.
/// </summary>
public enum PySceneDetectVersion
{
    /// <summary>scenedetect 0.7.1: real frame timestamps (handles variable frame rate), fractional downscale.</summary>
    V0_7_1,
    /// <summary>
    /// scenedetect 0.6.4: frame numbers at OpenCV's average frame rate, a whole-number downscale factor
    /// from the width (854x480 is analysed at 285x160), and its own fade-cut rounding.
    /// </summary>
    V0_6_4,
}

/// <summary>Which PySceneDetect detector to run.</summary>
public enum DetectorKind
{
    /// <summary>AdaptiveDetector (scenedetect detect-adaptive): content changes relative to neighbouring frames.</summary>
    Adaptive,
    /// <summary>ContentDetector (detect-content): content changes above a fixed threshold.</summary>
    Content,
    /// <summary>ThresholdDetector (detect-threshold): fades to and from black.</summary>
    Threshold,
    /// <summary>HistogramDetector (detect-hist): changes in the luma histogram.</summary>
    Histogram,
    /// <summary>HashDetector (detect-hash): changes in a perceptual hash of the frame.</summary>
    Hash,
}

/// <summary>Detection settings. Defaults match the scenedetect CLI.</summary>
public sealed record DetectionOptions
{
    /// <summary>Which PySceneDetect release to reproduce (default 0.7.1).</summary>
    public PySceneDetectVersion Compatibility { get; init; } = PySceneDetectVersion.V0_7_1;

    /// <summary>Which detector to run (default adaptive).</summary>
    public DetectorKind Detector { get; init; } = DetectorKind.Adaptive;

    /// <summary>
    /// Content: content_val threshold (27). Adaptive: ratio threshold (3). Threshold: fade level (12).
    /// Histogram: how far the histogram correlation must drop below 1 (0.05).
    /// Hash: fraction of hash bits that must differ (0.395).
    /// </summary>
    public double? Threshold { get; init; }

    /// <summary>Minimum shot length: frames ("15") or seconds ("0.6s").</summary>
    public string MinSceneLength { get; init; } = "0.6s";

    /// <summary>Adaptive: minimum content_val for a cut.</summary>
    public double MinContentVal { get; init; } = 15.0;

    /// <summary>Adaptive: frames on each side to average.</summary>
    public int FrameWindow { get; init; } = 2;

    /// <summary>Content/adaptive: weights of hue, saturation, luma and edge differences.</summary>
    public (double Hue, double Sat, double Lum, double Edges) Weights { get; init; } = (1, 1, 1, 0);

    /// <summary>Content/adaptive: only use the V (brightness) channel; overrides <see cref="Weights"/>.</summary>
    public bool LumaOnly { get; init; }

    /// <summary>Content/adaptive: odd size >= 3 to widen edges by; null picks it from the resolution.</summary>
    public int? KernelSize { get; init; }

    /// <summary>Histogram: number of luma histogram bins (1..256).</summary>
    public int Bins { get; init; } = 256;

    /// <summary>Hash: the hash is HashSize×HashSize bits.</summary>
    public int HashSize { get; init; } = 16;

    /// <summary>Hash: frames are shrunk to HashSize×HashLowpass pixels on each side before hashing.</summary>
    public int HashLowpass { get; init; } = 2;

    /// <summary>Threshold: cut position between fade-out (-1) and fade-in (+1).</summary>
    public double FadeBias { get; init; }

    /// <summary>
    /// Where to start, like scenedetect's `time -s`: "HH:MM:SS[.mmm]", seconds ("12.5s" or "12.5")
    /// or a 1-based frame number ("300"). Null: the first frame.
    /// </summary>
    public string? StartTime { get; init; }

    /// <summary>Where to stop (exclusive), like `time -e`; same formats, frame numbers are 0-based here as in scenedetect.</summary>
    public string? EndTime { get; init; }

    /// <summary>How much to analyse from the start, like `time -d`; same formats. Not with <see cref="EndTime"/>.</summary>
    public string? Duration { get; init; }

    /// <summary>
    /// Analyse every (FrameSkip + 1)th frame, like scenedetect's --frame-skip: faster on very
    /// high frame rates, less accurate. Not with <see cref="CollectStats"/>.
    /// </summary>
    public int FrameSkip { get; init; }

    /// <summary>
    /// Only analyse this part of the frame, like scenedetect's --crop: inclusive pixel coordinates
    /// of two opposite corners, in the upright frame.
    /// </summary>
    public (int X0, int Y0, int X1, int Y1)? Crop { get; init; }

    /// <summary>Let ffmpeg downscale (faster, but results can differ from PySceneDetect).</summary>
    public bool FfmpegResize { get; init; }

    /// <summary>ffmpeg decoder threads; 0 = ffmpeg's choice.</summary>
    public int DecodeThreads { get; init; } = 4;

    /// <summary>
    /// Enables the yuv420p fast path: much faster on large video, same results. Use SwscaleYuv420
    /// from the ShotDetector.FastYuv package (LGPL).
    /// </summary>
    public IYuv420Converter? Yuv420Converter { get; init; }

    /// <summary>Folder containing ffmpeg and ffprobe; null (default) finds them on PATH.</summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>
    /// Receives progress while frames are decoded (about 10 times a second, and once at the end).
    /// With <see cref="System.Progress{T}"/>, reports arrive on the thread that created it.
    /// </summary>
    public IProgress<DetectionProgress>? Progress { get; init; }

    /// <summary>Record per-frame metrics in <see cref="DetectionResult.Stats"/> (like scenedetect -s).</summary>
    public bool CollectStats { get; init; }
}

/// <summary>How far <see cref="ShotDetection.Detect"/> has got.</summary>
/// <param name="FramesProcessed">Frames decoded and analysed so far.</param>
/// <param name="ExpectedFrames">Frames the video is expected to have (from its packets); 0 if unknown.</param>
public readonly record struct DetectionProgress(int FramesProcessed, int ExpectedFrames)
{
    /// <summary>0..1, or null when the frame count is unknown.</summary>
    public double? Fraction => ExpectedFrames > 0 ? Math.Min(1.0, FramesProcessed / (double)ExpectedFrames) : null;
}

/// <summary>The outcome of <see cref="ShotDetection.Detect"/>.</summary>
/// <param name="VideoPath">The video analysed.</param>
/// <param name="Video">Its properties (size, frame rate, ...).</param>
/// <param name="Shots">The shots, in order, covering the whole video.</param>
/// <param name="FrameCount">Frames decoded.</param>
/// <param name="MinSceneLengthFrames"><see cref="DetectionOptions.MinSceneLength"/> in frames of this video.</param>
/// <param name="Stats">Per-frame metrics when <see cref="DetectionOptions.CollectStats"/> was set, else null.</param>
public sealed record DetectionResult(
    string VideoPath, VideoReader Video, IReadOnlyList<Shot> Shots, int FrameCount, int MinSceneLengthFrames, Stats? Stats);

/// <summary>Runs shot detection on a video file.</summary>
public static class ShotDetection
{
    /// <summary>
    /// Decodes <paramref name="videoPath"/> with ffmpeg and returns its shots, as
    /// `scenedetect -i video detect-... list-scenes` would.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="InvalidOperationException">ffprobe or ffmpeg failed (e.g. missing file).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; ffmpeg is stopped.</exception>
    public static DetectionResult Detect(string videoPath, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(videoPath, options, onShot: null, cancellationToken);

    /// <summary>
    /// Like <see cref="Detect"/>, but yields each shot as soon as it is known, while the rest of the
    /// video is still being decoded (on a background thread). The shots are exactly those Detect
    /// returns. A shot is known once the cut that ends it is confirmed, which can take a few frames
    /// (the adaptive detector's window, the minimum shot length); the last shot comes at the end.
    /// Stopping the enumeration early, or cancelling, stops ffmpeg.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="InvalidOperationException">ffprobe or ffmpeg failed (e.g. missing file).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; ffmpeg is stopped.</exception>
    public static async IAsyncEnumerable<Shot> DetectStreamAsync(string videoPath, DetectionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<Shot>(new() { SingleReader = true, SingleWriter = true });
        // Also cancelled when the caller stops enumerating, so the decoding thread and ffmpeg stop too.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() =>
        {
            try
            {
                Run(videoPath, options, shot => channel.Writer.TryWrite(shot), stop.Token);
                channel.Writer.Complete();
            }
            catch (Exception e)
            {
                channel.Writer.Complete(e);
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var shot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return shot;
        }
        finally
        {
            stop.Cancel();
            await producer.ConfigureAwait(false); // never throws: errors go through the channel
        }
    }

    static DetectionResult Run(string videoPath, DetectionOptions? options, Action<Shot>? onShot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var o = options ?? new DetectionOptions();
        if (o.FrameWindow < 1)
            throw new ArgumentException("FrameWindow must be at least 1.");
        if (o.KernelSize is { } k && (k < 3 || k % 2 == 0))
            throw new ArgumentException("KernelSize must be an odd number >= 3.");
        if (o.Bins is < 1 or > 256)
            throw new ArgumentException("Bins must be between 1 and 256.");
        if (o.HashSize < 1 || o.HashLowpass < 1)
            throw new ArgumentException("HashSize and HashLowpass must be at least 1.");
        if (o.FrameSkip < 0)
            throw new ArgumentException("FrameSkip must be at least 0.");
        if (o.FrameSkip > 0 && o.CollectStats)
            throw new ArgumentException("FrameSkip must be 0 when collecting stats (as in scenedetect).");
        if (o.EndTime is not null && o.Duration is not null)
            throw new ArgumentException("EndTime and Duration cannot both be set.");

        var video = new VideoReader(videoPath, o, cancellationToken);
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, video.Fps);

        var w = o.Weights;
        var scorer = o.LumaOnly ? ContentScorer.LumaOnly() : new ContentScorer(w.Hue, w.Sat, w.Lum, w.Edges);
        // Like PySceneDetect, edges are computed when they're weighted or stats are collected.
        if (o.Detector is DetectorKind.Adaptive or DetectorKind.Content && (scorer.UsesEdges || o.CollectStats))
            scorer.Edges = new EdgeDetector(video.Width, video.Height, o.KernelSize);
        IDetector detector = o.Detector switch
        {
            DetectorKind.Content => new ContentDetector(scorer, video.Position, video.Fps, o.Threshold ?? 27.0, minSceneLen),
            DetectorKind.Threshold => new ThresholdDetector(video.Position, video.Fps, o.Threshold ?? 12.0, minSceneLen, o.FadeBias, o.Compatibility),
            DetectorKind.Histogram => new HistogramDetector(video.Position, o.Threshold ?? 0.05, o.Bins, minSceneLen, o.Compatibility),
            DetectorKind.Hash => new HashDetector(video.Position, o.Threshold ?? 0.395, o.HashSize, o.HashLowpass, minSceneLen),
            _ => new AdaptiveDetector(scorer, video.Position, o.Threshold ?? 3.0, minSceneLen, o.FrameWindow, o.MinContentVal),
        };
        if (o.CollectStats)
            detector.Stats = new Stats();
        if (detector is HashDetector hash)
            hash.SetFrameSize(video.Width, video.Height);

        // Shots are built as cuts arrive (detectors report them in frame order), which is what
        // get_scenes_from_cuts does with the sorted, de-duplicated cut list.
        var shots = new List<Shot>();
        var start = video.Position(0);
        FrameTime? lastCut = null;
        void Cut(FrameTime cut)
        {
            // A cut at frame 0 still makes a (zero-length) first shot, as in scenedetect.
            if (lastCut is { } previous)
            {
                if (cut.FrameNum == previous.FrameNum)
                    return; // the same cut twice
                if (cut.FrameNum < previous.FrameNum)
                    throw new InvalidOperationException($"Cut at frame {cut.FrameNum} came after one at {previous.FrameNum}.");
            }
            lastCut = cut;
            var shot = new Shot(shots.Count + 1, start, cut);
            shots.Add(shot);
            onShot?.Invoke(shot);
            start = cut;
        }

        var range = FrameRange.For(video, o);
        start = video.Position(range.Start);
        int frameCount = 0, stride = o.FrameSkip + 1;
        long lastReport = 0;
        foreach (var frame in video.Frames(range.Start, range.Count, cancellationToken))
        {
            int index = range.Start + frameCount;
            if (frameCount % stride == 0 && detector.ProcessFrame(index, frame) is { } cut)
                Cut(cut);
            frameCount++;
            if (o.Progress is not null && Environment.TickCount64 - lastReport >= 100)
            {
                o.Progress.Report(new(frameCount, range.Count ?? video.ExpectedFrames - range.Start));
                lastReport = Environment.TickCount64;
            }
        }
        o.Progress?.Report(new(frameCount, frameCount));
        if (frameCount > 0)
        {
            // Where scenedetect finds the stream after the loop: past the end if it read until EOF,
            // otherwise at the last frame read (which may be a skipped one).
            var last = range.ReadsToEnd ? video.PositionAfterDecoding(range.Start + frameCount) : video.Position(range.Start + frameCount - 1);
            if (detector.PostProcess(last) is { } trailingCut)
                Cut(trailingCut);
            var final = new Shot(shots.Count + 1, start, last.PlusFrames(1));
            shots.Add(final);
            onShot?.Invoke(final);
        }
        return new(videoPath, video, shots, frameCount, minSceneLen, detector.Stats);
    }

    /// <summary>"0.6s" → frames with Python's round-half-to-even, like FrameTimecode._seconds_to_frames; "15" → 15.</summary>
    internal static int MinSceneLengthInFrames(string value, Fps fps) => value.EndsWith('s')
        ? (int)Math.Round(double.Parse(value[..^1], CultureInfo.InvariantCulture) * fps.Value)
        : int.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// A scenedetect timecode ("HH:MM:SS[.mmm]", "12.5s", "12.5", or "300" frames) in seconds, as
    /// FrameTimecode(str, fps).seconds: frames go through round(frames) / fps.
    /// </summary>
    internal static double TimecodeSeconds(string value, Fps fps, bool oneBasedFrames = false)
    {
        value = value.Trim();
        if (value.All(char.IsDigit))
        {
            long frames = long.Parse(value, CultureInfo.InvariantCulture);
            if (oneBasedFrames && frames >= 1) frames--;
            return frames / fps.Value;
        }
        if (value.EndsWith('s'))
            return double.Parse(value[..^1], CultureInfo.InvariantCulture);
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 3) throw new FormatException($"Timecode '{value}' must be HH:MM:SS[.mmm].");
            return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
                + double.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Which frames to decode, as scenedetect's `time` and `--frame-skip` options select them: frames
/// Start .. Start + Count - 1 (Count null: to the end), of which every (FrameSkip + 1)th is analysed.
/// </summary>
readonly record struct FrameRange(int Start, int? Count, bool ReadsToEnd)
{
    public static FrameRange For(VideoReader video, DetectionOptions o)
    {
        var fps = video.Fps;
        int total = video.ExpectedFrames;
        int start = o.StartTime is null ? 0 : video.SeekFrame(ShotDetection.TimecodeSeconds(o.StartTime, fps, oneBasedFrames: true));
        // scenedetect turns the end/duration into a whole number of frames at the average rate first
        // (FrameTimecode rounds half to even), which differs from the seconds on variable frame rate video.
        static double Frames(string value, Fps fps) => Math.Round(ShotDetection.TimecodeSeconds(value, fps) * fps.Value);
        double? end = o.EndTime is not null ? Frames(o.EndTime, fps) / fps.Value
            : o.Duration is not null ? (Frames(o.Duration, fps) + start) / fps.Value
            : null;
        if (end is null || total == 0)
            return new(start, null, true);

        // scenedetect analyses a frame, skips FrameSkip frames, then stops if the last frame read
        // plus one frame reaches the end time (or if it hit the end of the video while skipping).
        int stride = o.FrameSkip + 1;
        for (int processed = start; ; processed += stride)
        {
            if (processed > total - 1)
                return new(start, total - start, true);
            int lastRead = Math.Min(processed + o.FrameSkip, total - 1);
            if (processed + o.FrameSkip > total - 1)
                return new(start, total - start, true);
            if (video.Position(lastRead).PlusFrames(1).Seconds >= end.Value)
                return new(start, lastRead - start + 1, false);
        }
    }
}
