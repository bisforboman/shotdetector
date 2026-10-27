using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FieldOrder = FrameReader.FieldOrder;
using StreamInfo = FrameReader.StreamInfo;
using StreamType = FrameReader.StreamType;

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
    /// scenedetect 0.7.2 (the default): detect-hist's defaults are threshold 0.20 and 128 bins, detect-hash's 0.35 and
    /// size 8; a minimum length given in frames is compared as a frame count in ContentDetector's flash filter (#577);
    /// AdaptiveDetector measures it from the frame it cuts at (#408); detect-threshold ignores fade-outs shorter than
    /// <see cref="DetectionOptions.MinOutLength"/> (0.1 s by default).
    /// </summary>
    V0_7_2,
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

/// <summary>Whether frames are deinterlaced before analysis (<see cref="DetectionOptions.Deinterlace"/>).</summary>
public enum DeinterlaceMode
{
    /// <summary>Never (the default; as scenedetect).</summary>
    Off,

    /// <summary>Always, with ffmpeg's yadif.</summary>
    On,

    /// <summary>When the video stream is flagged interlaced (its field order is not progressive or unknown).</summary>
    Auto,
}

/// <summary>Whether a file is scored in chunks on several cores at once (<see cref="DetectionOptions.Mode"/>).</summary>
public enum DetectionMode
{
    /// <summary>
    /// Parallel for <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/> and DetectAsync from
    /// 4 CPUs on and for frames up to 3840 x 2160, sequential otherwise and for DetectStreamAsync, so its shots still
    /// come as they are found (the default).
    /// </summary>
    Auto,

    /// <summary>One pass from the start: one decoder, shots as they come.</summary>
    Sequential,

    /// <summary>
    /// In chunks wherever it can (see <see cref="DetectionOptions.Mode"/>), DetectStreamAsync too, whose shots then all
    /// come at the end.
    /// </summary>
    Parallel,
}

/// <summary>
/// A video's properties from its headers (<see cref="ShotDetection.Probe(string, DetectionOptions?, CancellationToken)"/>):
/// fast, without reading the packets.
/// </summary>
public sealed record VideoInfo
{
    internal VideoInfo()
    {
    }

    /// <summary>Width as shown (a 90° rotation tag swaps it with the height, as decoding does).</summary>
    public int Width { get; init; }

    /// <summary>Height as shown.</summary>
    public int Height { get; init; }

    /// <summary>The frame rate detection uses (the average rate, as OpenCV reports it).</summary>
    public Fps FrameRate { get; init; }

    /// <summary>The frame count scenedetect takes as the video's length; null if the headers don't say.</summary>
    public long? OpenCvFrameCount { get; init; }

    /// <summary>The container's duration; null if unknown.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The video codec's FFmpeg name (h264, hevc, mpeg2video, ...).</summary>
    public string Codec { get; init; } = "";

    /// <summary>FFmpeg's pixel format name (yuv420p, yuv420p10le, ...).</summary>
    public string PixelFormat { get; init; } = "";

    /// <summary>Progressive, interlaced (which field first), or unknown.</summary>
    public FieldOrder FieldOrder { get; init; }

    /// <summary>The display rotation in degrees, 0 if none.</summary>
    public int Rotation { get; init; }

    /// <summary>FFmpeg's name for the container (its demuxer): mxf, matroska,webm, mov,mp4,m4a,3gp,3g2,mj2, ...</summary>
    public string Container { get; init; } = "";

    /// <summary>Whether the file has an audio stream; null for a <see cref="System.IO.Stream"/>.</summary>
    public bool? HasAudio { get; init; }

    /// <summary>Every stream of the file or URL (video, audio, subtitles, data), in the container's order; null for a <see cref="System.IO.Stream"/>.</summary>
    public IReadOnlyList<StreamInfo>? Streams { get; init; }

    /// <summary>Whether the stream is flagged interlaced (what <see cref="DeinterlaceMode.Auto"/> goes by).</summary>
    public bool IsInterlaced => FieldOrder is FieldOrder.TopFirst or FieldOrder.BottomFirst
        or FieldOrder.TopCodedBottomFirst or FieldOrder.BottomCodedTopFirst;
}

/// <summary>How frames are decoded.</summary>
public enum VideoDecoder
{
    /// <summary>The ffmpeg executable, frames through a pipe (any ffmpeg version on PATH).</summary>
    FfmpegProcess,

    /// <summary>
    /// FFmpeg's libraries in this process (they must be FFmpeg 8's shared libraries, else an error): the same frames,
    /// faster and with less memory, without a second process. URLs, codecs the libraries have no decoder for and
    /// rotations other than quarter turns still use the executable.
    /// </summary>
    InProcess,

    /// <summary>
    /// The default: <see cref="InProcess"/> when FFmpeg 8's shared libraries load (from
    /// <see cref="DetectionOptions.FfmpegDirectory"/>, else the system's usual places), else <see cref="FfmpegProcess"/>.
    /// The results are the same either way. A file the libraries give no frames of, and a URL they can't open, go to the
    /// executable, except an http or https URL (when the libraries have its protocol): its failure is the error, since
    /// the executable doesn't check the certificates of the https segments, keys and variants a playlist names
    /// (<see cref="FfmpegProcess"/> still uses it).
    /// </summary>
    Auto,
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

    /// <summary>Threshold: see <see cref="DetectionOptions.MinOutLength"/>.</summary>
    public string? MinOutLength { get; init; }

    /// <summary><see cref="MinSceneLength"/> as a time; not both.</summary>
    public TimeSpan? MinSceneDuration { get; init; }

    /// <summary><see cref="MinOutLength"/> as a time; not both.</summary>
    public TimeSpan? MinOutDuration { get; init; }

    /// <summary>The settings with the typed times written into their strings.</summary>
    internal DetectorSettings Resolved() => this with
    {
        MinSceneLength = DetectionOptions.Either(MinSceneLength, null, MinSceneDuration, nameof(MinSceneLength), nameof(MinSceneDuration)),
        MinOutLength = DetectionOptions.Either(MinOutLength, null, MinOutDuration, nameof(MinOutLength), nameof(MinOutDuration)),
        MinSceneDuration = null,
        MinOutDuration = null,
    };
}

/// <summary>Detection settings. Defaults match the scenedetect CLI.</summary>
public sealed record DetectionOptions
{
    /// <summary>Which PySceneDetect release to reproduce (default 0.7.2, the newest).</summary>
    public PySceneDetectVersion Compatibility { get; init; } = PySceneDetectVersion.V0_7_2;

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

    /// <summary>Histogram: number of luma histogram bins (1..256); 0 (default): scenedetect's for <see cref="Compatibility"/> (128 in 0.7.2, 256 in 0.7.1).</summary>
    public int Bins { get; init; }

    /// <summary>Hash: the hash is HashSize×HashSize bits; 0 (default): scenedetect's for <see cref="Compatibility"/> (8 in 0.7.2, 16 in 0.7.1).</summary>
    public int HashSize { get; init; }

    /// <summary>Hash: frames are shrunk to HashSize×HashLowpass pixels on each side before hashing.</summary>
    public int HashLowpass { get; init; } = 2;

    /// <summary>Threshold: cut position between fade-out (-1) and fade-in (+1).</summary>
    public double FadeBias { get; init; }

    /// <summary>
    /// Threshold: a fade-out shorter than this makes no cut (scenedetect 0.7.2's <c>--min-out-length</c>; same formats as
    /// <see cref="MinSceneLength"/>). Null (default): the CLI's, 0.1 s with 0.7.2 and none with 0.7.1.
    /// </summary>
    public string? MinOutLength { get; init; }

    /// <summary>
    /// Where to start, like scenedetect's `time -s`: "HH:MM:SS[.mmm]", seconds ("12.5s" or "12.5")
    /// or a 1-based frame number ("300"). Null: the first frame.
    /// </summary>
    public string? StartTime { get; init; }

    /// <summary>Where to stop (exclusive), like `time -e`; same formats, frame numbers are 0-based here as in scenedetect.</summary>
    public string? EndTime { get; init; }

    /// <summary>How much to analyse from the start, like `time -d`; same formats. Not with <see cref="EndTime"/>.</summary>
    public string? RangeDuration { get; init; }

    /// <summary><see cref="StartTime"/> as a time; not both.</summary>
    public TimeSpan? Start { get; init; }

    /// <summary><see cref="EndTime"/> as a time; not both.</summary>
    public TimeSpan? End { get; init; }

    /// <summary><see cref="RangeDuration"/> as a time; not both.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary><see cref="MinSceneLength"/> as a time; not both (the string's default aside).</summary>
    public TimeSpan? MinSceneDuration { get; init; }

