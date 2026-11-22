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
            : $"-map 0:v:0 -map 0:a? -map 0:s? -c:v libx264 -preset {Preset ?? (HighQuality ? "slow" : "veryfast")} "
              + $"-crf {(RateFactor ?? (HighQuality ? 17 : 22)).ToString(CultureInfo.InvariantCulture)} -c:a aac";
        return args.Split(' ');
    }
}

/// <summary>Settings for <see cref="Export.SaveImages"/>; the defaults are scenedetect's.</summary>
public sealed record ImageOptions
{
    /// <summary>Images per shot; not used with <see cref="Interval"/>.</summary>
    public int NumImages { get; init; } = 3;

    /// <summary>
    /// One image per this much of each shot instead of <see cref="NumImages"/> (scenedetect 0.7.3's save-images
    /// <c>-i/--interval</c>): a shot gets floor(length / interval) + 1 images, at least <see cref="MinImages"/> and at most
    /// <see cref="MaxImages"/>, picked as with <see cref="NumImages"/>. Seconds ("2s", "1.5"), "HH:MM:SS[.mmm]" or frames
    /// ("48"). Null (default): <see cref="NumImages"/> per shot.
    /// </summary>
    public string? Interval { get; init; }

    /// <summary><see cref="Interval"/> as a time; not both.</summary>
    public TimeSpan? IntervalDuration { get; init; }

    /// <summary>The fewest images a shot gets with <see cref="Interval"/> (1 or more); only with it.</summary>
    public int MinImages { get; init; } = 1;

    /// <summary>The most images a shot gets with <see cref="Interval"/>, at least <see cref="MinImages"/>; null: no limit. Only with it.</summary>
    public int? MaxImages { get; init; }

    /// <summary>Each shot's number of images: port of scenedetect's _get_num_images, with save_images' checks of the options.</summary>
    internal int[] ImageCounts(IReadOnlyList<Shot> shots, Fps fps)
    {
        if (Interval is not null && IntervalDuration is not null)
            throw new ArgumentException("Interval and IntervalDuration are the same option; set one.");
        double? interval = IntervalDuration is { } t ? t.TotalSeconds
            : Interval is { } s ? ShotDetection.TimecodeSeconds(s, fps)
            : null;
        if (interval is not { } seconds)
        {
            if (MinImages != 1 || MaxImages is not null)
                throw new ArgumentException("MinImages and MaxImages can only be used with Interval.");
            if (NumImages < 1)
                throw new ArgumentException("NumImages must be at least 1.");
            return [.. Enumerable.Repeat(NumImages, shots.Count)];
        }

        if (!(seconds > 0))
            throw new ArgumentException("Interval must be greater than 0.");
        if (MinImages < 1)
            throw new ArgumentException("MinImages must be at least 1.");
        if (MaxImages < MinImages)
            throw new ArgumentException("MaxImages must be at least MinImages.");
        return [.. shots.Select(shot =>
        {
            // scenedetect adds 1e-9 intervals before rounding down, so a shot a whole number of intervals long keeps
            // its last image (0.3 / 0.1 is 2.9999999999999996).
            int count = Math.Max((int)Math.Floor((Math.Max(shot.Duration.Seconds, 0.0) / seconds) + IntervalCountTolerance) + 1, MinImages);
            return MaxImages is { } max ? Math.Min(count, max) : count;
        })];
    }

    /// <summary>scenedetect's _INTERVAL_COUNT_TOLERANCE.</summary>
    internal const double IntervalCountTolerance = 1e-9;

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
    /// JPEG or WebP quality, 0-100 (save-images --quality); null: scenedetect's defaults, 95 for JPEG and 100 for WebP.
    /// WebP's is libwebp's scale, as OpenCV's; JPEG's maps onto ffmpeg's -q:v 31-2, so files are similar to OpenCV's, not identical.
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>PNG compression, zlib's level 0-9 as OpenCV's (save-images --compression); null: scenedetect's 3.</summary>
    public int? Compression { get; init; }

