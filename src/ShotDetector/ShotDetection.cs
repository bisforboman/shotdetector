using System.Globalization;

namespace ShotDetector;

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

    /// <summary>Record per-frame metrics in <see cref="DetectionResult.Stats"/> (like scenedetect -s).</summary>
    public bool CollectStats { get; init; }
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
    public static DetectionResult Detect(string videoPath, DetectionOptions? options = null)
    {
        var o = options ?? new DetectionOptions();
        if (o.FrameWindow < 1)
            throw new ArgumentException("FrameWindow must be at least 1.");
        if (o.KernelSize is { } k && (k < 3 || k % 2 == 0))
            throw new ArgumentException("KernelSize must be an odd number >= 3.");

        var video = new VideoReader(videoPath, o.FfmpegResize, o.DecodeThreads);
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, video.Fps);

        var w = o.Weights;
        var scorer = o.LumaOnly ? ContentScorer.LumaOnly() : new ContentScorer(w.Hue, w.Sat, w.Lum, w.Edges);
        // Like PySceneDetect, edges are computed when they're weighted or stats are collected.
        if (o.Detector != DetectorKind.Threshold && (scorer.UsesEdges || o.CollectStats))
            scorer.Edges = new EdgeDetector(video.Width, video.Height, o.KernelSize);
        IDetector detector = o.Detector switch
        {
            DetectorKind.Content => new ContentDetector(scorer, video.Position, video.Fps, o.Threshold ?? 27.0, minSceneLen),
            DetectorKind.Threshold => new ThresholdDetector(video.Position, video.Fps, o.Threshold ?? 12.0, minSceneLen, o.FadeBias),
            _ => new AdaptiveDetector(scorer, video.Position, o.Threshold ?? 3.0, minSceneLen, o.FrameWindow, o.MinContentVal),
        };
        if (o.CollectStats)
            detector.Stats = new Stats();

        var cuts = new List<FrameTime>();
        int frameCount = 0;
        foreach (var frame in video.Frames())
        {
            if (detector.ProcessFrame(frameCount, frame) is { } cut)
                cuts.Add(cut);
            frameCount++;
        }
        if (frameCount > 0 && detector.PostProcess(video.PositionAfterDecoding(frameCount)) is { } last)
            cuts.Add(last);

        var shots = frameCount == 0
            ? []
            : Shots.FromCuts(cuts, video.Position(0), video.PositionAfterDecoding(frameCount).PlusFrames(1));
        return new(videoPath, video, shots, frameCount, minSceneLen, detector.Stats);
    }

    /// <summary>"0.6s" → frames with Python's round-half-to-even, like FrameTimecode._seconds_to_frames; "15" → 15.</summary>
    public static int MinSceneLengthInFrames(string value, Fps fps) => value.EndsWith('s')
        ? (int)Math.Round(double.Parse(value[..^1], CultureInfo.InvariantCulture) * fps.Value)
        : int.Parse(value, CultureInfo.InvariantCulture);
}