    /// <summary><see cref="MinOutLength"/> as a time; not both.</summary>
    public TimeSpan? MinOutDuration { get; init; }

    /// <summary>The options with the typed times written into their strings, which the detection reads.</summary>
    internal DetectionOptions Resolved() => this with
    {
        StartTime = Either(StartTime, null, Start, nameof(StartTime), nameof(Start)),
        EndTime = Either(EndTime, null, End, nameof(EndTime), nameof(End)),
        RangeDuration = Either(RangeDuration, null, Duration, nameof(RangeDuration), nameof(Duration)),
        MinSceneLength = Either(MinSceneLength, "0.6s", MinSceneDuration, nameof(MinSceneLength), nameof(MinSceneDuration))!,
        MinOutLength = Either(MinOutLength, null, MinOutDuration, nameof(MinOutLength), nameof(MinOutDuration)),
        Detectors = Detectors?.Select(d => d.Resolved()).ToList(),
        Start = null,
        End = null,
        Duration = null,
        MinSceneDuration = null,
        MinOutDuration = null,
    };

    /// <summary>
    /// A time option's string: the typed value as seconds ("12.5s", exact to the tick) when set, else the string as
    /// given; both set (the string not at its default) is an error.
    /// </summary>
    internal static string? Either(string? text, string? textDefault, TimeSpan? time, string textName, string timeName)
    {
        if (time is not { } t)
            return text;
        if (text != textDefault)
            throw new ArgumentException($"{textName} and {timeName} are the same option; set one.");
        if (t < TimeSpan.Zero)
            throw new ArgumentException($"{timeName} can't be negative.");
        return t.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture) + "s";
    }

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

    /// <summary>Tests: called each time a reader probes the file (not when it takes another reader's probe).</summary>
    internal Action? OnProbe { get; init; }

    /// <summary>Tests: called as a reader starts its decoding pass for per-frame timestamps (packets without a pts).</summary>
    internal Action? OnFrameTimestamps { get; init; }

    /// <summary>
    /// Tests: take the decoding pass for per-frame timestamps (packets without a pts) as the reader is made, as before
    /// they came with the frames in-process (same results).
    /// </summary>
    internal bool FrameTimestampsFirst { get; init; }

    /// <summary>
    /// compare.py on Windows (the CLI's hidden <c>--always-bt601</c>): convert with BT.601 coefficients whatever the
    /// colour tags say, as the Windows OpenCV wheel does (docs/decisions/); decodes with the ffmpeg executable.
    /// </summary>
    internal bool AlwaysBt601 { get; init; }

    /// <summary>Let ffmpeg downscale (faster, but results can differ from PySceneDetect).</summary>
    public bool FfmpegResize { get; init; }

    /// <summary>
    /// Deinterlace with ffmpeg's yadif filter (default settings, one frame out per frame in) before analysis, for
    /// interlaced sources such as 1080i broadcast: <see cref="DeinterlaceMode.On"/> always,
    /// <see cref="DeinterlaceMode.Auto"/> when the stream is flagged interlaced. Results match scenedetect run on a
    /// lossless copy made with <c>ffmpeg -vf yadif</c>. Only detection sees deinterlaced frames; exported images and
    /// clips are the original's.
    /// </summary>
    public DeinterlaceMode Deinterlace { get; init; }

    /// <summary>
    /// Decoder threads; 0 = FFmpeg's choice. Null (the default) is OpenCV's: the CPUs available, at most 16. Damaged
    /// video decodes like scenedetect's only with that count (concealment depends on it); clean video with any. Read in
    /// chunks (<see cref="Mode"/>), the count is shared out among the chunks: by default twice the CPUs (at most 16) for
    /// a decoder that decodes several frames at once (h264, HEVC) with frames up to 1920 x 1080, so each chunk's decoder
    /// has up to the CPUs' count.
    /// </summary>
    public int? DecodeThreads { get; init; }

    /// <summary>
    /// Whether a long local file is detected on several cores at once, with exactly the result of a sequential run:
    /// chunks of it are scored at the same time, each from one frame early, then the cut decisions run once over all
    /// scores. Half the CPUs, at most 4 chunks (no more than <see cref="DecodeThreads"/> when set), for files of two
    /// minutes or more (each chunk at least a minute). Only for files decoded in-process, without a time range or frame
    /// skip and not MPEG-PS; anything a chunk can't reproduce (a seek that lands late, dropped frames, a size change)
    /// makes it read the file sequentially instead, from the start. <see cref="DetectedVideo.Chunks"/> says which ran,
    /// <see cref="DetectedVideo.ChunkFallback"/> why it fell back (also in <see cref="DetectionResult.Warnings"/> and
    /// <see cref="DetectionOptions.Warnings"/>, as is why <see cref="DetectionMode.Parallel"/> read sequentially). The decoder threads are shared out among the chunks.
    /// Memory grows by a decoder per chunk (they share the frame buffers). <see cref="DetectionMode.Auto"/> chunks
    /// for Detect from 4 CPUs on and for frames up to 3840 x 2160, but not for DetectStreamAsync, whose shots would
    /// otherwise all come once the whole file is scored; <see cref="DetectionMode.Parallel"/> chunks from 4 CPUs on, at
    /// any frame size, and DetectStreamAsync too; <see cref="DetectionMode.Sequential"/> never does.
    /// </summary>
    public DetectionMode Mode { get; init; }

    /// <summary>
    /// The opt-in switch from before parallel detection became the default: true is <see cref="DetectionMode.Parallel"/>,
    /// false <see cref="DetectionMode.Sequential"/>; it reads true only for <see cref="DetectionMode.Parallel"/>.
    /// </summary>
    [Obsolete("Use Mode = DetectionMode.Parallel.")]
    public bool Parallel
    {
        get => Mode == DetectionMode.Parallel;
        init => Mode = value ? DetectionMode.Parallel : DetectionMode.Sequential;
    }

    /// <summary>When chunking: this many chunks instead of the automatic count (tests, the CLI's hidden --chunks).</summary>
    internal int? Chunks { get; init; }

    /// <summary>
    /// The in-process reader's frame buffers between its decoding thread and the caller's: 8, fewer per chunk
    /// (<see cref="ShotDetection.ChunkBuffers"/>).
    /// </summary>
    internal int FrameBuffers { get; init; } = 8;

    /// <summary>
    /// Threads converting the in-process reader's frames apart from its decoding thread: 0 converts on the decoding
    /// thread; 1 or more, on one conversion thread that takes the decoded frames (FrameDecoder.TakeFrame), except with
    /// row-wise yadif (<see cref="Yadif"/>). Same frames either way. Unset (null), the reader picks
    /// (<see cref="VideoReader.DefaultConvertThreads"/>): 1 for HD video read sequentially, files, streams and URLs
    /// alike, 0 for smaller video; a parallel run's chunks convert on their decoding threads
    /// (<see cref="ShotDetection.ChunkOptions"/>). Set for tests, timings and the CLI's hidden --convert-threads.
    /// </summary>
    internal int? ConvertThreads { get; init; }

    /// <summary><see cref="Mode"/> for a run: Auto is parallel, except when the shots are streamed.</summary>
    internal DetectionMode ResolvedMode(bool streaming) => Mode switch
    {
        DetectionMode.Auto => streaming ? DetectionMode.Sequential : DetectionMode.Parallel,
        var mode => mode,
    };

    /// <summary>
    /// Enables the YUV fast path for yuv420p, and for yuv422p when the converter is also an
    /// <see cref="IYuv422Converter"/>: much faster on large video through the ffmpeg executable, same results, and x86's
    /// results on ARM. Also for yuv420p10le when it is an <see cref="IYuv420P10Converter"/>: x86's results on ARM, the
    /// same results on x64 (where it is slower than the default path on small video). Use SwscaleYuv420 from the
    /// ShotDetector.FastYuv package (LGPL), which implements all three.
    /// </summary>
    public IYuv420Converter? Yuv420Converter { get; init; }

    /// <summary>
    /// yadif for single rows (the ShotDetector.FastYuv package's <c>Yadif</c>, or any FrameReader
    /// <see cref="FrameReader.IRowDeinterlacer"/>): in-process deinterlacing then computes only the rows the resize reads,
    /// instead of FFmpeg's yadif on whole frames. 8-bit results are identical either way. For 9 to 16 bits, FFmpeg 8.1's
    /// x86 SIMD yadif gives a different result on every run (its C code doesn't); FastYuv's follows the C code, so it is
    /// deterministic and equal to FFmpeg's yadif with SIMD off (-cpuflags 0).
    /// </summary>
    public FrameReader.IRowDeinterlacer? Yadif { get; init; }

    /// <summary>Folder containing ffmpeg and ffprobe; null (default) finds them on PATH. With <see cref="VideoDecoder.InProcess"/>, also where FFmpeg's shared libraries are.</summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>How frames are decoded: in this process when FFmpeg 8's libraries are there (default), or by the ffmpeg executable.</summary>
    public VideoDecoder Decoder { get; init; } = VideoDecoder.Auto;

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
    /// Receives progress while frames are decoded: a first report before any frame, then about 10 times a second (or
    /// per <see cref="ProgressStep"/>), and once at the end. With <see cref="System.Progress{T}"/>, reports arrive on the
    /// thread that created it.
    /// </summary>
    public IProgress<DetectionProgress>? Progress { get; init; }

    /// <summary>
    /// Told each warning (<see cref="DetectionResult.Warnings"/>' sentences, in the same order) once, as soon as it is
    /// known: an http or https HLS playlist decoded by the ffmpeg executable right after the probe, and why
    /// <see cref="DetectionMode.Parallel"/> reads sequentially before the first frame, both before any shot; a fallback
    /// from scoring in chunks when it happens, before the sequential read. The only way
    /// <see cref="ShotDetection.DetectStreamAsync(string, DetectionOptions?, CancellationToken)"/> has to tell them.
    /// Reported synchronously on the thread that runs the detection (DetectStreamAsync's background thread); with
    /// <see cref="System.Progress{T}"/>, reports arrive on the thread that created it, so possibly after a shot.
    /// </summary>
    public IProgress<string>? Warnings { get; init; }

    /// <summary>
    /// Told each shot (<see cref="DetectionResult.Shots"/>, in the same order) once, as soon as it is known, by every
    /// overload of <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/>, DetectAsync and
    /// DetectStreamAsync (a file path, a Stream, the caller's frames). With one detector and neither
    /// <see cref="DropShortScenes"/> nor <see cref="MergeLastScene"/>, a shot is told once the cut that ends it is
    /// confirmed (a few frames later: the adaptive detector's window, the minimum shot length), while the rest of the
    /// video is still being read, and the last one at the end; otherwise all of them at the end, as a later cut can still
    /// change them. A file scored in chunks (<see cref="DetectionMode.Parallel"/>, and <see cref="DetectionMode.Auto"/>
    /// in Detect) tells them in order once every chunk is scored; <see cref="DetectionMode.Sequential"/> tells them while
    /// decoding. Setting it changes neither the shots nor how the video is read. DetectStreamAsync tells each shot here,
    /// then yields it. Reported synchronously on the thread that runs the detection (DetectAsync's and DetectStreamAsync's
    /// background thread), so every shot is told before Detect returns; with <see cref="System.Progress{T}"/>, reports
    /// arrive on the thread that created it, possibly later. A run that fails or is cancelled has told the shots known by
    /// then. One exception to "the result's shots": timestamps that start over (two streams joined) make the cuts come out
    /// of order, and the shots told before that aren't told again from the sorted cuts.
    /// </summary>
    public IProgress<Shot>? OnShot { get; init; }

    /// <summary>
    /// Report <see cref="Progress"/> only when the fraction done crosses a multiple of this (0.1: at 10 %, 20 %, ...),
    /// besides the first report and the last: one line per step for a service log, with no throttling of your own.
    /// When the frame count is unknown (streamed input), just the first and the last. Above 0 and at most 1; null (the
    /// default): about 10 times a second.
    /// </summary>
    public double? ProgressStep { get; init; }

    /// <summary>Record per-frame metrics in <see cref="DetectionResult.Stats"/> (like scenedetect -s).</summary>
    public bool CollectStats { get; init; }

    /// <summary>
    /// Leave out reference tone (1 kHz line-up tone, <see cref="ShotDetection.FindReferenceTone"/>) at the start and
    /// end: detection starts after a segment that begins in the first 5 s, and stops before one that ends in the last
    /// 5 s; the segments found are in <see cref="DetectionResult.ReferenceTone"/>. Not in scenedetect. Needs a file
    /// path and FFmpeg's libraries (the audio is read in-process), and sets the time range itself: not with
    /// <see cref="StartTime"/>, <see cref="EndTime"/> or <see cref="RangeDuration"/>.
    /// </summary>
    public bool TrimReferenceTone { get; init; }

    /// <summary>
    /// What counts as reference tone (<see cref="ShotDetection.FindReferenceTone"/>): frequency, shortest run (5 s),
    /// longest break, and how much audio from the start is searched (e.g. 180 s: line-up tone is at the start, and a
    /// feature film's whole soundtrack takes a while; with <see cref="TrimReferenceTone"/>, tone at the end is then not
    /// found).
    /// </summary>
    public FrameReader.ReferenceToneOptions ReferenceTone { get; init; } = new();
}

