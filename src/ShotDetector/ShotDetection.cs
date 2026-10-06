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

/// <summary>How ContentDetector enforces the minimum scene length (scenedetect's detect-content --filter-mode).</summary>
public enum FlashFilterMode
{
    /// <summary>Merge cuts closer than the minimum length (scenedetect's default).</summary>
    Merge,

    /// <summary>Drop cuts until the minimum length has passed since the last cut.</summary>
    Suppress,
}

/// <summary>
/// One detector of a run with several (<see cref="DetectionOptions.Detectors"/>), like one detect-* command of the
/// scenedetect CLI with its own options. Unset values come from the <see cref="DetectionOptions"/> (Threshold: the
/// detector's default).
/// </summary>
/// <param name="Kind">The detector.</param>
public sealed record DetectorSettings(DetectorKind Kind)
{
    /// <summary>The detector's threshold; null: its scenedetect default.</summary>
    public double? Threshold { get; init; }

    /// <summary>Minimum shot length for this detector (same formats as <see cref="DetectionOptions.MinSceneLength"/>).</summary>
    public string? MinSceneLength { get; init; }

    /// <summary>Content: how the minimum length is enforced.</summary>
    public FlashFilterMode? FilterMode { get; init; }

    /// <summary>Adaptive: see <see cref="DetectionOptions.MinContentVal"/>.</summary>
    public double? MinContentVal { get; init; }

    /// <summary>Adaptive: see <see cref="DetectionOptions.FrameWindow"/>.</summary>
    public int? FrameWindow { get; init; }

    /// <summary>Content/adaptive: see <see cref="DetectionOptions.Weights"/>.</summary>
    public (double Hue, double Sat, double Lum, double Edges)? Weights { get; init; }

    /// <summary>Content/adaptive: see <see cref="DetectionOptions.LumaOnly"/>.</summary>
    public bool? LumaOnly { get; init; }

    /// <summary>Content/adaptive: see <see cref="DetectionOptions.KernelSize"/>.</summary>
    public int? KernelSize { get; init; }

    /// <summary>Histogram: see <see cref="DetectionOptions.Bins"/>.</summary>
    public int? Bins { get; init; }

    /// <summary>Hash: see <see cref="DetectionOptions.HashSize"/>.</summary>
    public int? HashSize { get; init; }

    /// <summary>Hash: see <see cref="DetectionOptions.HashLowpass"/>.</summary>
    public int? HashLowpass { get; init; }

    /// <summary>Threshold: see <see cref="DetectionOptions.FadeBias"/>.</summary>
    public double? FadeBias { get; init; }

    /// <summary>Threshold: see <see cref="DetectionOptions.AddLastScene"/>.</summary>
    public bool? AddLastScene { get; init; }
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

    /// <summary>Minimum shot length, as scenedetect takes it: frames ("15"), seconds ("0.6s" or "0.6") or "HH:MM:SS[.mmm]".</summary>
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

    /// <summary>
    /// Read the input once, start to end, without probing all of it first (as a Stream or URL input
    /// always is): for pipes and live feeds. Timestamps then come from ffmpeg as frames arrive,
    /// the frame count is unknown and <see cref="StartTime"/> is not available.
    /// </summary>
    public bool Streaming { get; init; }

    /// <summary>How many bytes of a Stream input to read for probing its container headers (16 MB).</summary>
    public int ProbeBytes { get; init; } = 16 << 20;

    /// <summary>Tests: pipe whole BGR frames rather than only the pixels the resize reads (same results, slower).</summary>
    internal bool FullFrames { get; init; }

    /// <summary>Let ffmpeg downscale (faster, but results can differ from PySceneDetect).</summary>
    public bool FfmpegResize { get; init; }

    /// <summary>
    /// ffmpeg decoder threads; 0 = ffmpeg's choice. Null (the default) is one fewer than the CPUs available (so 1 on a
    /// 2-CPU container), at most 8 when ffmpeg sends yuv420p (<see cref="FramePipeline.Yuv420Sampled"/>, bound by
    /// decoding) and at most 4 otherwise.
    /// </summary>
    public int? DecodeThreads { get; init; }

    /// <summary>
    /// Enables the yuv420p fast path: much faster on large video, same results. Use SwscaleYuv420
    /// from the ShotDetector.FastYuv package (LGPL).
    /// </summary>
    public IYuv420Converter? Yuv420Converter { get; init; }

