using System.Globalization;
using ShotDetector;

const string Usage = """
    Usage: shotdetect -i <video> [options]

      -i, --input <file|url|->           Video file, a URL (http, rtsp, ...), or - for standard input

      -d, --detector <name>              adaptive, content, threshold, hist or hash (default: adaptive)
      -t, --threshold <n>                content: content_val threshold (default 27)
                                         adaptive: adaptive ratio threshold (default 3)
                                         threshold: mean pixel level of a fade (default 12)
                                         hist: drop in histogram correlation (default 0.05)
                                         hash: fraction of hash bits that differ (default 0.395)
      -m, --min-scene-len <n|Ns>         Minimum shot length, frames or seconds e.g. 0.6s (default 0.6s)
      -c, --min-content-val <n>          adaptive: minimum content_val for a cut (default 15)
          --frame-window <n>             adaptive: frames on each side to average (default 2)
      -w, --weights <h> <s> <l> <e>      content/adaptive: weights of hue, saturation, luma and edge
                                         differences in content_val (default 1 1 1 0)
      -k, --kernel-size <n>              content/adaptive: odd size >= 3 to widen edges by
                                         (default: from the resolution)
      -l, --luma-only                    Only use the V (brightness) channel (overrides --weights)
      -b, --bins <n>                     hist: number of luma histogram bins (default 256)
          --hash-size <n>                hash: hash is n x n bits (default 16)
          --hash-lowpass <n>             hash: shrink frames to size*n pixels per side first (default 2)
      -f, --fade-bias <-1..1>            threshold: cut position between fade-out (-1) and fade-in (+1)
          --compat <0.7.1|0.6.4>         Which PySceneDetect release to reproduce (default 0.7.1)
          --frame-rate <fps>             Override the detected frame rate (scenedetect -f); also the
                                         rate of an image sequence such as frames/%04d.png (default 25)
          --ffmpeg-dir <dir>             Folder containing ffmpeg and ffprobe (default: found on PATH)
      -s, --start <time>                 Start here: HH:MM:SS[.mmm], seconds (12.5s) or 1-based frame (300)
      -e, --end <time>                   Stop here (exclusive); same formats, frames 0-based
          --duration <time>              Analyse this much from the start (not with --end)
          --frame-skip <n>               Analyse every (n+1)th frame (not with --stats)
          --crop <x0> <y0> <x1> <y1>     Only analyse this part of the frame (inclusive pixel corners)
          --threads <n>                  ffmpeg decoder threads (0 = ffmpeg's choice; default 4, or 8 on the
                                         yuv420p fast path, where decoding is the bottleneck). ~20 MB each at 1080p
          --ffmpeg-resize                Downscale with ffmpeg bilinear instead of an exact port of
                                         cv2.resize (faster, but cuts can differ from PySceneDetect)
          --csv <file>                   Write shot list as CSV
          --json <file>                  Write shot list as JSON
          --stats <file>                 Write per-frame metrics as CSV (like scenedetect -s)
          --save-images <dir>            Save JPEG thumbnails per shot (like scenedetect save-images)
          --num-images <n>               Thumbnails per shot (default 3)
          --frame-margin <n>             Frames to keep away from each end of a shot (default 1)
          --image-width <px>, --image-height <px>, --image-scale <factor>, --image-format <jpg|png|webp>
          --split-video <dir>            Cut the video into one mp4 per shot (like scenedetect split-video)
    """;