/// <summary>How far <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/> has got.</summary>
public readonly record struct DetectionProgress
{
    /// <summary>Frames decoded and analysed so far.</summary>
    public int FramesProcessed { get; init; }

    /// <summary>Frames the video is expected to have (from its packets); 0 if unknown.</summary>
    public int ExpectedFrames { get; init; }

    /// <summary>Whether frames are decoded in-process (FFmpeg's libraries) rather than by the ffmpeg executable.</summary>
    public bool DecodesInProcess { get; init; }

    /// <summary>0..1, or null when the frame count is unknown.</summary>
    public double? Fraction => ExpectedFrames > 0 ? Math.Min(1.0, FramesProcessed / (double)ExpectedFrames) : null;
}

/// <summary>The outcome of <see cref="ShotDetection.Detect(string, DetectionOptions?, CancellationToken)"/>.</summary>
public sealed record DetectionResult
{
    internal DetectionResult()
    {
    }

    /// <summary>The video analysed: a path or URL, or null for a Stream input (exports need a path).</summary>
    public string? VideoPath { get; init; }

    /// <summary>Its properties (size, frame rate, ...).</summary>
    public DetectedVideo Video { get; init; } = null!;

    /// <summary>The shots, in order, covering the whole video.</summary>
    public IReadOnlyList<Shot> Shots { get; init; } = [];

    /// <summary>Frames decoded.</summary>
    public int FrameCount { get; init; }

    /// <summary><see cref="DetectionOptions.MinSceneLength"/> in frames of this video.</summary>
    public int MinSceneLengthFrames { get; init; }

    /// <summary>Per-frame metrics when <see cref="DetectionOptions.CollectStats"/> was set, else null.</summary>
    public Stats? Stats { get; init; }

    /// <summary>Every cut, sorted: the shot starts after the first, unless shots were dropped or merged (scenedetect's cut list, which its CSV's first row, the HTML page and the QP file use).</summary>
    public IReadOnlyList<FrameTime> Cuts { get; init; } = [];

    /// <summary>The reference tone found with <see cref="DetectionOptions.TrimReferenceTone"/>, else null.</summary>
    public IReadOnlyList<FrameReader.ToneSegment>? ReferenceTone { get; init; }

    /// <summary>
    /// What the caller should know about how the video was read, one sentence each; empty when nothing (never null).
    /// So far: an http or https HLS playlist decoded by the ffmpeg executable, which fetches its https segments without
    /// checking their certificates (in-process decoding checks them); scoring in chunks that fell back to a sequential read
    /// (<see cref="DetectedVideo.ChunkFallback"/>); <see cref="DetectionMode.Parallel"/> asked for and the file read
    /// sequentially all the same, and why (fewer than 4 CPUs, shorter than two minutes, a time range, ...).
    /// <see cref="DetectionOptions.Warnings"/> is told each one as soon as it is known, also by
    /// <see cref="ShotDetection.DetectStreamAsync(string, DetectionOptions?, CancellationToken)"/>, which has no result.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Runs shot detection on a video file. Failures of the input, ffmpeg or FFmpeg's libraries are a
/// <see cref="ShotDetectionException"/> from every method here (and from <see cref="Export"/>), never a FrameReader
/// exception: one that happens inside (decoding in-process, reading the audio) is its InnerException, with the same
/// message. Invalid options are an <see cref="ArgumentException"/>; cancelling, <see cref="OperationCanceledException"/>.
/// </summary>
public static partial class ShotDetection
{
    /// <summary>
    /// The <see cref="System.Diagnostics.ActivitySource"/> name: each detection is one span ("ShotDetection.Detect")
    /// with the video's size, frame rate, pipeline and decoder, and the frames and shots found. For OpenTelemetry,
    /// <c>.AddSource(ShotDetection.ActivitySourceName)</c>.
    /// </summary>
    public const string ActivitySourceName = "ShotDetector";

