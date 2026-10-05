using System.Globalization;
using ShotDetector;

const string Usage = """
    Usage: ShotDetector -i <video> [options]

      -d, --detector <name>              adaptive, content or threshold (default: adaptive)
      -t, --threshold <n>                content: content_val threshold (default 27)
                                         adaptive: adaptive ratio threshold (default 3)
                                         threshold: mean pixel level of a fade (default 12)
      -m, --min-scene-len <n|Ns>         Minimum shot length, frames or seconds e.g. 0.6s (default 0.6s)
      -c, --min-content-val <n>          adaptive: minimum content_val for a cut (default 15)
          --frame-window <n>             adaptive: frames on each side to average (default 2)
      -w, --weights <h> <s> <l> <e>      content/adaptive: weights of hue, saturation, luma and edge
                                         differences in content_val (default 1 1 1 0)
      -k, --kernel-size <n>              content/adaptive: odd size >= 3 to widen edges by
                                         (default: from the resolution)
      -l, --luma-only                    Only use the V (brightness) channel (overrides --weights)
      -f, --fade-bias <-1..1>            threshold: cut position between fade-out (-1) and fade-in (+1)
          --threads <n>                  ffmpeg decoder threads (default 0 = automatic); fewer use less memory
          --ffmpeg-resize                Downscale with ffmpeg bilinear instead of an exact port of
                                         cv2.resize (faster, but cuts can differ from PySceneDetect)
          --csv <file>                   Write shot list as CSV
          --json <file>                  Write shot list as JSON
          --stats <file>                 Write per-frame metrics as CSV (like scenedetect -s)
    """;

string? input = null, csvPath = null, jsonPath = null, statsPath = null, minSceneLenArg = "0.6s";
string detectorName = "adaptive";
double? threshold = null;
double minContentVal = 15.0, fadeBias = 0;
int window = 2;
int? kernelSize = null;
int decodeThreads = 0;
double[] weights = [1, 1, 1, 0];
bool lumaOnly = false, ffmpegResize = false;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        double NextDouble() => double.Parse(Next(), CultureInfo.InvariantCulture);
        switch (args[i])
        {
            case "-i" or "--input": input = Next(); break;
            case "-d" or "--detector": detectorName = Next(); break;
            case "-t" or "--threshold": threshold = NextDouble(); break;
            case "-m" or "--min-scene-len": minSceneLenArg = Next(); break;
            case "-c" or "--min-content-val": minContentVal = NextDouble(); break;
            case "--frame-window": window = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "-w" or "--weights":
                weights = [NextDouble(), NextDouble(), NextDouble(), NextDouble()];
                break;
            case "-k" or "--kernel-size": kernelSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "-f" or "--fade-bias": fadeBias = NextDouble(); break;
            case "-l" or "--luma-only": lumaOnly = true; break;
            case "--ffmpeg-resize": ffmpegResize = true; break;
            case "--threads": decodeThreads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--csv": csvPath = Next(); break;
            case "--json": jsonPath = Next(); break;
            case "--stats": statsPath = Next(); break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
    if (input is null)
        throw new ArgumentException("Missing -i <video>");
    if (detectorName is not ("adaptive" or "content" or "threshold"))
        throw new ArgumentException($"Unknown detector '{detectorName}'");
    if (window < 1)
        throw new ArgumentException("--frame-window must be at least 1");
    if (kernelSize is -1)
        kernelSize = null; // scenedetect's spelling of "automatic"
    if (kernelSize is { } k && (k < 3 || k % 2 == 0))
        throw new ArgumentException("--kernel-size must be an odd number >= 3");
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
    return 2;
}

VideoReader video;
try { video = new VideoReader(input, ffmpegResize, decodeThreads); }
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
// "0.6s" → frames using Python's round-half-to-even, like FrameTimecode._seconds_to_frames.
int minSceneLen = minSceneLenArg.EndsWith('s')
    ? (int)Math.Round(double.Parse(minSceneLenArg[..^1], CultureInfo.InvariantCulture) * video.Fps.Value)
    : int.Parse(minSceneLenArg, CultureInfo.InvariantCulture);

var scorer = lumaOnly ? ContentScorer.LumaOnly() : new ContentScorer(weights[0], weights[1], weights[2], weights[3]);
// Like PySceneDetect, edges are computed when they're weighted or a stats file is written.
if (detectorName != "threshold" && (scorer.UsesEdges || statsPath is not null))
    scorer.Edges = new EdgeDetector(video.Width, video.Height, kernelSize);
IDetector detector = detectorName switch
{
    "content" => new ContentDetector(scorer, video.Position, video.Fps, threshold ?? 27.0, minSceneLen),
    "threshold" => new ThresholdDetector(video.Position, video.Fps, threshold ?? 12.0, minSceneLen, fadeBias),
    _ => new AdaptiveDetector(scorer, video.Position, threshold ?? 3.0, minSceneLen, window, minContentVal),
};
if (statsPath is not null)
    detector.Stats = new Stats();

Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{Path.GetFileName(input)}: {video.SourceWidth}x{video.SourceHeight} @ {video.Fps.Value:0.###} fps, " +
    $"~{video.FrameCountHint} frames, processing at {video.Width}x{video.Height}, " +
    $"detector={detectorName}, min-scene-len={minSceneLen} frames"));

var cuts = new List<PyTime>();
int frameCount = 0;
try
{
    foreach (var frame in video.Frames())
    {
        if (detector.ProcessFrame(frameCount, frame) is { } cut)
            cuts.Add(cut);
        frameCount++;
    }
    if (frameCount > 0 && detector.PostProcess(video.PositionAfterDecoding(frameCount)) is { } last)
        cuts.Add(last);
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

var shots = frameCount == 0
    ? []
    : Shots.FromCuts(cuts, video.Position(0), video.PositionAfterDecoding(frameCount).PlusFrames(1));
Console.WriteLine(Shots.Table(shots));
if (csvPath is not null)
    File.WriteAllText(csvPath, Shots.Csv(shots));
if (jsonPath is not null)
    File.WriteAllText(jsonPath, Shots.Json(shots));
if (detector.Stats is not null)
    File.WriteAllText(statsPath!, detector.Stats.Csv(video.Position));
return 0;