string? input = null, csvPath = null, jsonPath = null, statsPath = null, imagesDir = null, splitDir = null;
var options = new DetectionOptions();
var images = new ImageOptions();

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
                        "hist" => DetectorKind.Histogram,
                        "hash" => DetectorKind.Hash,
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
            case "-b" or "--bins": options = options with { Bins = NextInt() }; break;
            case "--hash-size": options = options with { HashSize = NextInt() }; break;
            case "--hash-lowpass": options = options with { HashLowpass = NextInt() }; break;
            case "-f" or "--fade-bias": options = options with { FadeBias = NextDouble() }; break;
            case "-l" or "--luma-only": options = options with { LumaOnly = true }; break;
            case "--ffmpeg-resize": options = options with { FfmpegResize = true }; break;
            case "--threads": options = options with { DecodeThreads = NextInt() }; break;
            case "--ffmpeg-dir": options = options with { FfmpegDirectory = Next() }; break;
            case "--frame-rate" or "--framerate": options = options with { FrameRate = NextDouble() }; break;
            case "--compat":
                options = options with
                {
                    Compatibility = Next() switch
                    {
                        "0.7.1" => PySceneDetectVersion.V0_7_1,
                        "0.6.4" => PySceneDetectVersion.V0_6_4,
                        var v => throw new ArgumentException($"Unknown --compat version '{v}' (0.7.1 or 0.6.4)"),
                    },
                };
                break;
#if FASTYUV
            case "--fast-yuv": options = options with { Yuv420Converter = new ShotDetector.FastYuv.SwscaleYuv420() }; break;
#endif
            case "--csv": csvPath = Next(); break;
            case "--json": jsonPath = Next(); break;
            case "--stats": statsPath = Next(); options = options with { CollectStats = true }; break;
            case "--save-images": imagesDir = Next(); break;
            case "--num-images": images = images with { NumImages = NextInt() }; break;
            case "--frame-margin": images = images with { FrameMargin = NextInt() }; break;
            case "--image-width": images = images with { Width = NextInt() }; break;
            case "--image-height": images = images with { Height = NextInt() }; break;
            case "--image-scale": images = images with { Scale = NextDouble() }; break;
            case "--image-format": images = images with { Format = Next() }; break;
            case "-s" or "--start": options = options with { StartTime = Next() }; break;
            case "-e" or "--end": options = options with { EndTime = Next() }; break;
            case "--duration": options = options with { Duration = Next() }; break;
            case "--frame-skip": options = options with { FrameSkip = NextInt() }; break;
            case "--crop": options = options with { Crop = (NextInt(), NextInt(), NextInt(), NextInt()) }; break;
            case "--split-video": splitDir = Next(); break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
    if (input is null)
        throw new ArgumentException("Missing -i <video>");
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
    return 2;
}

try
{
    // A live percentage on stderr, only when it's a terminal (not when output is redirected).
    if (!Console.IsErrorRedirected)
        options = options with { Progress = new SyncProgress<DetectionProgress>(p => Console.Error.Write(
            p.Fraction is { } f ? $"\r{f:P0} ({p.FramesProcessed} frames)  " : $"\r{p.FramesProcessed} frames  ")) };
    var result = input == "-"
        ? ShotDetection.Detect(Console.OpenStandardInput(), options)
        : ShotDetection.Detect(input, options);
    if (!Console.IsErrorRedirected)
        Console.Error.Write("\r" + new string(' ', 40) + "\r");
    var video = result.Video;
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{(input == "-" ? "stdin" : Path.GetFileName(input))}: {video.SourceWidth}x{video.SourceHeight} @ {video.Fps.Value:0.###} fps, " +
        $"{result.FrameCount} frames, processed at {video.Width}x{video.Height}, " +
        $"detector={options.Detector.ToString().ToLowerInvariant()}, min-scene-len={result.MinSceneLengthFrames} frames"));

    Console.WriteLine(Shots.Table(result.Shots));
    if (csvPath is not null)
        File.WriteAllText(csvPath, Shots.Csv(result.Shots));
    if (jsonPath is not null)
        File.WriteAllText(jsonPath, Shots.Json(result.Shots));
    if (statsPath is not null)
        File.WriteAllText(statsPath, result.Stats!.Csv(video.Position));
    if (imagesDir is not null)
        Console.Error.WriteLine($"Saved {Export.SaveImages(result, imagesDir, images).Count} images to {imagesDir}");
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

/// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool, out of order).</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
