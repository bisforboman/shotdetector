using System.Globalization;
using ShotDetector;

const string Usage = """
    Usage: shotdetect -i <video> [options]

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
          --threads <n>                  ffmpeg decoder threads (default 4; 0 = ffmpeg's choice). Each
                                         costs ~25 MB at 1080p; more rarely helps since we decode in parallel
          --ffmpeg-resize                Downscale with ffmpeg bilinear instead of an exact port of
                                         cv2.resize (faster, but cuts can differ from PySceneDetect)
          --csv <file>                   Write shot list as CSV
          --json <file>                  Write shot list as JSON
          --stats <file>                 Write per-frame metrics as CSV (like scenedetect -s)
          --save-images <dir>            Save JPEG thumbnails per shot (like scenedetect save-images)
          --num-images <n>               Thumbnails per shot (default 3)
          --split-video <dir>            Cut the video into one mp4 per shot (like scenedetect split-video)
    """;

string? input = null, csvPath = null, jsonPath = null, statsPath = null, imagesDir = null, splitDir = null;
var options = new DetectionOptions();
int numImages = 3;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        double NextDouble() => double.Parse(Next(), CultureInfo.InvariantCulture);
        int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
        switch (args[i])
        {
            case "-i" or "--input": input = Next(); break;
            case "-d" or "--detector":
                options = options with
                {
                    Detector = Next() switch
                    {
                        "adaptive" => DetectorKind.Adaptive,
                        "content" => DetectorKind.Content,
                        "threshold" => DetectorKind.Threshold,
                        var d => throw new ArgumentException($"Unknown detector '{d}'"),
                    },
                };
                break;
            case "-t" or "--threshold": options = options with { Threshold = NextDouble() }; break;
            case "-m" or "--min-scene-len": options = options with { MinSceneLength = Next() }; break;
            case "-c" or "--min-content-val": options = options with { MinContentVal = NextDouble() }; break;
            case "--frame-window": options = options with { FrameWindow = NextInt() }; break;
            case "-w" or "--weights": options = options with { Weights = (NextDouble(), NextDouble(), NextDouble(), NextDouble()) }; break;
            // -1 is scenedetect's spelling of "automatic".
            case "-k" or "--kernel-size": options = options with { KernelSize = NextInt() is var k and not -1 ? k : null }; break;
            case "-f" or "--fade-bias": options = options with { FadeBias = NextDouble() }; break;
            case "-l" or "--luma-only": options = options with { LumaOnly = true }; break;
            case "--ffmpeg-resize": options = options with { FfmpegResize = true }; break;
            case "--threads": options = options with { DecodeThreads = NextInt() }; break;
            case "--csv": csvPath = Next(); break;
            case "--json": jsonPath = Next(); break;
            case "--stats": statsPath = Next(); options = options with { CollectStats = true }; break;
            case "--save-images": imagesDir = Next(); break;
            case "--num-images": numImages = NextInt(); break;
            case "--split-video": splitDir = Next(); break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
    if (input is null)
        throw new ArgumentException("Missing -i <video>");
    if (numImages < 1)
        throw new ArgumentException("--num-images must be at least 1");
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
    return 2;
}

try
{
    var result = ShotDetection.Detect(input, options);
    var video = result.Video;
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{Path.GetFileName(input)}: {video.SourceWidth}x{video.SourceHeight} @ {video.Fps.Value:0.###} fps, " +
        $"{result.FrameCount} frames, processed at {video.Width}x{video.Height}, " +
        $"detector={options.Detector.ToString().ToLowerInvariant()}, min-scene-len={result.MinSceneLength} frames"));

    Console.WriteLine(Shots.Table(result.Shots));
    if (csvPath is not null)
        File.WriteAllText(csvPath, Shots.Csv(result.Shots));
    if (jsonPath is not null)
        File.WriteAllText(jsonPath, Shots.Json(result.Shots));
    if (statsPath is not null)
        File.WriteAllText(statsPath, result.Stats!.Csv(video.Position));
    if (imagesDir is not null)
        Console.Error.WriteLine($"Saved {Export.SaveImages(result, imagesDir, numImages).Count} images to {imagesDir}");
    if (splitDir is not null)
        Console.Error.WriteLine($"Wrote {Export.SplitVideo(result, splitDir).Count} clips to {splitDir}");
    return 0;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
    return 2;
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