    /// <summary>
    /// File name template, without extension (save-images -f): $VIDEO_NAME, $SCENE_NUMBER, $IMAGE_NUMBER,
    /// $FRAME_NUMBER, $TIMESTAMP_MS, $TIMECODE (with ';' for ':'); may contain folders.
    /// </summary>
    public string FileName { get; init; } = "$VIDEO_NAME-Scene-$SCENE_NUMBER-$IMAGE_NUMBER";

    /// <summary>
    /// ffmpeg's encoder options for the format, quality and compression. WebP names its encoder: ffmpeg's default for .webp,
    /// libwebp_anim where it is built, holds every frame for one animation, so a run writing several images wrote one file.
    /// </summary>
    internal string[] EncoderOptions()
    {
        var o = Encoder();
        return o.Format switch
        {
            FrameReader.ImageFormat.Png => ["-compression_level", o.PngCompressionLevel!.Value.ToString(CultureInfo.InvariantCulture)],
            FrameReader.ImageFormat.WebP => ["-c:v", "libwebp", "-quality", o.WebPQuality!.Value.ToString(CultureInfo.InvariantCulture)],
            _ => ["-q:v", o.JpegQscale!.Value.ToString(CultureInfo.InvariantCulture)],
        };
    }

    /// <summary>The format, quality and compression as FrameReader's encoder takes them: the same as <see cref="EncoderOptions"/>.</summary>
    internal FrameReader.ImageEncoderOptions Encoder()
    {
        if (Quality is < 0 or > 100)
            throw new ArgumentException("Quality is 0 to 100.", nameof(Quality));
        if (Compression is < 0 or > 9)
            throw new ArgumentException("Compression is 0 to 9.", nameof(Compression));
        return Format switch
        {
            "png" => new() { Format = FrameReader.ImageFormat.Png, PngCompressionLevel = Compression ?? 3 },
            "webp" => new() { Format = FrameReader.ImageFormat.WebP, WebPQuality = Quality ?? 100 },

            // OpenCV's JPEG quality has no ffmpeg equivalent: 95 (scenedetect's default) and above is -q:v 2, ffmpeg's
            // best usual setting, and lower qualities spread evenly down to -q:v 31 at 0.
            _ => new() { Format = FrameReader.ImageFormat.Jpg, JpegQscale = Math.Clamp((int)Math.Round(2 + ((95 - (Quality ?? 95)) * 29 / 95.0)), 2, 31) },
        };
    }

    /// <summary>A pixel format the images are converted to last (the contact sheet's PNGs: rgb24); null: the encoder's choice.</summary>
    internal string? PixelFormat { get; init; }

    /// <summary>Each image's filters after picking its frame: square pixels (at ffmpeg's default flags), then the size.</summary>
    internal string Filter() => "scale='round(iw*sar)':ih,setsar=1" + ScaleFilter() + (PixelFormat is { } f ? ",format=" + f : "");

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
/// defaults and file names. Both run ffmpeg; save-images runs FFmpeg's libraries in this process when the detection decoded
/// there.
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
    /// save-images: NumImages images per shot, or one per <see cref="ImageOptions.Interval"/> of each shot (see
    /// <see cref="ImageOptions"/>), named "{video}-Scene-{NNN}-{II}.jpg", in shot order. Frames are picked like
    /// scenedetect's _generate_timecode_list:
    /// the shot is split into equal segments; the first image is FrameMargin
    /// frames in, the last that far from the end, the others mid-segment. Images are full size,
    /// stretched to square pixels if needed.
    /// Same frames as scenedetect (by its frame positions), but encoded by ffmpeg at
    /// -q:v 2 rather than OpenCV at JPEG quality 95, so files are similar, not byte-identical
    /// (<see cref="ImageOptions.Quality"/>, <see cref="ImageOptions.Compression"/>).
    /// When the detection decoded in this process (<see cref="DetectedVideo.DecodedInProcess"/>) and the libraries have the
    /// format's encoder, the images are encoded there too, through FrameReader: the files the ffmpeg executable of the same
    /// FFmpeg build writes, byte for byte (a JPEG's comment names libavcodec's version, a WebP carries libwebp's encoding);
    /// otherwise by the ffmpeg executable.
    /// </summary>
    public static IReadOnlyList<string> SaveImages(DetectionResult result,
        string outputDir,
        ImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.VideoPath is null)
            throw new InvalidOperationException("Saving images needs the video as a file path or URL; the input was a Stream.");
        var io = options ?? new ImageOptions();
        string[] quality = io.EncoderOptions();
        string filter = io.Filter();
        int frameMargin = io.FrameMargin;
        if (frameMargin < 0)
            throw new ArgumentException("FrameMargin must be at least 0.");
        if (io.Format is not ("jpg" or "png" or "webp"))
            throw new ArgumentException("Format must be jpg, png or webp.");
        var (videoPath, video, shots, frameCount) = (result.VideoPath!, result.Video.Reader, result.Shots, result.FrameCount);
        if (shots.Count == 0)
            return [];
        Directory.CreateDirectory(outputDir);
        string name = Timeline.FileNameOf(videoPath, video.Compatibility);
        int[] counts = io.ImageCounts(shots, video.Fps);

