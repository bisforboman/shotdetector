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
/// Differs: ffmpeg's swscale bilinear does the resize instead of cv2.resize(INTER_LINEAR), and does
/// the YUV→BGR conversion in the same pass, so pixel values (and content_val) differ slightly.
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

    public VideoReader(string path)
    {
        _path = path;
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
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", _path, "-map", "0:v:0",
                     // Emit every decoded frame once, like OpenCV does (no CFR dup/drop).
                     "-fps_mode", "passthrough",
                     "-vf", $"scale={Width}:{Height}:flags=bilinear",
                     "-f", "rawvideo", "-pix_fmt", "bgr24", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.BaseStream;
        var frame = new byte[Width * Height * 3];
        while (stdout.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false) == frame.Length)
            yield return frame;
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