    /// <summary>Folder containing ffmpeg and ffprobe; null (default) finds them on PATH.</summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>
    /// Several detectors in one run (like several detect-* commands): their cuts are combined. Null or empty: the one
    /// <see cref="Detector"/>, with <see cref="Threshold"/>. With several, shots stream out only at the end.
    /// </summary>
    public IReadOnlyList<DetectorSettings>? Detectors { get; init; }

    /// <summary>Content: how the minimum length is enforced (detect-content --filter-mode).</summary>
    public FlashFilterMode FilterMode { get; init; }

    /// <summary>
    /// Threshold: when the video ends faded out, cut at the fade-out (scenedetect's add-last-scene, on by default;
    /// its config file is the only place to turn it off).
    /// </summary>
    public bool AddLastScene { get; init; } = true;

    /// <summary>Downscale frames by this whole factor (scenedetect -d); null: scenedetect's automatic factor; 1: none.</summary>
    public int? Downscale { get; init; }

    /// <summary>
    /// Detect without a minimum length, then drop shots shorter than <see cref="MinSceneLength"/>
    /// (--drop-short-scenes). The result can have gaps; <see cref="DetectionResult.Cuts"/> keeps every cut.
    /// </summary>
    public bool DropShortScenes { get; init; }

    /// <summary>Merge the last shot into the one before it when it is shorter than <see cref="MinSceneLength"/> (--merge-last-scene).</summary>
    public bool MergeLastScene { get; init; }

    /// <summary>
    /// Override the detected frame rate, like scenedetect's <c>-f/--frame-rate</c>: frame numbers and
    /// <see cref="MinSceneLength"/> in frames use it, while times still come from the container. For an
    /// image sequence ("frames/%04d.png"), which has no times, frame n is at n / rate (default 25).
    /// </summary>
    public double? FrameRate { get; init; }

    /// <summary>
    /// Receives progress while frames are decoded (about 10 times a second, and once at the end).
    /// With <see cref="System.Progress{T}"/>, reports arrive on the thread that created it.
    /// </summary>
    public IProgress<DetectionProgress>? Progress { get; init; }

    /// <summary>Record per-frame metrics in <see cref="DetectionResult.Stats"/> (like scenedetect -s).</summary>
    public bool CollectStats { get; init; }
}

/// <summary>How far <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/> has got.</summary>
/// <param name="FramesProcessed">Frames decoded and analysed so far.</param>
/// <param name="ExpectedFrames">Frames the video is expected to have (from its packets); 0 if unknown.</param>
public readonly record struct DetectionProgress(int FramesProcessed, int ExpectedFrames)
{
    /// <summary>0..1, or null when the frame count is unknown.</summary>
    public double? Fraction => ExpectedFrames > 0 ? Math.Min(1.0, FramesProcessed / (double)ExpectedFrames) : null;
}

