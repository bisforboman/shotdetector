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
      -w, --frame-window <n>             adaptive: frames on each side to average (default 2)
      -f, --fade-bias <-1..1>            threshold: cut position between fade-out (-1) and fade-in (+1)
      -l, --luma-only                    Only use the V (brightness) channel
          --ffmpeg-resize                Downscale with ffmpeg bilinear instead of an exact port of
                                         cv2.resize (faster, but cuts can differ from PySceneDetect)
          --csv <file>                   Write shot list as CSV
          --json <file>                  Write shot list as JSON
    """;

string? input = null, csvPath = null, jsonPath = null, minSceneLenArg = "0.6s";
string detectorName = "adaptive";
double? threshold = null;
double minContentVal = 15.0, fadeBias = 0;
int window = 2;
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
            case "-w" or "--frame-window": window = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "-f" or "--fade-bias": fadeBias = NextDouble(); break;
            case "-l" or "--luma-only": lumaOnly = true; break;
            case "--ffmpeg-resize": ffmpegResize = true; break;
            case "--csv": csvPath = Next(); break;
            case "--json": jsonPath = Next(); break;
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
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
    return 2;
}

VideoReader video;
try { video = new VideoReader(input, ffmpegResize); }
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
// "0.6s" → frames using Python's round-half-to-even, like FrameTimecode._seconds_to_frames.
int minSceneLen = minSceneLenArg.EndsWith('s')
    ? (int)Math.Round(double.Parse(minSceneLenArg[..^1], CultureInfo.InvariantCulture) * video.Fps.Value)
    : int.Parse(minSceneLenArg, CultureInfo.InvariantCulture);

var scorer = lumaOnly ? ContentScorer.LumaOnly() : new ContentScorer();
IDetector detector = detectorName switch
{
    "content" => new ContentDetector(scorer, video.Fps, threshold ?? 27.0, minSceneLen),
    "threshold" => new ThresholdDetector(threshold ?? 12.0, minSceneLen, fadeBias),
    _ => new AdaptiveDetector(scorer, threshold ?? 3.0, minSceneLen, window, minContentVal),
};

Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{Path.GetFileName(input)}: {video.SourceWidth}x{video.SourceHeight} @ {video.Fps.Value:0.###} fps, " +
    $"~{video.FrameCountHint} frames, processing at {video.Width}x{video.Height}, " +
    $"detector={detectorName}, min-scene-len={minSceneLen} frames"));

var cuts = new List<int>();
int frameCount = 0;
try
{
    foreach (var frame in video.Frames())
    {
        if (detector.ProcessFrame(frameCount, frame) is int cut)
            cuts.Add(cut);
        frameCount++;
    }
    if (frameCount > 0 && detector.PostProcess(frameCount - 1) is int last)
        cuts.Add(last);
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

var shots = Shots.FromCuts(cuts, frameCount);
Console.WriteLine(Shots.Table(shots, video.Fps));
if (csvPath is not null)
    File.WriteAllText(csvPath, Shots.Csv(shots, video.Fps));
if (jsonPath is not null)
    File.WriteAllText(jsonPath, Shots.Json(shots, video.Fps));
return 0;