    /// <summary>
    /// A video's properties from its headers: size, frame rate, frame count, duration, codec, pixel format, field
    /// order (to decide <see cref="DetectionOptions.Deinterlace"/>, or leave it to <see cref="DeinterlaceMode.Auto"/>)
    /// and rotation. Quick: it doesn't read the packets. In-process when FFmpeg's libraries load, else with ffprobe.
    /// </summary>
    /// <param name="videoPath">A file path or URL.</param>
    /// <param name="options">Where ffmpeg is and how to decode (<see cref="DetectionOptions.FfmpegDirectory"/>,
    /// <see cref="DetectionOptions.Decoder"/>), and the compatibility mode and frame rate override, which the reported
    /// frame rate follows.</param>
    /// <param name="cancellationToken">Cancels probing.</param>
    public static VideoInfo Probe(string videoPath, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        VideoReader.Headers(videoPath, options, cancellationToken).Info;

    /// <summary>
    /// Whether FFmpeg 8's shared libraries load, so <see cref="VideoDecoder.Auto"/> decodes in-process: a cheap check for
    /// a worker's startup. Looks where detection does (<paramref name="ffmpegDirectory"/>, else next to the app, else
    /// the system's usual places); once loaded they stay loaded for the process.
    /// </summary>
    /// <param name="ffmpegDirectory">As <see cref="DetectionOptions.FfmpegDirectory"/>.</param>
    public static bool CanDecodeInProcess(string? ffmpegDirectory = null) => InProcess.CanLoad(ffmpegDirectory);

    /// <summary><see cref="Probe(string, DetectionOptions?, CancellationToken)"/> on a background thread.</summary>
    /// <param name="videoPath">A file path or URL.</param>
    /// <param name="options">See <see cref="Probe(string, DetectionOptions?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels probing.</param>
    public static Task<VideoInfo> ProbeAsync(string videoPath, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Probe(videoPath, options, cancellationToken), cancellationToken);

    static readonly System.Diagnostics.ActivitySource Tracing =
        new(ActivitySourceName, typeof(ShotDetection).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// Finds reference tone (line-up or alignment tone: a steady 1 kHz sine, often with colour bars, before or after
    /// the programme) in a file's audio, a video's or an audio-only file's. A run counts when it is mostly 1 kHz
    /// (about ±15 Hz) at a steady level for at least the options' <see cref="FrameReader.ReferenceToneOptions.MinDuration"/>; breaks up
    /// to 1 s (line-up conventions that cut one channel now and then) don't split it. Music, beeps and held notes don't
    /// count. Not in scenedetect: nothing here changes detection unless <see cref="DetectionOptions.TrimReferenceTone"/>
    /// is set. Reads the audio in-process with FFmpeg's libraries.
    /// </summary>
    /// <param name="path">A video or audio file.</param>
    /// <param name="options">Uses <see cref="DetectionOptions.ReferenceTone"/> and <see cref="DetectionOptions.FfmpegDirectory"/>.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The segments, in order; empty when there's none.</returns>
    /// <exception cref="ShotDetectionException">FFmpeg's libraries didn't load, or the file can't be read or has no audio.</exception>
    public static IReadOnlyList<FrameReader.ToneSegment> FindReferenceTone(string path, DetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new DetectionOptions();
        try
        {
            InProcess.Load(o.FfmpegDirectory);
            using var audio = new FrameReader.AudioReader(path);
            return FrameReader.ReferenceTone.Find(audio, o.ReferenceTone, cancellationToken);
        }
        catch (FrameReader.FrameReaderException e)
        {
            throw InProcess.Map(e, o.FfmpegDirectory);
        }
    }

    /// <summary>
    /// Decodes <paramref name="videoPath"/> with ffmpeg and returns its shots, as
    /// `scenedetect -i video detect-... list-scenes` would.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="ShotDetectionException">The input, ffmpeg or FFmpeg's libraries failed; <see cref="ShotDetectionException.Reason"/>
    /// says which (a FrameReader failure is its InnerException).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; ffmpeg is stopped.</exception>
    public static DetectionResult Detect(string videoPath, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(videoPath, null, options, onShot: null, cancellationToken);

    /// <summary>
    /// As <see cref="Detect(string, DetectionOptions?, CancellationToken)"/>, reading the video's bytes
    /// from <paramref name="video"/> (in-process with FFmpeg's libraries, else piped into ffmpeg; no temporary file) when the container headers come
    /// first: mkv, webm, ts, mov, or an mp4 written with "faststart". An mp4 with its headers last is copied to a
    /// temporary file first, deleted afterwards, and read as a file.
    /// </summary>
    public static DetectionResult Detect(Stream video, DetectionOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(null, video, options, onShot: null, cancellationToken);

    /// <summary>
    /// Detects shots on frames the caller decoded, without FFmpeg: nothing here loads FFmpeg's libraries or starts
    /// ffmpeg or ffprobe, so it runs where neither exists (browser WebAssembly, with a managed decoder). The frames are
    /// converted with <see cref="DetectionOptions.Yuv420Converter"/>, which is required (SwscaleYuv420 from the
    /// ShotDetector.FastYuv package: there is no other conversion without FFmpeg), then analysed as a file's. Given the
    /// frames, timestamps and properties FFmpeg gives for a file, the result is the one
    /// <see cref="Detect(string, DetectionOptions?, CancellationToken)"/> gives for it with the same converter: shots,
    /// cuts, stats and their CSVs, byte for byte.
    /// </summary>
    /// <param name="video">The video's properties, as its headers give them (FFmpeg's values, for a file's results).</param>
    /// <param name="frames">The decoded frames in presentation order, every frame from the first.</param>
    /// <param name="options">Detection settings; see the remarks for which apply.</param>
    /// <param name="cancellationToken">Stops between frames.</param>
    /// <exception cref="ArgumentException">Invalid options, an unsupported video, or a frame's plane smaller than the frame.</exception>
    /// <exception cref="ShotDetectionException">The frames don't come at a constant frame rate (<see cref="ShotDetectionError.InvalidInput"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// So far 8-bit 4:2:0 (yuv420p, yuvj420p) of an even height, at a constant frame rate (checked, with
    /// <see cref="YuvVideo.TimeBase"/> set: a frame more than half a frame from where the rate puts it is an error), at
    /// one size, upright (no rotation is applied). Options: the detectors' settings, stats, the time range
    /// (<see cref="DetectionOptions.StartTime"/>: the frames before it are still enumerated, not converted), frame skip,
    /// crop, downscale, the frame rate override, merge and drop of short shots, progress, warnings and
    /// <see cref="DetectionOptions.OnShot"/> (each shot as soon as it is known, while frames are still pulled). Not available
    /// (an <see cref="ArgumentException"/>): <see cref="DetectionOptions.Deinterlace"/>, <see cref="DetectionOptions.Yadif"/>,
    /// <see cref="DetectionOptions.FfmpegResize"/>, <see cref="DetectionOptions.TrimReferenceTone"/>. Ignored, as nothing
    /// is decoded here: <see cref="DetectionOptions.Decoder"/>, <see cref="DetectionOptions.FfmpegDirectory"/>,
    /// <see cref="DetectionOptions.DecodeThreads"/>, <see cref="DetectionOptions.Streaming"/>,
    /// <see cref="DetectionOptions.ProbeBytes"/>; the frames are read once, in order (<see cref="DetectionMode.Parallel"/>
    /// is told why it read sequentially). The result has no <see cref="DetectionResult.VideoPath"/>, so the exports that
    /// cut the file (images, clips) aren't available; the CSVs, timelines and stats are.
    /// </remarks>
    public static DetectionResult Detect(YuvVideo video, IEnumerable<YuvFrame> frames, DetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(frames);
        using var e = frames.GetEnumerator();
        var feed = new FrameFeed();
        return Run(null,
            null,
            options,
            onShot: null,
            cancellationToken,
            o => new VideoReader(video, feed, o),
            () =>
            {
                if (feed.NeedsFrame)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // before each frame is pulled
                    if (e.MoveNext())
                        feed.Put(e.Current);
                    else
                        feed.End();
                }
            });
    }

    /// <summary>
    /// <see cref="Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/> on frames from an
    /// asynchronous source: the same options, refusals and result (shots, cuts, stats and their CSVs, byte for byte), the
    /// frames pulled the same way (one at a time, none ahead). It runs on the caller's own awaits, never on another thread
    /// (no <see cref="Task.Run(Action)"/>): between frames it awaits <paramref name="frames"/>, so on a single-threaded host
    /// (browser WebAssembly, a web worker) the event loop runs whenever the source yields to it, and a cancel arriving there
    /// stops the run at the next frame. A source that never yields runs the whole detection before this returns.
    /// </summary>
    /// <param name="video">The video's properties, as its headers give them (FFmpeg's values, for a file's results).</param>
    /// <param name="frames">The decoded frames in presentation order, every frame from the first; enumerated once, with
    /// <paramref name="cancellationToken"/>, and disposed when the run ends, fails or is cancelled.</param>
    /// <param name="options">Detection settings, as for the synchronous overload. <see cref="DetectionOptions.Progress"/>,
    /// <see cref="DetectionOptions.Warnings"/> and <see cref="DetectionOptions.OnShot"/> are told from inside the run, on
    /// whichever thread continues after the source's await.</param>
    /// <param name="cancellationToken">Checked before each frame is pulled.</param>
    /// <returns>The detection's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="video"/> or <paramref name="frames"/> is null (thrown at once).</exception>
    /// <exception cref="ArgumentException">Invalid options, an unsupported video, or a frame's plane smaller than the frame (in the task).</exception>
    /// <exception cref="ShotDetectionException">The frames don't come at a constant frame rate (<see cref="ShotDetectionError.InvalidInput"/>, in the task).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (in the task).</exception>
    public static Task<DetectionResult> DetectAsync(YuvVideo video,
        IAsyncEnumerable<YuvFrame> frames,
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(frames);
        return RunAsync(video, frames, options, cancellationToken);
    }

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
    public static DetectionResult LoadScenes(string videoPath,
        string sceneListCsv,
        string column = "Start Frame",
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var o = (options ?? new DetectionOptions()).Resolved();
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
            ? FrameTime.Frame(Math.Max(0, long.Parse(value, CultureInfo.InvariantCulture) - shift), fps)
            : FrameTime.FromSeconds(TimecodeSeconds(value, fps), fps);

        // Start Frame values are 1-based in the CLI's output.
        FrameTime Parse(string value) => Time(value, shift: 1);
        var starts = new List<FrameTime>();
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            if (line.Length > 0 && CsvRow(line) is var row && col < row.Count)
                starts.Add(Parse(row[col]));
        }

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
            : o.RangeDuration is { } d ? Min(start.Plus(Time(d.Trim())))
            : duration;
        cuts = [.. cuts.Where(c => c.FrameNum < end.FrameNum)];
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, fps);
        var shots = Scenes(cuts, start, end, o.MergeLastScene, o.DropShortScenes ? minSceneLen : 0, minSceneLen);
        return new()
        {
            VideoPath = videoPath,
            Video = new DetectedVideo(video),
            Shots = shots,
            FrameCount = (int)video.OpenCvFrameCount,
            MinSceneLengthFrames = minSceneLen,
            Stats = null,
            Cuts = cuts,
        };
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
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
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
    /// Stopping the enumeration early, or cancelling, stops ffmpeg. There is no result: warnings
    /// (<see cref="DetectionResult.Warnings"/>) go to <see cref="DetectionOptions.Warnings"/> as soon as they are known.
    /// A <see cref="DetectionOptions.OnShot"/> is told each shot too, just before it is yielded.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid options.</exception>
    /// <exception cref="ShotDetectionException">The input, ffmpeg or FFmpeg's libraries failed; <see cref="ShotDetectionException.Reason"/>
    /// says which (a FrameReader failure is its InnerException).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; ffmpeg is stopped.</exception>
    public static IAsyncEnumerable<Shot> DetectStreamAsync(string videoPath,
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default) => StreamShots(videoPath, null, options, cancellationToken);

    /// <summary>
    /// As <see cref="DetectStreamAsync(string, DetectionOptions?, CancellationToken)"/>, reading the
    /// video's bytes from <paramref name="video"/>: bytes in, shots out, while both are in progress.
    /// </summary>
    public static IAsyncEnumerable<Shot> DetectStreamAsync(Stream video,
        DetectionOptions? options = null,
        CancellationToken cancellationToken = default) => StreamShots(null, video, options, cancellationToken);

    static async IAsyncEnumerable<Shot> StreamShots(string? videoPath,
        Stream? video,
        DetectionOptions? options,
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
        },
            CancellationToken.None);
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

    static DetectionResult Run(string? videoPath,
        Stream? videoStream,
        DetectionOptions? options,
        Action<Shot>? onShot,
        CancellationToken cancellationToken,
        Func<DetectionOptions, VideoReader>? frames = null,
        Action? beforeStep = null)
    {
        using var activity = Tracing.StartActivity("ShotDetection.Detect");
        try
        {
            // The steps to the end; frames from the caller go into the feed before each step but the first.
            DetectionResult result;
            using (var steps = Steps(videoPath, videoStream, options, onShot, cancellationToken, frames).GetEnumerator())
            {
                while (!Stepped(steps, out result))
                    beforeStep?.Invoke();
            }

            Traced(activity, result);
            return result;
        }
        catch (Exception e)
        {
            Failed(activity, e);
            throw;
        }
    }

    /// <summary>
    /// The frame-source run on an asynchronous source: <see cref="Steps"/> as <see cref="Run"/> drives them, the next frame
    /// awaited before each step that takes one. No other thread: the run continues wherever the source's await does.
    /// </summary>
    static async Task<DetectionResult> RunAsync(YuvVideo video, IAsyncEnumerable<YuvFrame> frames, DetectionOptions? options, CancellationToken cancellationToken)
    {
        using var activity = Tracing.StartActivity("ShotDetection.Detect");
        try
        {
            var feed = new FrameFeed();
            DetectionResult result;
            var source = frames.GetAsyncEnumerator(cancellationToken);
            await using (source.ConfigureAwait(false))
            {
                using var steps = Steps(null, null, options, null, cancellationToken, o => new VideoReader(video, feed, o)).GetEnumerator();
                while (!Stepped(steps, out result))
                {
                    if (feed.NeedsFrame)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // before each frame is awaited
                        if (await source.MoveNextAsync().ConfigureAwait(false))
                            feed.Put(source.Current);
                        else
                            feed.End();
                    }
                }
            }

            Traced(activity, result);
            return result;
        }
        catch (Exception e)
        {
            Failed(activity, e);
            throw;
        }
    }

