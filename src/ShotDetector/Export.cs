using System.Diagnostics;
using System.Globalization;

namespace ShotDetector;

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
    /// save-images: <paramref name="numImages"/> JPEGs per shot, named
    /// "{video}-Scene-{NNN}-{II}.jpg". Frames are picked like scenedetect's _generate_timecode_list:
    /// the shot is split into equal segments; the first image is <paramref name="frameMargin"/>
    /// frames in, the last that far from the end, the others mid-segment. Images are full size,
    /// stretched to square pixels if needed.
    /// Same frames as scenedetect (see <see cref="VideoReader.FrameAt"/>), but encoded by ffmpeg at
    /// -q:v 2 rather than OpenCV at JPEG quality 95, so files are similar, not byte-identical.
    /// </summary>
    public static List<string> SaveImages(string videoPath, VideoReader video, IReadOnlyList<Shot> shots, int frameCount,
        string outputDir, int numImages = 3, int frameMargin = 1)
    {
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        string imageFormat = new('0', (int)Math.Floor(Math.Log10(numImages)) + 2);

        // (frame index, file name) for every image; a frame may be wanted twice.
        var wanted = new List<(int Frame, string File)>();
        var times = ImageTimes(shots, video.Fps, numImages, frameMargin);
        for (int i = 0; i < shots.Count; i++)
            for (int j = 0; j < numImages; j++)
                wanted.Add((Math.Clamp(video.FrameAt(times[i][j]), 0, frameCount - 1), Path.Combine(outputDir,
                    $"{name}-Scene-{SceneNumber(shots[i].Number, shots.Count)}-{(j + 1).ToString(imageFormat, CultureInfo.InvariantCulture)}.jpg")));

        // One ffmpeg pass: select the wanted frames (in order) into numbered temp files, then rename.
        int[] frames = wanted.Select(w => w.Frame).Distinct().Order().ToArray();
        string temp = Path.Combine(outputDir, $".shotdetector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string filterFile = Path.Combine(temp, "filter.txt");
            File.WriteAllText(filterFile,
                $"select='{string.Join("+", frames.Select(f => $"eq(n\\,{f})"))}',scale='round(iw*sar)':ih,setsar=1");
            RunFfmpeg(["-v", "error", "-nostdin", "-y", "-i", videoPath, "-map", "0:v:0", "-/vf", filterFile,
                "-fps_mode", "passthrough", "-q:v", "2", Path.Combine(temp, "%06d.jpg")]);
            var byFrame = frames.Select((f, i) => (f, Path.Combine(temp, $"{i + 1:000000}.jpg"))).ToDictionary();
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
    public static double[][] ImageTimes(IReadOnlyList<Shot> shots, Fps fps, int numImages, int frameMargin)
    {
        double marginSecs = PyTime.Frame(frameMargin, fps).Seconds;
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
    /// split-video: one file per shot, "{video}-Scene-{NNN}.mp4", cut and re-encoded by ffmpeg with
    /// scenedetect's default arguments (libx264 veryfast, CRF 22, AAC audio, subtitles kept).
    /// </summary>
    public static List<string> SplitVideo(string videoPath, IReadOnlyList<Shot> shots, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string name = Path.GetFileNameWithoutExtension(videoPath);
        var files = new List<string>();
        foreach (var shot in shots)
        {
            string file = Path.Combine(outputDir, $"{name}-Scene-{SceneNumber(shot.Number, shots.Count)}.mp4");
            RunFfmpeg(["-v", "error", "-nostdin", "-y",
                "-ss", Stats.PyFloat(shot.Start.Seconds), "-i", videoPath, "-t", Stats.PyFloat(shot.Duration.Seconds),
                "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "22",
                "-c:a", "aac", file]);
            files.Add(file);
        }
        return files;
    }

    static void RunFfmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {stderr}");
    }
}
