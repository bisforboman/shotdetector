using System.Globalization;

namespace ShotDetector;

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
public static class Export
{
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
    public static List<string> SaveImages(DetectionResult result, string outputDir, ImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var io = options ?? new ImageOptions();
        int numImages = io.NumImages, frameMargin = io.FrameMargin;
        if (numImages < 1 || frameMargin < 0)
            throw new ArgumentException("NumImages must be at least 1 and FrameMargin at least 0.");
        if (io.Format is not ("jpg" or "png" or "webp"))
            throw new ArgumentException("Format must be jpg, png or webp.");
        var (videoPath, video, shots, frameCount) = (result.VideoPath, result.Video, result.Shots, result.FrameCount);
        if (shots.Count == 0)
            return [];
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        string imageFormat = new('0', (int)Math.Floor(Math.Log10(numImages)) + 2);

        // (frame index, file name) for every image; a frame may be wanted twice.
        var wanted = new List<(int Frame, string File)>();
        // 0.7.1 picks times and seeks to them; 0.6.4 picks frame numbers directly.
        int[][] picked = video.Compatibility == PySceneDetectVersion.V0_6_4
            ? ImageFrames064(shots, numImages, frameMargin)
            : ImageTimes(shots, video.Fps, numImages, frameMargin).Select(t => t.Select(video.FrameAt).ToArray()).ToArray();
        for (int i = 0; i < shots.Count; i++)
            for (int j = 0; j < numImages; j++)
                wanted.Add((Math.Clamp(picked[i][j], 0, frameCount - 1), Path.Combine(outputDir,
                    $"{name}-Scene-{SceneNumber(shots[i].Number, shots.Count)}-{(j + 1).ToString(imageFormat, CultureInfo.InvariantCulture)}.{io.Format}")));

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
                "-fps_mode", "passthrough", .. quality, Path.Combine(temp, $"%06d.{io.Format}")], cancellationToken);
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
    /// Frame numbers of each shot's images as scenedetect 0.6.4's save_images picks them: the shot's
    /// frames (padded with its last frame to at least <paramref name="numImages"/>) are split into
    /// equal parts with np.array_split; the first image is <paramref name="frameMargin"/> frames into
    /// the first part, the last that far from the end of the last part, the others mid-part.
    /// Differs: on variable frame rate video OpenCV's frame seek can land a frame early; this doesn't.
    /// </summary>
    internal static int[][] ImageFrames064(IReadOnlyList<Shot> shots, int numImages, int frameMargin) => shots.Select(shot =>
    {
        int start = (int)shot.Start.FrameNum, count = Math.Max(1, (int)(shot.End.FrameNum - shot.Start.FrameNum));
        var frames = Enumerable.Range(start, count).ToList();
        while (frames.Count < numImages)
            frames.Add(frames[^1]);
        // np.array_split: the first (count % n) parts get one extra element.
        int size = frames.Count / numImages, extra = frames.Count % numImages, offset = 0;
        var picked = new int[numImages];
        for (int j = 0; j < numImages; j++)
        {
            var part = frames.GetRange(offset, size + (j < extra ? 1 : 0));
            offset += part.Count;
            picked[j] = (0 < j && j < numImages - 1) || numImages == 1 ? part[part.Count / 2]
                : j == 0 ? Math.Min(part[0] + frameMargin, part[^1])
                : Math.Max(part[^1] - frameMargin, part[0]);
        }
        return picked;
    }).ToArray();

    /// <summary>
    /// split-video: one file per shot, "{video}-Scene-{NNN}.mp4", cut and re-encoded by ffmpeg with
    /// scenedetect's default arguments (libx264 veryfast, CRF 22, AAC audio, subtitles kept; 0.6.4 adds
    /// -sn, which drops them).
    /// </summary>
    /// <remarks>Cancelling stops between clips and kills the running ffmpeg; finished clips are kept.</remarks>
    public static List<string> SplitVideo(DetectionResult result, string outputDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (videoPath, shots) = (result.VideoPath, result.Shots);
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        var files = new List<string>();
        foreach (var shot in shots)
        {
            string file = Path.Combine(outputDir, $"{name}-Scene-{SceneNumber(shot.Number, shots.Count)}.mp4");
            VideoReader.Run(result.Video.FfmpegExe, ["-v", "error", "-nostdin", "-y",
                "-ss", Stats.PyFloat(shot.Start.Seconds), "-i", videoPath, "-t", Stats.PyFloat(shot.Duration.Seconds),
                "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                "-c:a", "aac", .. (result.Video.Compatibility == PySceneDetectVersion.V0_6_4 ? ["-sn"] : Array.Empty<string>()),
                file], cancellationToken);
            files.Add(file);
        }
        return files;
    }
}