    /// <summary>One step of the run: true with the result once it has one.</summary>
    static bool Stepped<T>(IEnumerator<T?> steps, out T result)
        where T : class
    {
        if (!steps.MoveNext())
            throw new InvalidOperationException("The run's steps ended without a result.");
        result = steps.Current!;
        return steps.Current is not null;
    }

    /// <summary>The run's span: what it read and found.</summary>
    static void Traced(System.Diagnostics.Activity? activity, DetectionResult result)
    {
        Console.Error.WriteLine($"DEBUG pipeline={result.Video.Reader.Pipeline} chunks={result.Video.Chunks} {result.Video.Reader.Width}x{result.Video.Reader.Height}");
        if (activity is not null)
        {
            var v = result.Video.Reader;
            activity.SetTag("shotdetector.video.width", v.SourceWidth);
            activity.SetTag("shotdetector.video.height", v.SourceHeight);
            activity.SetTag("shotdetector.video.fps", v.Fps.Value);
            activity.SetTag("shotdetector.pipeline", v.Pipeline.ToString());
            activity.SetTag("shotdetector.decoder", v.DecodesInProcess ? "inprocess" : "process");
            activity.SetTag("shotdetector.streaming", v.Streaming);
            activity.SetTag("shotdetector.chunks", result.Video.Chunks);
            activity.SetTag("shotdetector.frames", result.FrameCount);
            activity.SetTag("shotdetector.shots", result.Shots.Count);
        }
    }

    /// <summary>The run's span: why it failed.</summary>
    static void Failed(System.Diagnostics.Activity? activity, Exception e)
    {
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, e.Message);
        activity?.AddException(e);
        if (e is ShotDetectionException d)
            activity?.SetTag("shotdetector.error", d.Reason.ToString());
    }