        // Image numbers' digits: from the shot with the most images, as scenedetect's max_scene_images.
        string imageFormat = new('0', (int)Math.Floor(Math.Log10(counts.Max())) + 2);

        // (frame index, file name) for every image; a frame may be wanted twice.
        var wanted = new List<(int Frame, string File)>();

        // scenedetect picks times and seeks to them.
        double[][] times = ImageTimes(shots, video.Fps, counts, frameMargin);
        int[][] picked = times.Select(t => t.Select(video.FrameAt).ToArray()).ToArray();
        for (int i = 0; i < shots.Count; i++)
        {
            for (int j = 0; j < counts[i]; j++)
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
        }

        int[] frames = wanted.Select(w => w.Frame).Distinct().Order().ToArray();
        if (video.DecodesInProcess && !IsUrl(videoPath) && CanEncodeInProcess(io, filter))
        {
            SaveImagesInProcess(videoPath, frames, wanted, io.Encoder(), filter, cancellationToken);
            return wanted.Select(w => w.File).ToList();
        }

        // One ffmpeg pass: select the wanted frames (in order) into numbered temp files, then rename.
        string temp = Path.Combine(outputDir, $".shotdetector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string filterFile = Path.Combine(temp, "filter.txt");
            File.WriteAllText(filterFile,
                $"select='{SelectFrames(frames)}',{filter}");
            VideoReader.Run(video.FfmpegExe,
                ["-v", "error", "-nostdin", "-y", .. VideoReader.InputOptions(videoPath), .. VideoReader.MaxPixels,
                    .. VideoReader.IgnoreContainerCropping(video.FfmpegExe, cancellationToken), "-i", videoPath, "-map", "0:v:0", "-/vf", filterFile,
                    "-fps_mode", "passthrough", .. quality, Path.Combine(temp, $"%06d.{io.Format}")],
                cancellationToken,
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

    /// <summary>SaveImages' files cut into shots: <paramref name="counts"/>[i] for shot i, in order.</summary>
    internal static List<IReadOnlyList<string>> PerShot(IReadOnlyList<string> files, int[] counts)
    {
        var groups = new List<IReadOnlyList<string>>(counts.Length);
        int at = 0;
        foreach (int count in counts)
        {
            groups.Add([.. files.Skip(at).Take(count)]);
            at += count;
        }

        return groups;
    }

    /// <summary>
    /// ffmpeg's select expression for these frames: their eq(n,F) summed as a balanced tree. A chain of a+b+c... stops
    /// parsing at about 100 terms ("Error while parsing expression"), which 34 shots at 3 images each reached.
    /// </summary>
    internal static string SelectFrames(ReadOnlySpan<int> frames) => frames.Length == 1
        ? $"eq(n\\,{frames[0].ToString(CultureInfo.InvariantCulture)})"
        : $"({SelectFrames(frames[..(frames.Length / 2)])}+{SelectFrames(frames[(frames.Length / 2)..])})";

    static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && uri.Scheme != Uri.UriSchemeFile;

    /// <summary>Whether the loaded libraries have the format's encoder and the filters (never inlined: browser WebAssembly has no FFmpeg).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool CanEncodeInProcess(ImageOptions io, string filter) =>
        FrameReader.FFmpegLibraries.Missing(io.Format switch
        {
            "png" => FrameReader.Capability.Png,
            "webp" => FrameReader.Capability.WebP,
            _ => FrameReader.Capability.Jpeg,
        }).Count == 0
        && FrameReader.FFmpegLibraries.MissingFilters(filter).Count == 0;

    /// <summary>
    /// save-images in this process: the ffmpeg executable's one pass (<c>-i X -vf select=...,F</c> to numbered files) as
    /// FrameReader runs it, frames decoded in order (select's n) and each wanted one through the same filters into the same
    /// encoder, which the executable's pass also keeps for all of its frames.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void SaveImagesInProcess(string videoPath,
        int[] frames,
        List<(int Frame, string File)> wanted,
        FrameReader.ImageEncoderOptions encoderOptions,
        string filter,
        CancellationToken cancellationToken)
    {
        var files = wanted.ToLookup(w => w.Frame, w => w.File);
        var vf = new FrameReader.ImageFilterOptions { Filter = filter };
        try
        {
            using var decoder = new FrameReader.FrameDecoder(videoPath, new() { CancellationToken = cancellationToken });
            using var encoder = new FrameReader.ImageEncoder(encoderOptions);
            int n = 0;
            foreach (int frame in frames)
            {
                bool more = true;
                while (more && n <= frame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    more = decoder.Next();
                    n++;
                }

                if (!more)
                    throw new ShotDetectionException(ShotDetectionError.ExportFailed, $"The video ended before frame {frame} (save-images).");
                using var decoded = decoder.TakeFrame();
                byte[] image = encoder.Encode(decoded, vf);
                foreach (string file in files[frame])
                    File.WriteAllBytes(file, image);
            }
        }
        catch (FrameReader.FrameReaderException e)
        {
            throw new ShotDetectionException(ShotDetectionError.ExportFailed, e.Message, e);
        }
    }

    /// <summary>Seconds of each shot's images: port of _generate_timecode_list.</summary>
    internal static double[][] ImageTimes(IReadOnlyList<Shot> shots, Fps fps, int numImages, int frameMargin) =>
        ImageTimes(shots, fps, [.. Enumerable.Repeat(numImages, shots.Count)], frameMargin);

    /// <summary>Seconds of each shot's images, <paramref name="counts"/>[i] for shot i: port of _generate_timecode_list.</summary>
    internal static double[][] ImageTimes(IReadOnlyList<Shot> shots, Fps fps, int[] counts, int frameMargin)
    {
        double marginSecs = FrameTime.Frame(frameMargin, fps).Seconds;
        return shots.Select((shot, i) =>
        {
            int numImages = counts[i];
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
        var (videoPath, video) = (result.VideoPath, result.Video.Reader);
        var clips = result.Shots.Select(s => (s.Start, s.End)).ToList();
        if (so.Expand && clips.Count > 0)
        {
            // expand_scenes_to_bounds: from the base timecode (frame 0) to base + OpenCV's frame count.
            clips[0] = (FrameTime.Frame(0, video.Fps), clips[0].End);
            clips[^1] = (clips[^1].Start, FrameTime.Frame(video.OpenCvFrameCount, video.Fps));
        }

        Directory.CreateDirectory(outputDir);
        string name = Timeline.FileNameOf(videoPath, video.Compatibility);

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
            VideoReader.Run(video.FfmpegExe,
                ["-v", "error", "-nostdin", "-y",
                    "-ss", Stats.PyFloat(start.Seconds), .. VideoReader.InputOptions(videoPath), .. VideoReader.MaxPixels, "-i", videoPath, "-t", Stats.PyFloat(end.Minus(start).Seconds),
                    .. codecArgs, "-sn", file],
                cancellationToken,
                failure: ShotDetectionError.ExportFailed);
            files.Add(file);
        }

        return files;
    }
}
