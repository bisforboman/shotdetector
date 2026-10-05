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
}

/// <summary>Detection settings. Defaults match the scenedetect CLI.</summary>
public sealed record DetectionOptions
{
    /// <summary>Which PySceneDetect release to reproduce (default 0.7.1).</summary>
    public PySceneDetectVersion Compatibility { get; init; } = PySceneDetectVersion.V0_7_1;

    /// <summary>Which detector to run (default adaptive).</summary>
    public DetectorKind Detector { get; init; } = DetectorKind.Adaptive;

    /// <summary>Content: content_val threshold (27). Adaptive: ratio threshold (3). Threshold: fade level (12).</summary>
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

    /// <summary>Threshold: cut position between fade-out (-1) and fade-in (+1).</summary>
    public double FadeBias { get; init; }

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
public sealed record DetectionResult(
    string VideoPath, VideoReader Video, IReadOnlyList<Shot> Shots, int FrameCount, int MinSceneLength, Stats? Stats);

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

        var video = new VideoReader(videoPath, o, cancellationToken);
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, video.Fps);

        var w = o.Weights;
        var scorer = o.LumaOnly ? ContentScorer.LumaOnly() : new ContentScorer(w.Hue, w.Sat, w.Lum, w.Edges);
        // Like PySceneDetect, edges are computed when they're weighted or stats are collected.
        if (o.Detector != DetectorKind.Threshold && (scorer.UsesEdges || o.CollectStats))
            scorer.Edges = new EdgeDetector(video.Width, video.Height, o.KernelSize);
        IDetector detector = o.Detector switch
        {
            DetectorKind.Content => new ContentDetector(scorer, video.Position, video.Fps, o.Threshold ?? 27.0, minSceneLen),
            DetectorKind.Threshold => new ThresholdDetector(video.Position, video.Fps, o.Threshold ?? 12.0, minSceneLen, o.FadeBias, o.Compatibility),
            _ => new AdaptiveDetector(scorer, video.Position, o.Threshold ?? 3.0, minSceneLen, o.FrameWindow, o.MinContentVal),
        };
        if (o.CollectStats)
            detector.Stats = new Stats();

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

        int frameCount = 0;
        long lastReport = 0;
        foreach (var frame in video.Frames(cancellationToken))
        {
            if (detector.ProcessFrame(frameCount, frame) is { } cut)
                Cut(cut);
            frameCount++;
            if (o.Progress is not null && Environment.TickCount64 - lastReport >= 100)
            {
                o.Progress.Report(new(frameCount, video.ExpectedFrames));
                lastReport = Environment.TickCount64;
            }
        }
        o.Progress?.Report(new(frameCount, video.ExpectedFrames));
        if (frameCount > 0)
        {
            if (detector.PostProcess(video.PositionAfterDecoding(frameCount)) is { } last)
                Cut(last);
            var final = new Shot(shots.Count + 1, start, video.PositionAfterDecoding(frameCount).PlusFrames(1));
            shots.Add(final);
            onShot?.Invoke(final);
        }
        return new(videoPath, video, shots, frameCount, minSceneLen, detector.Stats);
    }

    /// <summary>"0.6s" → frames with Python's round-half-to-even, like FrameTimecode._seconds_to_frames; "15" → 15.</summary>
    public static int MinSceneLengthInFrames(string value, Fps fps) => value.EndsWith('s')
        ? (int)Math.Round(double.Parse(value[..^1], CultureInfo.InvariantCulture) * fps.Value)
        : int.Parse(value, CultureInfo.InvariantCulture);
}
