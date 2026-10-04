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
    readonly long[] _pts;        // presentation timestamps of the frames, in display order
    readonly long _startPts;
    readonly double _timeBase;   // seconds per pts unit, as OpenCV's r2d(time_base)

    public VideoReader(string path, bool ffmpegResize = false)
    {
        _path = path;
        _ffmpegResize = ffmpegResize;
        // One demux-only pass: stream properties plus every packet's pts (no decoding).
        var stream = new Dictionary<string, string>();
        var pts = new List<long>();
        foreach (var line in Run("ffprobe", ["-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=width,height,r_frame_rate,time_base,start_pts,nb_frames:packet=pts",
                     "-of", "default=nw=1", path]).Split('\n', StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq], value = line[(eq + 1)..];
            if (key == "pts")
            {
                if (long.TryParse(value, CultureInfo.InvariantCulture, out long p)) pts.Add(p);
            }
            else
                stream[key] = value;
        }
        SourceWidth = int.Parse(stream["width"], CultureInfo.InvariantCulture);
        SourceHeight = int.Parse(stream["height"], CultureInfo.InvariantCulture);
        Fps = Fps.Parse(stream["r_frame_rate"]);
        FrameCountHint = long.TryParse(stream.GetValueOrDefault("nb_frames"), out var n) ? n : 0;
        (Width, Height) = DownscaledSize(SourceWidth, SourceHeight);

        var tb = Fps.Parse(stream["time_base"]);
        _timeBase = tb.Num / (double)tb.Den;
        _startPts = long.TryParse(stream.GetValueOrDefault("start_pts"), out var s) ? s : 0;
        pts.Sort(); // packets come in decode order
        _pts = [.. pts];
    }

    /// <summary>
    /// The decoded frame's position as PySceneDetect's OpenCV backend reports it: CAP_PROP_POS_MSEC
    /// ((pts - start) * time_base * 1000) rounded to µs, or (frame - 1) / fps when that is not positive.
    /// Assumes OpenCV's best-effort timestamps equal the sorted packet pts, which holds for the
    /// sample clips (mp4, mov, m4v, mkv, ogg) but not for every stream (e.g. missing pts).
    /// </summary>
    public PyTime Position(int frame)
    {
        if (frame < _pts.Length)
        {
            double ms = (_pts[frame] - _startPts) * _timeBase * 1000;
            long micros = (long)Math.Round(ms * 1000);
            if (micros > 0)
                return PyTime.Pts(micros, 1_000_000, Fps);
        }
        // PySceneDetect's fallback uses frame_number - 1 with frame_number already advanced, i.e. this frame.
        return PyTime.Pts((long)frame * Fps.Den, Fps.Num, Fps);
    }

    /// <summary>
    /// End of the last scene: PySceneDetect takes the position after decoding stops, plus one frame.
    /// OpenCV reports CAP_PROP_POS_MSEC as 0 at that point, so it is always the frame-number fallback.
    /// </summary>
    public PyTime EndPosition(int frameCount) => PyTime.Pts((long)(frameCount - 1) * Fps.Den, Fps.Num, Fps).PlusFrames(1);

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
