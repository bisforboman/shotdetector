using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
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
/// By default ffmpeg sends full-size frames and <see cref="CvResize"/> replicates cv2.resize exactly
/// (a 1080p frame is 6 MB through the pipe). With ffmpegResize, ffmpeg's swscale bilinear downscales
/// instead: faster, but it averages over the whole footprint, so content_val is lower on fine detail
/// and cuts can differ from PySceneDetect's.
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
    readonly bool _ffmpegResize;

    public VideoReader(string path, bool ffmpegResize = false)
    {
        _path = path;
        _ffmpegResize = ffmpegResize;
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

    /// <summary>
    /// Yields every decoded frame. A background thread reads (and resizes) the next frames while
    /// the caller processes the current one, so a yielded buffer is only valid until the next one.
    /// </summary>
    public IEnumerable<byte[]> Frames()
    {
        bool resizeHere = !_ffmpegResize && (Width, Height) != (SourceWidth, SourceHeight);
        // On Windows, stdout redirection uses a 4 KB pipe, which makes full-size frames crawl
        // (19 s vs 12 s for 5000 1080p frames), so ffmpeg writes to a named pipe with a big buffer.
        string pipeName = $"shotdetector_{Guid.NewGuid():N}";
        using var server = OperatingSystem.IsWindows()
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize: 8 << 20, outBufferSize: 0)
            : null;

        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = server is null, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-y", "-i", _path, "-map", "0:v:0",
                     // Emit every decoded frame once, like OpenCV does (no CFR dup/drop).
                     "-fps_mode", "passthrough",
                     // OpenCV converts to BGR with swscale's defaults (BT.601) and SWS_BICUBIC, ignoring
                     // the stream's colour tags, so do the same.
                     "-vf", resizeHere
                         ? "scale=in_color_matrix=bt601:flags=bicubic,format=bgr24"
                         : $"scale={Width}:{Height}:in_color_matrix=bt601:flags=bilinear,format=bgr24",
                     "-f", "rawvideo", "-pix_fmt", "bgr24", server is null ? "-" : $@"\\.\pipe\{pipeName}" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        Stream input = server ?? proc.StandardOutput.BaseStream;
        if (server is not null)
        {
            var connected = server.WaitForConnectionAsync();
            if (Task.WhenAny(connected, proc.WaitForExitAsync()).Result != connected)
                throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
        }

        // The reader thread only drains the pipe (the bottleneck); resizing happens on the caller's
        // thread. Three buffers: one being filled, one queued, one being resized/processed.
        using var cancel = new CancellationTokenSource();
        using var free = new BlockingCollection<byte[]>();
        using var full = new BlockingCollection<byte[]>();
        int readSize = resizeHere ? SourceWidth * SourceHeight * 3 : Width * Height * 3;
        for (int i = 0; i < 3; i++)
            free.Add(new byte[readSize]);
        var reader = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var buffer = free.Take(cancel.Token);
                    if (input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
                        break;
                    full.Add(buffer);
                }
            }
            catch (OperationCanceledException) { }
            finally { full.CompleteAdding(); }
        });

        try
        {
            var small = resizeHere ? new byte[Width * Height * 3] : null;
            byte[]? previous = null;
            foreach (var buffer in full.GetConsumingEnumerable())
            {
                if (previous is not null)
                    free.Add(previous);
                if (small is not null)
                {
                    CvResize.Linear(buffer, SourceWidth, SourceHeight, small, Width, Height);
                    free.Add(buffer);
                }
                else
                    previous = buffer;
                yield return small ?? buffer;
            }
            reader.Wait(); // rethrows read errors
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
        }
        finally
        {
            // Caller stopped early (or we failed): stop the reader and ffmpeg before disposing.
            cancel.Cancel();
            if (!proc.HasExited)
                proc.Kill();
            try { reader.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
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
