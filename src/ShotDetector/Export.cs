using System.Globalization;

namespace ShotDetector;

/// <summary>Settings for <see cref="Export.SplitVideo"/>, like scenedetect's split-video options; the defaults are its.</summary>
public sealed record SplitOptions
{
    /// <summary>Copy the streams instead of re-encoding (<c>--copy</c>): fast, but cuts land on keyframes.</summary>
    public bool Copy { get; init; }

    /// <summary>CRF 17 and preset "slow" instead of 22 and "veryfast" (<c>--high-quality</c>).</summary>
    public bool HighQuality { get; init; }

    /// <summary>x264 constant rate factor (<c>--rate-factor</c>); default 22, or 17 with <see cref="HighQuality"/>.</summary>
    public int? RateFactor { get; init; }

    /// <summary>x264 preset (<c>--preset</c>); default "veryfast", or "slow" with <see cref="HighQuality"/>.</summary>
    public string? Preset { get; init; }

    /// <summary>ffmpeg arguments replacing the encoding ones, split on spaces (<c>--args</c>); <see cref="Copy"/> wins over these.</summary>
    public string? Args { get; init; }

    /// <summary>Stretch the first clip to the start of the video and the last to its end (<c>--expand</c>), for a time range.</summary>
    public bool Expand { get; init; }

    /// <summary>
    /// File name template (split-video -f): $VIDEO_NAME, $SCENE_NUMBER, $START_TIME, $END_TIME (with ';' for ':'),
    /// $START_FRAME, $END_FRAME, $START_PTS, $END_PTS (milliseconds); ".mp4" is added unless it has an extension.
    /// </summary>
    public string FileName { get; init; } = "$VIDEO_NAME-Scene-$SCENE_NUMBER";

    internal string[] FfmpegArgs()
    {
        if (Copy && (HighQuality || RateFactor is not null || Preset is not null))
            throw new ArgumentException("High quality, rate factor and preset can't be used with Copy.");
        // As the scenedetect CLI builds them: copy, else the given args, else libx264 with crf and preset.
        string args = Copy ? "-map 0:v:0 -map 0:a? -map 0:s? -c:v copy -c:a copy"
            : Args is not null ? Args.Replace("\\\"", "\"")
            : $"-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset {Preset ?? (HighQuality ? "slow" : "veryfast")} " +
              $"-crf {(RateFactor ?? (HighQuality ? 17 : 22)).ToString(CultureInfo.InvariantCulture)} -c:a aac";
        return args.Split(' ');
    }
}

/// <summary>Settings for <see cref="Export.SaveImages"/>; the defaults are scenedetect's.</summary>
public sealed record ImageOptions
{
    /// <summary>Images per shot.</summary>
    public int NumImages { get; init; } = 3;

    /// <summary>Frames to keep away from each end of a shot.</summary>
    public int FrameMargin { get; init; } = 1;

    /// <summary>Output width in pixels; with only one of Width/Height set, the other keeps the aspect ratio.</summary>
    public int? Width { get; init; }

    /// <summary>Output height in pixels.</summary>
    public int? Height { get; init; }

    /// <summary>Scale factor (ignored when Width or Height is set).</summary>
    public double? Scale { get; init; }

    /// <summary>"jpg", "png" or "webp".</summary>
    public string Format { get; init; } = "jpg";

    /// <summary>
    /// File name template, without extension (save-images -f): $VIDEO_NAME, $SCENE_NUMBER, $IMAGE_NUMBER,
    /// $FRAME_NUMBER, $TIMESTAMP_MS, $TIMECODE (with ';' for ':'); may contain folders.
    /// </summary>
    public string FileName { get; init; } = "$VIDEO_NAME-Scene-$SCENE_NUMBER-$IMAGE_NUMBER";

    /// <summary>The ffmpeg scale filter for these settings (bilinear, as scenedetect's default scale-method).</summary>
    internal string ScaleFilter() => (Width, Height, Scale) switch
    {
        ({ } w, { } h, _) => $",scale={w}:{h}:flags=bilinear",
        ({ } w, null, _) => $",scale={w}:'trunc(ih*{w}/iw)':flags=bilinear",
        (null, { } h, _) => $",scale='trunc(iw*{h}/ih)':{h}:flags=bilinear",
        (null, null, { } s) => $",scale='round(iw*{s.ToString("R", CultureInfo.InvariantCulture)})':'round(ih*{s.ToString("R", CultureInfo.InvariantCulture)})':flags=bilinear",
        _ => "",
    };
}