    /// <summary>
    /// The run, as steps: null where its next action takes a frame from the caller (the driver puts one in the
    /// <see cref="FrameFeed"/> first, awaited in <see cref="RunAsync"/>), then the result. A file's or a stream's run goes
    /// through to the end without stopping (<see cref="Run"/>). With <paramref name="prescored"/> (<see cref="Merge"/>), its
    /// scores take the place of the frames': the decisions run over them as over a chunked file's.
    /// </summary>
    static IEnumerable<DetectionResult?> Steps(string? videoPath,
        Stream? videoStream,
        DetectionOptions? options,
        Action<Shot>? onShot,
        CancellationToken cancellationToken,
        Func<DetectionOptions, VideoReader>? frames,
        Prescored? prescored = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var o = (options ?? new DetectionOptions()).Resolved();

        // DetectStreamAsync's channel (it decides the mode's Auto), and DetectionOptions.OnShot, which only listens.
        bool streaming = onShot is not null;
        if (o.OnShot is { } told)
        {
            var yielded = onShot;
            onShot = shot =>
            {
                told.Report(shot);
                yielded?.Invoke(shot);
            };
        }

        CheckOptions(o, videoPath);

        // A streamed mp4 without "faststart" (headers last) can't be read from a pipe: it goes to a temporary file first,
        // read as a file and deleted at the end. Other streams stream.
        byte[]? prefix = videoStream is null ? null : VideoReader.ReadPrefix(videoStream, o.ProbeBytes, cancellationToken);
        using var spilled = prefix is not null && VideoReader.HeadersLast(prefix) ? VideoReader.Spill(prefix, videoStream!, cancellationToken) : null;
        var video = frames is not null ? frames(o)
            : spilled is not null ? new VideoReader(spilled.Path, o, cancellationToken)
            : videoStream is not null ? new VideoReader(videoStream, prefix!, o, cancellationToken)
            : new VideoReader(videoPath!, o, cancellationToken);

        // Warnings for the result, each also told to DetectionOptions.Warnings as soon as it is known (streaming callers
        // get no result).
        var warnings = new List<string>();
        void Warn(string? warning)
        {
            if (warning is null)
                return;
            warnings.Add(warning);
            o.Warnings?.Report(warning);
        }

        // The executable's HLS warning; checked again after the frames, in case Auto handed decoding to the executable.
        bool hlsWarned = false;
        void WarnHls()
        {
            if (!hlsWarned && VideoReader.ExecutableHlsWarning(videoPath, video.Container, video.DecodesInProcess) is { } hls)
            {
                hlsWarned = true;
                Warn(hls);
            }
        }

        WarnHls();
        foreach (string warning in prescored?.Warnings ?? [])
            Warn(warning);
        IReadOnlyList<FrameReader.ToneSegment>? tone = null;
        if (o.TrimReferenceTone)
        {
            if (video.Streaming)
                throw new ArgumentException("TrimReferenceTone needs a file path (the audio is read separately).");
            tone = FindReferenceTone(videoPath!, o, cancellationToken);
            o = TrimmedToTone(o, tone, video.Duration);
        }

        if (o.StartTime is not null && video.Streaming)
            throw new ArgumentException("StartTime isn't available for a streamed input (a Stream, a URL, or Streaming = true).");
        int minSceneLen = MinSceneLengthInFrames(o.MinSceneLength, video.Fps);

        var settings = Settings(o);
        var stats = o.CollectStats ? new Stats() : null;
        var detectors = settings.Select(st => Build(st, o, video, minSceneLen, stats)).ToList();

        // Shots stream out as cuts arrive only when nothing can reorder or change them later.
        bool incremental = detectors.Count == 1 && !o.DropShortScenes && !o.MergeLastScene;

        // Cuts as scenedetect keeps them (its cutting list, later sorted and de-duplicated).
        var cuts = new List<FrameTime>();
        var shots = new List<Shot>();

        // Frames from the caller: those the start reads (frame 0, and up to the start time), one per step, so an asynchronous
        // source is awaited between them as between the others (DetectAsync).
        double? startSeconds = o.StartTime is null ? null : TimecodeSeconds(o.StartTime, video.Fps, oneBasedFrames: true);
        while (video.TakesBeforeStart(startSeconds))
        {
            yield return null;
            video.TakeBeforeStart();
        }

        // MPEG-PS decoded in-process (TimestampsArrive): the first frame's time comes with it, so the first shot's start
        // is taken then (below); asked for now, it would take the decoding pass this reading spares.
        var start = video.TimestampsArrive ? default : video.Position(0);
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
                {
                    // Timestamps that start over (two streams joined): scenedetect labels frames by time, so its cuts
                    // come out of order too, and it sorts them at the end. Shots from here on come from the sorted cuts.
                    incremental = false;
                    return;
                }
            }

