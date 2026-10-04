using System.Diagnostics;
using System.Globalization;

namespace ShotDetector;

/// <summary>Frame rate as an exact fraction (e.g. 30000/1001), as reported by ffprobe.</summary>
public readonly record struct Fps(int Num, int Den)
{
    public double Value => (double)Num / Den;

    public static Fps Parse(string s)
    {
        var parts = s.Split('/');
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture),
                   parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
    }
}

/// <summary>
/// Reads frames by piping ffmpeg's rawvideo bgr24 output. Frames are downscaled the same way
/// PySceneDetect does it by default: factor = max(width, height) / 256 when that side is >= 256.
/// Differs (default): ffmpeg's swscale bilinear does the resize instead of cv2.resize(INTER_LINEAR).
/// It averages over the whole footprint, so content_val is lower on fine detail than PySceneDetect's.
/// With cvResize, ffmpeg sends full-size frames and <see cref="CvResize"/> replicates cv2 exactly
/// (slower: a 1080p frame is 6 MB through the pipe). YUV→BGR conversion is ffmpeg's either way.
/// </summary>
public sealed class VideoReader
{
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int Width { get; }
    public int Height { get; }
    public Fps Fps { get; }
    /// <summary>Container frame count from ffprobe; may be missing (0) or approximate.</summary>
    public long FrameCountHint { get; }

    readonly string _path;
    readonly bool _cvResize;

    public VideoReader(string path, bool cvResize = false)
    {
        _path = path;
        _cvResize = cvResize;
        string[] f = Run("ffprobe", ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate,nb_frames", "-of", "csv=p=0:nk=1", path])
            .Trim().Split(',');
        // csv order follows the stream's field order: width,height,r_frame_rate,nb_frames
        SourceWidth = int.Parse(f[0], CultureInfo.InvariantCulture);
        SourceHeight = int.Parse(f[1], CultureInfo.InvariantCulture);
        Fps = Fps.Parse(f[2]);
        FrameCountHint = f.Length > 3 && long.TryParse(f[3], out var n) ? n : 0;
        (Width, Height) = DownscaledSize(SourceWidth, SourceHeight);
    }

    /// <summary>PySceneDetect's compute_downscale_factor + resize size (Python round = half to even).</summary>
    public static (int W, int H) DownscaledSize(int width, int height, int minWidth = 256)
    {
        int largest = Math.Max(width, height);
        if (largest < minWidth)
            return (width, height);
        double factor = largest / (double)minWidth;
        return (Math.Max(1, (int)Math.Round(width / factor)), Math.Max(1, (int)Math.Round(height / factor)));
    }

    /// <summary>Yields every decoded frame. The same buffer is reused for each frame.</summary>
    public IEnumerable<byte[]> Frames()
    {
        bool resizeHere = _cvResize && (Width, Height) != (SourceWidth, SourceHeight);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", _path, "-map", "0:v:0",
                     // Emit every decoded frame once, like OpenCV does (no CFR dup/drop).
                     "-fps_mode", "passthrough",
                     "-vf", resizeHere ? "null" : $"scale={Width}:{Height}:flags=bilinear",
                     "-f", "rawvideo", "-pix_fmt", "bgr24", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.BaseStream;
        var frame = new byte[Width * Height * 3];
        var raw = resizeHere ? new byte[SourceWidth * SourceHeight * 3] : frame;
        while (stdout.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) == raw.Length)
        {
            if (resizeHere)
                CvResize.Linear(raw, SourceWidth, SourceHeight, frame, Width, Height);
            yield return frame;
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
    }

    static string Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} failed ({proc.ExitCode}): {stderr.Result}");
        return output;
    }
}