/// <summary>
/// Uses a shot list like scenedetect's save-images and split-video commands, with the same
/// defaults and file names. Both run ffmpeg.
/// </summary>
public static partial class Export
{
    /// <summary>
    /// Python's string.Template(template).safe_substitute(values): $NAME or ${NAME} (letters, digits, '_'),
    /// "$$" for '$'; unknown names and stray '$' stay as they are.
    /// </summary>
    internal static string Substitute(string template, IReadOnlyDictionary<string, string> values) =>
        TemplateVariable().Replace(template, m =>
            m.Groups["escaped"].Success ? "$"
            : (m.Groups["named"].Success ? m.Groups["named"].Value : m.Groups["braced"].Value) is var key && values.TryGetValue(key, out var v) ? v
            : m.Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"\$(?:(?<escaped>\$)|(?<named>[_a-zA-Z][_a-zA-Z0-9]*)|\{(?<braced>[_a-zA-Z][_a-zA-Z0-9]*)\})")]
    private static partial System.Text.RegularExpressions.Regex TemplateVariable();

    /// <summary>scenedetect pads scene numbers to at least 3 digits.</summary>
    static string SceneNumber(int number, int count) =>
        number.ToString(new string('0', Math.Max(3, (int)Math.Floor(Math.Log10(count)) + 1)), CultureInfo.InvariantCulture);

    /// <summary>
    /// save-images: NumImages images per shot (see <see cref="ImageOptions"/>), named
    /// "{video}-Scene-{NNN}-{II}.jpg". Frames are picked like scenedetect's _generate_timecode_list:
    /// the shot is split into equal segments; the first image is FrameMargin
    /// frames in, the last that far from the end, the others mid-segment. Images are full size,
    /// stretched to square pixels if needed.
    /// Same frames as scenedetect (see <see cref="VideoReader.FrameAt"/>), but encoded by ffmpeg at
    /// -q:v 2 rather than OpenCV at JPEG quality 95, so files are similar, not byte-identical.
    /// </summary>
    public static IReadOnlyList<string> SaveImages(DetectionResult result, string outputDir, ImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.VideoPath is null)
            throw new InvalidOperationException("Saving images needs the video as a file path or URL; the input was a Stream.");
        var io = options ?? new ImageOptions();
        int numImages = io.NumImages, frameMargin = io.FrameMargin;
        if (numImages < 1 || frameMargin < 0)
            throw new ArgumentException("NumImages must be at least 1 and FrameMargin at least 0.");
        if (io.Format is not ("jpg" or "png" or "webp"))
            throw new ArgumentException("Format must be jpg, png or webp.");
        var (videoPath, video, shots, frameCount) = (result.VideoPath!, result.Video, result.Shots, result.FrameCount);
        if (shots.Count == 0)
            return [];
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        string imageFormat = new('0', (int)Math.Floor(Math.Log10(numImages)) + 2);

        // (frame index, file name) for every image; a frame may be wanted twice.
        var wanted = new List<(int Frame, string File)>();
        // scenedetect picks times and seeks to them.
        double[][] times = ImageTimes(shots, video.Fps, numImages, frameMargin);
        int[][] picked = times.Select(t => t.Select(video.FrameAt).ToArray()).ToArray();
        for (int i = 0; i < shots.Count; i++)
            for (int j = 0; j < numImages; j++)
            {
                double seconds = times[i][j]; // the image's own time: the picked one
                string file = Substitute(io.FileName, new Dictionary<string, string>
                {
                    ["VIDEO_NAME"] = name,
                    ["SCENE_NUMBER"] = SceneNumber(shots[i].Number, shots.Count),
                    ["IMAGE_NUMBER"] = (j + 1).ToString(imageFormat, CultureInfo.InvariantCulture),
                    ["FRAME_NUMBER"] = picked[i][j].ToString(CultureInfo.InvariantCulture),
                    ["TIMESTAMP_MS"] = ((long)(seconds * 1000)).ToString(CultureInfo.InvariantCulture),
                    ["TIMECODE"] = FrameTime.FormatTimecode(seconds).Replace(':', ';'),
                }) + "." + io.Format;
                string path = Path.Combine(outputDir, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                wanted.Add((Math.Clamp(picked[i][j], 0, frameCount - 1), path));
            }

        // One ffmpeg pass: select the wanted frames (in order) into numbered temp files, then rename.
        int[] frames = wanted.Select(w => w.Frame).Distinct().Order().ToArray();
        string temp = Path.Combine(outputDir, $".shotdetector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string filterFile = Path.Combine(temp, "filter.txt");
            File.WriteAllText(filterFile,
                $"select='{string.Join("+", frames.Select(f => $"eq(n\\,{f})"))}',scale='round(iw*sar)':ih,setsar=1{io.ScaleFilter()}");
            string[] quality = io.Format switch
            {
                "png" => ["-compression_level", "3"],   // scenedetect's defaults: jpeg 95, png 3, webp 100
                "webp" => ["-quality", "100"],
                _ => ["-q:v", "2"],
            };
            VideoReader.Run(video.FfmpegExe, ["-v", "error", "-nostdin", "-y", "-i", videoPath, "-map", "0:v:0", "-/vf", filterFile,
                "-fps_mode", "passthrough", .. quality, Path.Combine(temp, $"%06d.{io.Format}")], cancellationToken,
                failure: ShotDetectionError.ExportFailed);
            var byFrame = frames.Select((f, i) => (f, Path.Combine(temp, $"{i + 1:000000}.{io.Format}"))).ToDictionary();
            foreach (var (frame, file) in wanted)
                File.Copy(byFrame[frame], file, overwrite: true);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
        return wanted.Select(w => w.File).ToList();
    }

    /// <summary>Seconds of each shot's images: port of _generate_timecode_list.</summary>
    internal static double[][] ImageTimes(IReadOnlyList<Shot> shots, Fps fps, int numImages, int frameMargin)
    {
        double marginSecs = FrameTime.Frame(frameMargin, fps).Seconds;
        return shots.Select(shot =>
        {
            double start = shot.Start.Seconds, duration = shot.Duration.Seconds;
            if (duration <= 0)
                return Enumerable.Repeat(start, numImages).ToArray();
            double segment = duration / numImages;
            return Enumerable.Range(0, numImages).Select(j =>
            {
                double segStart = start + j * segment, segEnd = start + (j + 1) * segment;
                return numImages == 1 ? start + duration / 2.0
                    : j == 0 ? Math.Min(segStart + marginSecs, segEnd)
                    : j == numImages - 1 ? Math.Max(segEnd - marginSecs, segStart)
                    : (segStart + segEnd) / 2.0;
            }).ToArray();
        }).ToArray();
    }


    /// <summary>
    /// split-video: one file per shot, "{video}-Scene-{NNN}.mp4", cut by ffmpeg with scenedetect's
    /// arguments (by default libx264 veryfast, CRF 22 and AAC audio; subtitles dropped).
    /// </summary>
    /// <remarks>Cancelling stops between clips and kills the running ffmpeg; finished clips are kept.</remarks>
    public static IReadOnlyList<string> SplitVideo(DetectionResult result, string outputDir, SplitOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.VideoPath is null)
            throw new InvalidOperationException("Splitting needs the video as a file path or URL; the input was a Stream.");
        var so = options ?? new SplitOptions();
        string[] codecArgs = so.FfmpegArgs();
        var (videoPath, video) = (result.VideoPath, result.Video);
        var clips = result.Shots.Select(s => (s.Start, s.End)).ToList();
        if (so.Expand && clips.Count > 0)
        {
            // expand_scenes_to_bounds: from the base timecode (frame 0) to base + OpenCV's frame count.
            clips[0] = (FrameTime.Frame(0, video.Fps), clips[0].End);
            clips[^1] = (clips[^1].Start, FrameTime.Frame(video.OpenCvFrameCount, video.Fps));
        }
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        // As the CLI: ".mp4" unless the template ends in a 2-4 character extension.
        int dot = so.FileName.LastIndexOf('.');
        string template = so.FileName + (dot >= 0 && so.FileName.Length - dot - 1 is >= 2 and <= 4 ? "" : ".mp4");
        var files = new List<string>();
        for (int i = 0; i < clips.Count; i++)
        {
            var (start, end) = clips[i];
            string file = Path.Combine(outputDir, Substitute(template, new Dictionary<string, string>
            {
                ["VIDEO_NAME"] = name,
                ["SCENE_NUMBER"] = SceneNumber(i + 1, clips.Count),
                ["START_TIME"] = start.Timecode().Replace(':', ';'),
                ["END_TIME"] = end.Timecode().Replace(':', ';'),
                ["START_FRAME"] = start.FrameNum.ToString(CultureInfo.InvariantCulture),
                ["END_FRAME"] = end.FrameNum.ToString(CultureInfo.InvariantCulture),
                ["START_PTS"] = ((long)Math.Round(start.Seconds * 1000)).ToString(CultureInfo.InvariantCulture),
                ["END_PTS"] = ((long)Math.Round(end.Seconds * 1000)).ToString(CultureInfo.InvariantCulture),
            }));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            VideoReader.Run(video.FfmpegExe, ["-v", "error", "-nostdin", "-y",
                "-ss", Stats.PyFloat(start.Seconds), "-i", videoPath, "-t", Stats.PyFloat(end.Minus(start).Seconds),
                .. codecArgs, "-sn", file], cancellationToken, failure: ShotDetectionError.ExportFailed);
            files.Add(file);
        }
        return files;
    }
}