            lastCut = cut;
            var shot = new Shot(shots.Count + 1, start, cut);
            shots.Add(shot);
            onShot?.Invoke(shot);
            start = cut;
        }

        // scenedetect's `time -s` seeks (1-based frame numbers); `-e`/`-d` become whole frames at the
        // average rate (FrameTimecode rounds half to even) and are compared as frame numbers.
        int startFrame = startSeconds is { } seconds ? video.SeekFrame(seconds) : 0;
        static long Frames(string value, Fps fps) => (long)Math.Round(TimecodeSeconds(value, fps) * fps.Value);
        long? endFrame = o.EndTime is not null ? Frames(o.EndTime, video.Fps)
            : o.RangeDuration is not null ? Frames(o.RangeDuration, video.Fps) + startFrame
            : null;
        bool startArrives = video.TimestampsArrive;
        start = startArrives ? default : video.Position(startFrame);
        var first = start;
        int frameCount = 0, stride = o.FrameSkip + 1;
        bool readToEnd = true;
        long lastReport = 0;
        int lastStep = 0; // with ProgressStep: the last multiple of it reported
        DetectionProgress Progress(int done, int expected) =>
            new() { FramesProcessed = done, ExpectedFrames = expected, DecodesInProcess = video.DecodesInProcess };

        // A first report before any frame: which decoder and pipeline this run uses.
        o.Progress?.Report(Progress(0, Math.Max(0, video.ExpectedFrames - startFrame)));

        void Advance(int done)
        {
            int expected = Math.Max(0, video.ExpectedFrames - startFrame);
            if (o.ProgressStep is { } progressStep)
            {
                // The steps crossed below 100 % (the last report is that); none when the count is unknown.
                int reached = expected > 0 ? (int)Math.Floor(Math.Min(1.0, done / (double)expected) / progressStep + 1e-9) : 0;
                if (reached > lastStep && reached * progressStep < 1 - 1e-9)
                {
                    o.Progress!.Report(Progress(done, expected));
                    lastStep = reached;
                }
            }
            else if (Environment.TickCount64 - lastReport >= 100)
            {
                o.Progress!.Report(Progress(done, expected));
                lastReport = Environment.TickCount64;
            }
        }

        // Parallel: the scores in chunks at once, then the decisions over them in frame order (docs/parallel-detection.md).
        // Why not, when it can't: reported (Warnings) when the caller asked for Parallel.
        bool parallel = o.ResolvedMode(streaming) == DetectionMode.Parallel;
        long pixels = (long)video.SourceWidth * video.SourceHeight;
        string? notChunked = !parallel ? null
            : video.FromFrames ? "frames from the caller"
            : NotChunkable(videoPath is null || spilled is not null || video.Streaming, video.PtsFromFrames, startFrame != 0 || endFrame is not null, stride != 1);
        int chunks = parallel && notChunked is null
            ? o.Chunks ?? AutoChunks(Environment.ProcessorCount, video.ExpectedFrames, video.Fps, pixels, o.Mode == DetectionMode.Parallel, o.DecodeThreads)
            : 1;
        if (parallel && notChunked is null && chunks < 2)
            notChunked = o.Chunks is not null ? "one chunk asked for" : WhyOneChunk(Environment.ProcessorCount, video.ExpectedFrames, video.Fps, pixels, o.Mode == DetectionMode.Parallel, o.DecodeThreads);
        if (o.Mode == DetectionMode.Parallel && notChunked is not null)
            Warn("read sequentially, not in chunks: " + notChunked);
        string? fallback = null;
        var gate = new object();
        int scoredFrames = 0;
        var chunked = prescored is not null ? prescored.Scores
            : chunks > 1
            ? ScoreChunked(videoPath!,
                o,
                video,
                settings,
                minSceneLen,
                stats is not null,
                chunks,
                o.Progress is null ? null : () => { lock (gate) Advance(++scoredFrames); },
                out fallback,
                cancellationToken)
            : null;
        if (fallback is not null)
            Warn("read sequentially, from the start, as scoring in chunks fell back: " + fallback); // no notChunked then
        if (chunked is { } scored)
        {
            for (int k = 0; k < scored.Frames; k++)
            {
                for (int d = 0; d < detectors.Count; d++)
                {
                    if (detectors[d].Decide(k, scored.Metrics[d][k]) is { } cut)
                        Cut(cut);
                }
            }

            foreach (var chunkStats in scored.Stats)
                stats?.Merge(chunkStats.Stats!, chunkStats.From, chunkStats.To);
            frameCount = scored.Frames;
        }
        else
        {
            chunks = 1;
            lastStep = 0; // a fallback reads from the start: its progress starts over
        }

        foreach (var frame in chunked is null ? video.Frames(startFrame, null, cancellationToken) : [])
        {
            int index = startFrame + frameCount;
            if (frameCount == 0 && startArrives)
                start = first = video.Position(index);
            if (frameCount % stride == 0)
            {
                foreach (var detector in detectors)
                {
                    if (detector.ProcessFrame(index, frame) is { } cut)
                        Cut(cut);
                }
            }

            frameCount++;
            if (o.Progress is not null)
                Advance(frameCount);

            // scenedetect analyses a frame, skips the next FrameSkip frames, then stops if the last
            // frame read plus one frame reaches the end (an end hit while skipping counts as EOF).
            if (endFrame is { } e && frameCount % stride == 0 && video.Position(index).PlusFrames(1).FrameNum >= e)
            {
                readToEnd = false;
                break;
            }

            yield return null; // the next frame is taken next
        }

        WarnHls();
        Warn(video.SitingChangeWarning);
        o.Progress?.Report(Progress(frameCount, frameCount));
        if (frameCount > 0)
        {
            // Where scenedetect finds the stream after the loop: past the end if it read until EOF,
            // otherwise at the last frame read (which may be a skipped one).
            var last = readToEnd ? video.PositionAfterDecoding(startFrame + frameCount) : video.Position(startFrame + frameCount - 1);
            foreach (var detector in detectors)
            {
                if (detector.PostProcess(last) is { } trailingCut)
                    Cut(trailingCut);
            }

            if (incremental)
            {
                var final = new Shot(shots.Count + 1, start, last.PlusFrames(1));
                shots.Add(final);
                onShot?.Invoke(final);
            }
            else
            {
                // Shots already streamed out before the cuts came out of order aren't repeated.
                var streamedUntil = shots.Count > 0 ? shots[^1].End : (FrameTime?)null;
                shots = Scenes(Unique(cuts), first, last.PlusFrames(1), o.MergeLastScene, o.DropShortScenes ? minSceneLen : 0, minSceneLen);
                foreach (var shot in shots)
                {
                    if (streamedUntil is not { } until || CompareCuts(shot.Start, until) >= 0)
                        onShot?.Invoke(shot);
                }
            }
        }

        yield return new()
        {
            VideoPath = videoPath,
            Video = new DetectedVideo(video) { Chunks = chunks, ChunkFallback = fallback },
            Shots = shots,
            FrameCount = frameCount,
            MinSceneLengthFrames = minSceneLen,
            Stats = stats,
            Cuts = Unique(cuts),
            ReferenceTone = tone,
            Warnings = warnings,
        };
    }

    /// <summary>The checks of the resolved options every run makes before reading anything (an <see cref="ArgumentException"/>).</summary>
    static void CheckOptions(DetectionOptions o, string? videoPath)
    {
        if (o.TrimReferenceTone && videoPath is null)
            throw new ArgumentException("TrimReferenceTone needs a file path (the audio is read separately).");
        if (o.TrimReferenceTone && (o.StartTime is not null || o.EndTime is not null || o.RangeDuration is not null))
            throw new ArgumentException("TrimReferenceTone sets the time range itself; leave StartTime, EndTime and RangeDuration unset.");
        if (o.FrameSkip < 0)
            throw new ArgumentException("FrameSkip must be at least 0.");
        if (o.ProgressStep is { } step && !(step > 0 && step <= 1))
            throw new ArgumentException("ProgressStep must be above 0 and at most 1.");
        if (o.FrameSkip > 0 && o.CollectStats)
            throw new ArgumentException("FrameSkip must be 0 when collecting stats (as in scenedetect).");
        if (o.EndTime is not null && o.RangeDuration is not null)
            throw new ArgumentException("EndTime and Duration cannot both be set.");
        if (o.Downscale is < 1)
            throw new ArgumentException("Downscale must be at least 1.");
    }

    /// <summary>
    /// The time range without a leading tone segment (starting in the first 5 s, nearer the start than the end) and a
    /// trailing one (ending in the last 5 s).
    /// </summary>
    internal static DetectionOptions TrimmedToTone(DetectionOptions o, IReadOnlyList<FrameReader.ToneSegment> tone, TimeSpan? duration)
    {
        var ends = TimeSpan.FromSeconds(5);
        static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        int first = 0;
        if (tone.Count > 0 && tone[0].Start <= ends && (duration is not { } length || tone[0].Start <= length - tone[0].End))
        {
            o = o with { StartTime = Seconds(tone[0].End) };
            first = 1;
        }

        if (tone.Count > first && duration is { } d && tone[^1].End >= d - ends)
            o = o with { EndTime = Seconds(tone[^1].Start) };
        return o;
    }

    /// <summary>The largest frame <see cref="DetectionMode.Auto"/> chunks: 3840 x 2160 pixels (4K UHD).</summary>
    internal const long AutoChunksMaxPixels = 3840L * 2160;

    /// <summary>
    /// The chunk count when chunking: half the CPUs (so none below 4), at most 4, and none shorter than a minute; never
    /// more than the decoder threads the caller set (<paramref name="threads"/>, 0 = FFmpeg's choice: no limit).
    /// <see cref="DetectionMode.Auto"/> only for frames up to <see cref="AutoChunksMaxPixels"/>, as memory grows per chunk
    /// with the frame size; <see cref="DetectionMode.Parallel"/> (<paramref name="forced"/>) at any size. 2 chunks on 4
    /// CPUs gained on every film and clip measured, 1080p included (docs/decisions/2026-10-18-parallel-auto-4cpus.md).
    /// </summary>
    internal static int AutoChunks(int cpus, int frames, Fps fps, long pixels = 0, bool forced = false, int? threads = null)
    {
        if (!forced && pixels > AutoChunksMaxPixels)
            return 1;
        int chunks = Math.Max(1, Math.Min(Math.Min(4, cpus / 2), (int)(frames / (60 * fps.Value))));
        return threads > 0 ? Math.Min(chunks, threads.Value) : chunks;
    }

    /// <summary>Why <see cref="AutoChunks"/> gave one chunk, for the same arguments; null when it gave more.</summary>
    internal static string? WhyOneChunk(int cpus, int frames, Fps fps, long pixels = 0, bool forced = false, int? threads = null) =>
        AutoChunks(cpus, frames, fps, pixels, forced, threads) > 1 ? null
        : cpus < 4 ? $"fewer than 4 CPUs ({cpus})"
        : !forced && pixels > AutoChunksMaxPixels ? "frames larger than 3840 x 2160"
        : threads == 1 ? "one decoder thread"
        : "shorter than two minutes";

    /// <summary>Why a file can't be scored in chunks at all, whatever the CPUs; null when it can.</summary>
    internal static string? NotChunkable(bool streamed, bool ptsFromFrames, bool timeRange, bool frameSkip) =>
        streamed ? "a stream, a URL or streaming mode"
        : ptsFromFrames ? "packets without timestamps (MPEG-PS)"
        : timeRange ? "a time range"
        : frameSkip ? "frame skip"
        : null;

    /// <summary>
    /// The decoder threads a chunked run shares out when the caller set none: a sequential run's
    /// (<see cref="VideoReader.DefaultDecodeThreads"/>), or, for a decoder that decodes several frames at once
    /// (<paramref name="frameThreads"/>: h264, HEVC, ...) and frames up to <see cref="MoreThreadsMaxPixels"/>, twice the
    /// CPUs, at most 16. Such a decoder waits on the frames it has in flight, one per thread, and 2 chunks of 2 threads
    /// on 4 CPUs left the cores idle: 2 of 4 decoded 150 s of 1080p h264 1.11x (8-bit) and 1.22x (10-bit) as fast in the
    /// same CPU time, for 30 MB more (2026-10-21). Each chunk then has at most the CPUs' count, OpenCV's for its one
    /// decoder. Slice-threaded decoders (MPEG-2) gain nothing; at 3840 x 2160 it was 1.09x for 125 MB more (each thread
    /// holds 4K frames), so larger frames keep the sequential count.
    /// </summary>
    internal static int ChunkedDecodeThreads(int cpus, bool frameThreads, long pixels) =>
        frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(2 * cpus, 1, 16) : VideoReader.DefaultDecodeThreads(cpus);

    /// <summary>The largest frame <see cref="ChunkedDecodeThreads"/> gives a frame-threaded decoder twice the CPUs for: 1920 x 1080.</summary>
    internal const long MoreThreadsMaxPixels = 1920L * 1080;

    /// <summary>
    /// A chunk's reader's options: its share of the decoder threads and frame buffers, and its frames converted on its
    /// decoding thread unless <see cref="DetectionOptions.ConvertThreads"/> is set (the chunks keep the cores busy
    /// already: a conversion thread gained 1.00-1.07x there, 2026-10-22, docs/decisions/2026-10-22-decode-split.md).
    /// </summary>
    internal static DetectionOptions ChunkOptions(DetectionOptions o, int threads, int chunks, int chunk) =>
        o with { DecodeThreads = ChunkThreads(threads, chunks, chunk), FrameBuffers = ChunkBuffers(chunks), ConvertThreads = o.ConvertThreads ?? 0 };

    /// <summary>
    /// A chunk's share of <paramref name="threads"/> decoder threads, the remainder going to the first chunks so none
    /// is lost (5 in 2 chunks: 3 and 2); at least 1 each.
    /// </summary>
    internal static int ChunkThreads(int threads, int chunks, int chunk) =>
        Math.Max(1, threads / chunks + (chunk < threads % chunks ? 1 : 0));

    /// <summary>
    /// A chunk's frame buffers: a sequential run's 8 shared out (4 each in 2 chunks), at least 2 (one decoding, one
    /// scored). Measured 2026-10-18 on 4 CPUs, 240p to 2160p: no slower than 8 each, and up to 285 MB less at 2160p
    /// with FastYuv in 4 chunks (docs/parallel-detection.md).
    /// </summary>
    internal static int ChunkBuffers(int chunks) => Math.Max(2, 8 / chunks);

    /// <summary>
    /// Each detector's per-frame metrics, scored in chunks of the file at once: each chunk on its own reader, from one
    /// frame before its first (metrics compare with the previous frame) to one past its last, whose score must equal the
    /// next chunk's for it. Every frame's position must be the probe's for its index (a seek that lands late, a dropped
    /// frame), each chunk's first frame must have the first chunk's size (scenedetect's buffer keeps the stream's first
    /// size), and nothing may fail: otherwise null, and the caller reads the video sequentially (which reports any real
    /// error itself). Stats are per chunk, for its frames.
    /// </summary>
    static (List<double?>[] Metrics, int Frames, (Stats? Stats, int From, int To)[] Stats)? ScoreChunked(string path,
        DetectionOptions o,
        VideoReader video,
        IReadOnlyList<DetectorSettings> settings,
        int minSceneLen,
        bool collectStats,
        int chunks,
        Action? onFrame,
        out string? fallback,
        CancellationToken cancellationToken)
    {
        using var activity = Tracing.StartActivity("ShotDetection.ScoreChunked");
        int total = video.ExpectedFrames;

        // The decoder threads shared out (FFmpeg's choice, 0, would give each chunk about one per CPU: the default count
        // is shared out instead): a sequential run's, or twice that for a frame-threaded decoder (ChunkedDecodeThreads).
        int threads = o.DecodeThreads is > 0 and var set
            ? set
            : ChunkedDecodeThreads(Environment.ProcessorCount, video.HasFrameThreads, (long)video.SourceWidth * video.SourceHeight);
        var metrics = new List<double?>[chunks, settings.Count];
        var overlap = new double?[chunks, settings.Count];
        var sizes = new (int, int)?[chunks];
        var chunkStats = new Stats?[chunks];
        var bounds = Enumerable.Range(0, chunks + 1).Select(i => (int)((long)total * i / chunks)).ToArray();
        string? problem = null;
        void Fail(string why) => Interlocked.CompareExchange(ref problem, why, null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = Enumerable.Range(0, chunks).Select(i => Task.Factory.StartNew(() =>
        {
            int a = bounds[i], b = bounds[i + 1], from = Math.Max(0, a - 1), k = from;
            bool last = i == chunks - 1;
            var co = ChunkOptions(o, threads, chunks, i);
            try
            {
                var reader = video.Probed is { } probed ? new VideoReader(path, co, probed, stop.Token) : new VideoReader(path, co, stop.Token);
                chunkStats[i] = collectStats ? new Stats() : null;
                var ds = settings.Select(st => Build(st, co, reader, minSceneLen, chunkStats[i])).ToList();
                for (int d = 0; d < ds.Count; d++)
                    metrics[i, d] = [];
                foreach (var frame in reader.Frames(from, last ? null : b + 1 - from, stop.Token))
                {
                    sizes[i] ??= reader.FirstFrameSize;
                    for (int d = 0; d < ds.Count; d++)
                    {
                        var m = ds[d].Metric(k, frame);
                        if (k == b && !last)
                            overlap[i, d] = m;
                        else if (k >= a)
                            metrics[i, d].Add(m);
                    }

                    if (k >= a && reader.Position(k).Key != video.Position(k).Key)
                        Fail($"chunk {i}: frame {k} at {reader.Position(k).Seconds} s, expected {video.Position(k).Seconds} s");
                    if (problem is not null)
                    {
                        stop.Cancel();
                        return;
                    }

                    if (k >= a && (k < b || last))
                        onFrame?.Invoke();
                    k++;
                }

                if (!last && k != b + 1)
                    Fail($"chunk {i}: {k - a} frames, expected {b + 1 - a}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            } // another chunk's problem stopped this one
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Fail($"chunk {i}: {e.Message}");
                stop.Cancel();
            }
        },
            stop.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        for (int i = 0; i + 1 < chunks && problem is null; i++)
        {
            if (sizes[i + 1] != sizes[0])
                Fail($"chunk {i + 1}: frames of {sizes[i + 1]}, the first chunk's {sizes[0]}");
            for (int d = 0; d < settings.Count; d++)
            {
                if (!Equals(overlap[i, d], metrics[i + 1, d].FirstOrDefault()))
                    Fail($"chunk {i}: overlap score {overlap[i, d]} != {metrics[i + 1, d].FirstOrDefault()} at frame {bounds[i + 1]}");
            }
        }

        fallback = problem;
        if (problem is not null)
        {
            activity?.SetTag("shotdetector.chunks.fallback", problem);
            return null;
        }

        var all = Enumerable.Range(0, settings.Count)
            .Select(d => Enumerable.Range(0, chunks).SelectMany(i => metrics[i, d]).ToList())
            .ToArray();
        return (all, all[0].Count, [.. Enumerable.Range(0, chunks).Select(i => (chunkStats[i], bounds[i], i == chunks - 1 ? int.MaxValue : bounds[i + 1]))]);
    }

    /// <summary>One detector's settings per entry of <see cref="DetectionOptions.Detectors"/> (the single-detector options are the shorthand); each gets the same frames.</summary>
    static IReadOnlyList<DetectorSettings> Settings(DetectionOptions o) =>
        o.Detectors is not { Count: > 0 } ? [new DetectorSettings(o.Detector) { Threshold = o.Threshold }] : o.Detectors;

    static IDetector Build(DetectorSettings st, DetectionOptions o, VideoReader video, int minSceneLen, Stats? stats)
    {
        bool v072 = o.Compatibility >= PySceneDetectVersion.V0_7_2;
        int frameWindow = st.FrameWindow ?? o.FrameWindow,
            bins = st.Bins ?? (o.Bins > 0 ? o.Bins : v072 ? 128 : 256),
            hashSize = st.HashSize ?? (o.HashSize > 0 ? o.HashSize : v072 ? 8 : 16),
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

        int minOut = MinOutLengthFrames(st.MinOutLength ?? o.MinOutLength, v072, video.Fps);
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
            DetectorKind.Content => new ContentDetector(Scorer(),
                video.Position,
                video.Fps,
                st.Threshold ?? 27.0,
                min,
                (st.FilterMode ?? o.FilterMode) == FlashFilterMode.Suppress,
                frameCounts: v072),
            DetectorKind.Threshold => new ThresholdDetector(video.Position,
                video.Fps,
                st.Threshold ?? 12.0,
                min,
                st.FadeBias ?? o.FadeBias,
                st.AddLastScene ?? o.AddLastScene,
                minOut),
            DetectorKind.Histogram => new HistogramDetector(video.Position, st.Threshold ?? (v072 ? 0.20 : 0.05), bins, min),
            DetectorKind.Hash => new HashDetector(video.Position, st.Threshold ?? (v072 ? 0.35 : 0.395), hashSize, hashLowpass, min),
            _ => new AdaptiveDetector(Scorer(),
                video.Position,
                st.Threshold ?? 3.0,
                min,
                frameWindow,
                st.MinContentVal ?? o.MinContentVal,
                fromTarget: v072),
        };
        detector.Stats = stats;
        if (detector is HashDetector hash)
            hash.SetFrameSize(video.Width, video.Height);
        return detector;
    }

    /// <summary>FrameTimecode equality: exact times when both are timestamps at the same rate, else frame numbers.</summary>
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
        {
            if (!unique.Any(u => SameCut(u, cut)))
                unique.Add(cut);
        }

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
    /// <summary>detect-threshold's --min-out-length in frames: the CLI's 0.1 s from 0.7.2 when not given, none before.</summary>
    internal static int MinOutLengthFrames(string? value, bool v072, Fps fps) =>
        (value ?? (v072 ? "0.1s" : null)) is { } v ? MinSceneLengthInFrames(v, fps) : 0;

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
            if (oneBasedFrames && frames >= 1)
                frames--;
            return frames / fps.Value;
        }

        if (value.EndsWith('s'))
            return double.Parse(value[..^1], CultureInfo.InvariantCulture);
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 3)
                throw new FormatException($"Timecode '{value}' must be HH:MM:SS[.mmm].");
            return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
                + double.Parse(parts[2], CultureInfo.InvariantCulture);
        }

        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}