/// <summary>The outcome of <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/>.</summary>
/// <param name="VideoPath">The video analysed: a path or URL, or null for a Stream input (exports need a path).</param>
/// <param name="Video">Its properties (size, frame rate, ...).</param>
/// <param name="Shots">The shots, in order, covering the whole video.</param>
/// <param name="FrameCount">Frames decoded.</param>
/// <param name="MinSceneLengthFrames"><see cref="DetectionOptions.MinSceneLength"/> in frames of this video.</param>
/// <param name="Stats">Per-frame metrics when <see cref="DetectionOptions.CollectStats"/> was set, else null.</param>
/// <param name="Cuts">Every cut, sorted: the shot starts after the first, unless shots were dropped or merged
/// (scenedetect's cut list, which its CSV's first row, the HTML page and the QP file use).</param>
public sealed record DetectionResult(
    string? VideoPath, VideoReader Video, IReadOnlyList<Shot> Shots, int FrameCount, int MinSceneLengthFrames, Stats? Stats,
    IReadOnlyList<FrameTime> Cuts);

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
        Run(videoPath, null, options, onShot: null, cancellationToken);

    /// <summary>
    /// As <see cref="Detect(string, DetectionOptions?, CancellationToken)"/>, reading the video's bytes
    /// from <paramref name="video"/> (piped straight into ffmpeg, no temporary file). The container
    /// headers must come first: mkv, webm, ts, mov, or an mp4 written with "faststart".
    /// </summary>
    public static DetectionResult Detect(Stream video, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(null, video, options, onShot: null, cancellationToken);

    /// <summary>
    /// load-scenes: shots from a scene list CSV instead of detecting them (nothing is decoded; the video is only
    /// probed for its frame rate and length), e.g. to export again from an edited list. The column holds each shot's
    /// start: 1-based frame numbers ("Start Frame", the default), or timecodes/seconds. The time range options, and
    /// <see cref="DetectionOptions.DropShortScenes"/>/<see cref="DetectionOptions.MergeLastScene"/>, apply as in scenedetect.
    /// </summary>
    /// <param name="videoPath">The video the list is for.</param>
    /// <param name="sceneListCsv">A scene list CSV, e.g. from <see cref="Shots.Csv"/> or scenedetect's list-scenes.</param>
    /// <param name="column">The column with each shot's start.</param>
    /// <param name="options">Uses the time range, MinSceneLength, DropShortScenes, MergeLastScene, Compatibility and FfmpegDirectory.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public static DetectionResult LoadScenes(string videoPath, string sceneListCsv, string column = "Start Frame",
        DetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new DetectionOptions();
        var video = new VideoReader(videoPath, o, cancellationToken);
        var fps = video.Fps;
        using var reader = new StreamReader(sceneListCsv);
        var header = CsvRow(reader.ReadLine());
        if (!header.Contains(column))
            header = CsvRow(reader.ReadLine()); // past scenedetect's "Timecode List:" row
        int col = header.IndexOf(column);
        if (col < 0)
            throw new ArgumentException($"The scene list has no column '{column}'.");
        // FrameTimecode(value, fps): digits are a frame number, anything else a time in seconds.
        FrameTime Time(string value, int shift = 0) => value.All(char.IsDigit)
            ? (o.Compatibility == PySceneDetectVersion.V0_6_4 ? FrameTime.Frame064(Math.Max(0, long.Parse(value, CultureInfo.InvariantCulture) - shift), fps)
                : FrameTime.Frame(Math.Max(0, long.Parse(value, CultureInfo.InvariantCulture) - shift), fps))
            : FrameTime.FromSeconds(TimecodeSeconds(value, fps), fps);
        // Start Frame values are 1-based in the CLI's output.
        FrameTime Parse(string value) => Time(value, shift: 1);
        var starts = new List<FrameTime>();
        for (string? line; (line = reader.ReadLine()) is not null;)
            if (line.Length > 0 && CsvRow(line) is var row && col < row.Count)
                starts.Add(Parse(row[col]));
        // sorted(...)[1:]: the first shot's start is no cut. None of these are exact timestamps, so they
        // compare (and sort, stably) by frame number, as FrameTimecode does.
        var cuts = starts.OrderBy(c => c.FrameNum).Skip(1).ToList();
        var start = FrameTime.Frame(0, fps); // base_timecode
        if (o.StartTime is { } s)
        {
            start = Time(s.Trim(), shift: 1); // `time -s`: a frame number is 1-based (parse_timecode(correct_pts=True))
            cuts = [.. cuts.Where(c => c.FrameNum > start.FrameNum)];
        }
        // min(x, duration): the first one unless the video is shorter (by frame number).
        var duration = FrameTime.Frame(video.OpenCvFrameCount, fps);
        FrameTime Min(FrameTime x) => duration.FrameNum < x.FrameNum ? duration : x;
        var end = o.EndTime is { } e ? Min(Time(e.Trim()))
            : o.Duration is { } d ? Min(start.Plus(Time(d.Trim())))
            : duration;
        cuts = [.. cuts.Where(c => c.FrameNum < end.FrameNum)];
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, fps);
        var shots = Scenes(cuts, start, end, o.MergeLastScene, o.DropShortScenes ? minSceneLen : 0, minSceneLen);
        return new(videoPath, video, shots, (int)video.OpenCvFrameCount, minSceneLen, null, cuts);
    }

    /// <summary>One CSV row (RFC 4180 quoting), as Python's csv.reader reads it.</summary>
    static List<string> CsvRow(string? line)
    {
        var cells = new List<string>();
        if (line is null)
            return cells;
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>
    /// <see cref="Detect(string, DetectionOptions?, CancellationToken)"/> on a thread-pool thread, so the
    /// caller's thread (a UI, a request) isn't blocked while ffmpeg decodes.
    /// </summary>
    public static Task<DetectionResult> DetectAsync(string videoPath, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(videoPath, null, options, onShot: null, cancellationToken), cancellationToken);

    /// <summary><see cref="Detect(Stream, DetectionOptions?, CancellationToken)"/> on a thread-pool thread.</summary>
    public static Task<DetectionResult> DetectAsync(Stream video, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(null, video, options, onShot: null, cancellationToken), cancellationToken);

    /// <summary>
    /// Like <see cref="Detect(string, DetectionOptions?, CancellationToken)"/>, but yields each shot as soon as it is known, while the rest of the
    /// video is still being decoded (on a background thread). The shots are exactly those Detect
    /// returns. A shot is known once the cut that ends it is confirmed, which can take a few frames
    /// (the adaptive detector's window, the minimum shot length); the last shot comes at the end.
    /// Stopping the enumeration early, or cancelling, stops ffmpeg.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="InvalidOperationException">ffprobe or ffmpeg failed (e.g. missing file).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; ffmpeg is stopped.</exception>
    public static IAsyncEnumerable<Shot> DetectStreamAsync(string videoPath, DetectionOptions? options = null,
        CancellationToken cancellationToken = default) => StreamShots(videoPath, null, options, cancellationToken);

    /// <summary>
    /// As <see cref="DetectStreamAsync(string, DetectionOptions?, CancellationToken)"/>, reading the
    /// video's bytes from <paramref name="video"/>: bytes in, shots out, while both are in progress.
    /// </summary>
    public static IAsyncEnumerable<Shot> DetectStreamAsync(Stream video, DetectionOptions? options = null,
        CancellationToken cancellationToken = default) => StreamShots(null, video, options, cancellationToken);

    static async IAsyncEnumerable<Shot> StreamShots(string? videoPath, Stream? video, DetectionOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<Shot>(new() { SingleReader = true, SingleWriter = true });
        // Also cancelled when the caller stops enumerating, so the decoding thread and ffmpeg stop too.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() =>
        {
            try
            {
                Run(videoPath, video, options, shot => channel.Writer.TryWrite(shot), stop.Token);
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

    static DetectionResult Run(string? videoPath, Stream? videoStream, DetectionOptions? options, Action<Shot>? onShot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var o = options ?? new DetectionOptions();
        if (o.FrameSkip < 0)
            throw new ArgumentException("FrameSkip must be at least 0.");
        if (o.FrameSkip > 0 && o.CollectStats)
            throw new ArgumentException("FrameSkip must be 0 when collecting stats (as in scenedetect).");
        if (o.EndTime is not null && o.Duration is not null)
            throw new ArgumentException("EndTime and Duration cannot both be set.");
        if (o.Downscale is < 1)
            throw new ArgumentException("Downscale must be at least 1.");

        var video = videoStream is not null ? new VideoReader(videoStream, o, cancellationToken) : new VideoReader(videoPath!, o, cancellationToken);
        if (o.StartTime is not null && video.Streaming)
            throw new ArgumentException("StartTime isn't available for a streamed input (a Stream, a URL, or Streaming = true).");
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, video.Fps);

        // One detector per settings entry (the single-detector options are the shorthand); each gets the same frames.
        bool single = o.Detectors is not { Count: > 0 };
        var settings = single ? [new DetectorSettings(o.Detector) { Threshold = o.Threshold }] : o.Detectors!;
        var stats = o.CollectStats ? new Stats() : null;
        var detectors = settings.Select(st => Build(st, o, video, minSceneLen, stats)).ToList();
        // Shots stream out as cuts arrive only when nothing can reorder or change them later.
        bool incremental = detectors.Count == 1 && !o.DropShortScenes && !o.MergeLastScene;

        // Cuts as scenedetect keeps them (its cutting list, later sorted and de-duplicated).
        var cuts = new List<FrameTime>();
        var shots = new List<Shot>();
        var start = video.Position(0);
        FrameTime? lastCut = null;
        void Cut(FrameTime cut)
        {
            cuts.Add(cut);
            if (!incremental)
                return;
            // A cut at frame 0 still makes a (zero-length) first shot, as in scenedetect.
            if (lastCut is { } previous)
            {
                if (SameCut(cut, previous))
                    return; // the same cut twice
                if (CompareCuts(cut, previous) < 0)
                    throw new InvalidOperationException($"Cut at frame {cut.FrameNum} came after one at {previous.FrameNum}.");
            }
            lastCut = cut;
            var shot = new Shot(shots.Count + 1, start, cut);
            shots.Add(shot);
            onShot?.Invoke(shot);
            start = cut;
        }

        // scenedetect's `time -s` seeks (1-based frame numbers); `-e`/`-d` become whole frames at the
        // average rate (FrameTimecode rounds half to even) and are compared as frame numbers.
        int startFrame = o.StartTime is null ? 0 : video.SeekFrame(TimecodeSeconds(o.StartTime, video.Fps, oneBasedFrames: true));
        static long Frames(string value, Fps fps) => (long)Math.Round(TimecodeSeconds(value, fps) * fps.Value);
        long? endFrame = o.EndTime is not null ? Frames(o.EndTime, video.Fps)
            : o.Duration is not null ? Frames(o.Duration, video.Fps) + startFrame
            : null;
        start = video.Position(startFrame);
        int frameCount = 0, stride = o.FrameSkip + 1;
        bool readToEnd = true;
        long lastReport = 0;
        foreach (var frame in video.Frames(startFrame, null, cancellationToken))
        {
            int index = startFrame + frameCount;
            if (frameCount % stride == 0)
                foreach (var detector in detectors)
                    if (detector.ProcessFrame(index, frame) is { } cut)
                        Cut(cut);
            frameCount++;
            if (o.Progress is not null && Environment.TickCount64 - lastReport >= 100)
            {
                o.Progress.Report(new(frameCount, Math.Max(0, video.ExpectedFrames - startFrame)));
                lastReport = Environment.TickCount64;
            }
            // scenedetect analyses a frame, skips the next FrameSkip frames, then stops if the last
            // frame read plus one frame reaches the end (an end hit while skipping counts as EOF).
            if (endFrame is { } e && frameCount % stride == 0 && video.Position(index).PlusFrames(1).FrameNum >= e)
            {
                readToEnd = false;
                break;
            }
        }
        o.Progress?.Report(new(frameCount, frameCount));
        if (frameCount > 0)
        {
            // Where scenedetect finds the stream after the loop: past the end if it read until EOF,
            // otherwise at the last frame read (which may be a skipped one).
            var last = readToEnd ? video.PositionAfterDecoding(startFrame + frameCount) : video.Position(startFrame + frameCount - 1);
            foreach (var detector in detectors)
                if (detector.PostProcess(last) is { } trailingCut)
                    Cut(trailingCut);
            if (incremental)
            {
                var final = new Shot(shots.Count + 1, start, last.PlusFrames(1));
                shots.Add(final);
                onShot?.Invoke(final);
            }
            else
            {
                shots = Scenes(Unique(cuts), start, last.PlusFrames(1), o.MergeLastScene, o.DropShortScenes ? minSceneLen : 0, minSceneLen);
                foreach (var shot in shots)
                    onShot?.Invoke(shot);
            }
        }
        return new(videoPath, video, shots, frameCount, minSceneLen, stats, Unique(cuts));
    }

    static IDetector Build(DetectorSettings st, DetectionOptions o, VideoReader video, int minSceneLen, Stats? stats)
    {
        int frameWindow = st.FrameWindow ?? o.FrameWindow, bins = st.Bins ?? o.Bins, hashSize = st.HashSize ?? o.HashSize,
            hashLowpass = st.HashLowpass ?? o.HashLowpass;
        int? kernelSize = st.KernelSize ?? o.KernelSize;
        if (frameWindow < 1)
            throw new ArgumentException("FrameWindow must be at least 1.");
        if (kernelSize is { } k && (k < 3 || k % 2 == 0))
            throw new ArgumentException("KernelSize must be an odd number >= 3.");
        if (bins is < 1 or > 256)
            throw new ArgumentException("Bins must be between 1 and 256.");
        if (hashSize < 1 || hashLowpass < 1)
            throw new ArgumentException("HashSize and HashLowpass must be at least 1.");
        // --drop-short-scenes: detectors cut freely; the short shots are dropped afterwards.
        int min = o.DropShortScenes ? 0 : st.MinSceneLength is { } m ? MinSceneLengthInFrames(m, video.Fps) : minSceneLen;
        ContentScorer Scorer()
        {
            var w = st.Weights ?? o.Weights;
            var scorer = st.LumaOnly ?? o.LumaOnly ? ContentScorer.LumaOnly() : new ContentScorer(w.Hue, w.Sat, w.Lum, w.Edges);
            // Like PySceneDetect, edges are computed when they're weighted or stats are collected.
            if (scorer.UsesEdges || o.CollectStats)
                scorer.Edges = new EdgeDetector(video.Width, video.Height, kernelSize);
            return scorer;
        }
        IDetector detector = st.Kind switch
        {
            DetectorKind.Content => new ContentDetector(Scorer(), video.Position, video.Fps, st.Threshold ?? 27.0, min,
                (st.FilterMode ?? o.FilterMode) == FlashFilterMode.Suppress),
            DetectorKind.Threshold => new ThresholdDetector(video.Position, video.Fps, st.Threshold ?? 12.0, min, st.FadeBias ?? o.FadeBias, o.Compatibility,
                st.AddLastScene ?? o.AddLastScene),
            DetectorKind.Histogram => new HistogramDetector(video.Position, st.Threshold ?? 0.05, bins, min, o.Compatibility),
            DetectorKind.Hash => new HashDetector(video.Position, st.Threshold ?? 0.395, hashSize, hashLowpass, min),
            _ => new AdaptiveDetector(Scorer(), video.Position, st.Threshold ?? 3.0, min, frameWindow, st.MinContentVal ?? o.MinContentVal),
        };
        detector.Stats = stats;
        if (detector is HashDetector hash)
            hash.SetFrameSize(video.Width, video.Height);
        return detector;
    }

    /// <summary>
    /// FrameTimecode equality: exact times when both are timestamps at the same rate, else frame numbers.
    /// </summary>
    internal static bool SameCut(FrameTime a, FrameTime b) => CompareCuts(a, b) == 0;

    /// <summary>FrameTimecode ordering, as scenedetect sorts its cut list.</summary>
    internal static int CompareCuts(FrameTime a, FrameTime b) =>
        a.TbDen > 0 && b.TbDen > 0 && a.Fps == b.Fps
            ? ((System.Numerics.BigInteger)a.Value * b.TbDen).CompareTo((System.Numerics.BigInteger)b.Value * a.TbDen)
            : a.FrameNum.CompareTo(b.FrameNum);

    /// <summary>sorted(set(cutting_list)): sorted, the first of equal cuts kept.</summary>
    internal static List<FrameTime> Unique(List<FrameTime> cuts)
    {
        var unique = new List<FrameTime>();
        foreach (var cut in cuts)
            if (!unique.Any(u => SameCut(u, cut)))
                unique.Add(cut);
        // A stable sort, so equal-rank cuts keep their order as Python's sorted does.
        return [.. unique.Select((c, i) => (c, i)).OrderBy(x => x.c, Comparer<FrameTime>.Create(CompareCuts)).ThenBy(x => x.i).Select(x => x.c)];
    }

    /// <summary>get_scenes_from_cuts, then the CLI's --merge-last-scene and --drop-short-scenes; numbered from 1.</summary>
    internal static List<Shot> Scenes(List<FrameTime> cuts, FrameTime start, FrameTime end, bool mergeLast, int dropShorterThan, int minSceneLen)
    {
        var scenes = new List<(FrameTime Start, FrameTime End)>();
        var from = start;
        foreach (var cut in cuts)
        {
            scenes.Add((from, cut));
            from = cut;
        }
        scenes.Add((from, end));
        if (mergeLast && minSceneLen > 0 && scenes.Count > 1 && scenes[^1].End.Minus(scenes[^1].Start).FrameNum < minSceneLen)
        {
            scenes[^2] = (scenes[^2].Start, scenes[^1].End);
            scenes.RemoveAt(scenes.Count - 1);
        }
        if (dropShorterThan > 0)
            scenes.RemoveAll(sc => sc.End.Minus(sc.Start).FrameNum < dropShorterThan);
        return [.. scenes.Select((sc, i) => new Shot(i + 1, sc.Start, sc.End))];
    }

    /// <summary>"0.6s" → frames with Python's round-half-to-even, like FrameTimecode._seconds_to_frames; "15" → 15.</summary>
    internal static int MinSceneLengthInFrames(string value, Fps fps) => value.Trim().All(char.IsDigit)
        ? int.Parse(value, CultureInfo.InvariantCulture)
        : (int)Math.Round(TimecodeSeconds(value, fps) * fps.Value);

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
