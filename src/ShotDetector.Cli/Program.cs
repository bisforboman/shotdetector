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
      -m, --min-scene-len <time>         Minimum shot length: frames (15), seconds (0.6s, 0.6) or HH:MM:SS.mmm (default 0.6s)
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
          --skip-cuts                    Leave the "Timecode List:" row out of the CSV (list-scenes -s)
      -q, --quiet                        Don't print the shot list (list-scenes -q)
          --json <file>                  Write shot list as JSON
          --stats <file>                 Write per-frame metrics as CSV (like scenedetect -s)
          --save-images <dir>            Save JPEG thumbnails per shot (like scenedetect save-images)
          --num-images <n>               Thumbnails per shot (default 3)
          --frame-margin <n>             Frames to keep away from each end of a shot (default 1)
          --image-width <px>, --image-height <px>, --image-scale <factor>, --image-format <jpg|png|webp>
          --save-html <file>             Write the shot list as an HTML page with thumbnails (scenedetect
                                         save-html); images go to --save-images <dir>, or next to the page
          --html-no-images               Leave the thumbnails out of the HTML page
          --html-image-width <px>, --html-image-height <px>   Size attributes of the HTML thumbnails
          --save-edl <file>              CMX 3600 EDL (scenedetect save-edl); --edl-title <t>, --edl-reel <r>,
                                         --edl-start-timecode <HH:MM:SS:FF>
          --save-fcp <file>              Final Cut Pro XML (save-fcp); --fcp-format <fcpx|fcp7> (default fcpx)
          --save-otio <file>             OpenTimelineIO timeline (save-otio); --otio-name <n>, --otio-no-audio
          --save-qp <file>               x264 QP file with a keyframe at each cut (save-qp); --qp-disable-shift
          --split-video <dir>            Cut the video into one mp4 per shot (like scenedetect split-video)
          --split-copy                   Copy streams instead of re-encoding (fast; cuts on keyframes)
          --split-high-quality           CRF 17, preset slow (instead of 22, veryfast)
          --split-crf <n>, --split-preset <name>, --split-args "<ffmpeg args>"
          --split-expand                 Stretch the first/last clip to the video's start/end (with -s/-e)
    """;

bool skipCuts = false, quiet = false, htmlNoImages = false;
int? htmlWidth = null, htmlHeight = null;
string? htmlPath = null;
string? edlPath = null, edlTitle = null, edlReel = "AX", edlStart = null, fcpPath = null, fcpFormat = "fcpx";
string? otioPath = null, otioName = null, qpPath = null;
bool otioAudio = true, qpShift = true;
string? input = null, csvPath = null, jsonPath = null, statsPath = null, imagesDir = null, splitDir = null;
var options = new DetectionOptions();
var images = new ImageOptions();
var split = new SplitOptions();

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
            case "--skip-cuts": skipCuts = true; break;
            case "--save-html": htmlPath = Next(); break;
            case "--save-edl": edlPath = Next(); break;
            case "--edl-title": edlTitle = Next(); break;
            case "--edl-reel": edlReel = Next(); break;
            case "--edl-start-timecode": edlStart = Next(); break;
            case "--save-fcp": fcpPath = Next(); break;
            case "--fcp-format":
                fcpFormat = Next();
                if (fcpFormat is not ("fcpx" or "fcp7"))
                    throw new ArgumentException("--fcp-format must be fcpx or fcp7");
                break;
            case "--save-otio": otioPath = Next(); break;
            case "--otio-name": otioName = Next(); break;
            case "--otio-no-audio": otioAudio = false; break;
            case "--save-qp": qpPath = Next(); break;
            case "--qp-disable-shift": qpShift = false; break;
            case "--html-no-images": htmlNoImages = true; break;
            case "--html-image-width": htmlWidth = NextInt(); break;
            case "--html-image-height": htmlHeight = NextInt(); break;
            case "-q" or "--quiet": quiet = true; break;
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
            case "--split-copy": split = split with { Copy = true }; break;
            case "--split-high-quality": split = split with { HighQuality = true }; break;
            case "--split-crf": split = split with { RateFactor = NextInt() }; break;
            case "--split-preset": split = split with { Preset = Next() }; break;
            case "--split-args": split = split with { Args = Next() }; break;
            case "--split-expand": split = split with { Expand = true }; break;
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

    if (!quiet)
        Console.WriteLine(Shots.Table(result.Shots));
    if (csvPath is not null)
        File.WriteAllText(csvPath, Shots.Csv(result.Shots, includeCutList: !skipCuts));
    if (jsonPath is not null)
        File.WriteAllText(jsonPath, Shots.Json(result.Shots));
    if (statsPath is not null)
        File.WriteAllText(statsPath, result.Stats!.Csv(video.Position));
    // save-html includes thumbnails by default; like scenedetect, it saves them (next to the page) if
    // save-images wasn't asked for.
    if (htmlPath is not null && !htmlNoImages)
        imagesDir ??= Path.GetDirectoryName(Path.GetFullPath(htmlPath));
    IReadOnlyList<string>? saved = null;
    if (imagesDir is not null)
    {
        saved = Export.SaveImages(result, imagesDir, images);
        Console.Error.WriteLine($"Saved {saved.Count} images to {imagesDir}");
    }
    if (htmlPath is not null)
    {
        // Image links relative to the page, grouped per shot (SaveImages writes NumImages per shot, in order).
        string pageDir = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
        var perShot = htmlNoImages || saved is null ? null
            : saved.Select(f => Path.GetRelativePath(pageDir, f)).Chunk(images.NumImages).Select(c => (IReadOnlyList<string>)c).ToList();
        File.WriteAllText(htmlPath, Shots.Html(result.Shots, perShot, htmlWidth, htmlHeight));
        Console.Error.WriteLine($"Wrote {htmlPath}");
    }
    if (edlPath is not null)
        File.WriteAllText(edlPath, Timeline.Edl(result, edlTitle, edlReel!, edlStart));
    if (fcpPath is not null && result.Shots.Count > 0)
        File.WriteAllText(fcpPath, fcpFormat == "fcp7" ? Timeline.Fcp7(result) : Timeline.Fcpx(result));
    if (otioPath is not null)
        File.WriteAllText(otioPath, Timeline.Otio(result, otioName, otioAudio));
    if (qpPath is not null)
        File.WriteAllText(qpPath, Timeline.Qp(result, qpShift));
    if (splitDir is not null)
        Console.Error.WriteLine($"Wrote {Export.SplitVideo(result, splitDir, split).Count} clips to {splitDir}");
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
