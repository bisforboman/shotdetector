using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Numerics;
using System.Globalization;

namespace ShotDetector;

/// <summary>Frame rate as an exact fraction (e.g. 30000/1001).</summary>
public readonly record struct Fps(int Num, int Den)
{
    /// <summary>The frame rate as a double, i.e. Python's float(frame_rate).</summary>
    public double Value => (double)Num / Den;

    /// <summary>Parses "num/den" or a whole number, as ffprobe prints rates.</summary>
    public static Fps Parse(string s)
    {
        var parts = s.Split('/');
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture),
                   parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
    }

    /// <summary>
    /// Port of PySceneDetect's framerate_to_fraction, applied to the float fps OpenCV reports:
    /// whole numbers stay whole, N*1000/1001 rates become exact NTSC fractions, and anything else
    /// (e.g. the average rate of a variable frame rate video) becomes Fraction(fps).limit_denominator(10000).
    /// </summary>
    public static Fps FromFloat(double fps)
    {
        if (fps == Math.Floor(fps))
            return new((int)fps, 1);
        double ntscBase = Math.Round(fps * 1001 / 1000);
        if (ntscBase > 0 && Math.Abs(ntscBase * 1000 / 1001 - fps) < 1e-3)
            return Reduce((long)ntscBase * 1000, 1001);
        var (n, d) = LimitDenominator(fps, 10000);
        return new((int)n, (int)d);
    }

    static Fps Reduce(long n, long d)
    {
        long g = (long)BigInteger.GreatestCommonDivisor(n, d);
        return new((int)(n / g), (int)(d / g));
    }

    /// <summary>Python's Fraction(x).limit_denominator(max): the closest fraction with denominator ≤ max.</summary>
    public static (long Num, long Den) LimitDenominator(double x, long maxDen)
    {
        // Exact value of the double as n/d.
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF) - 1075;
        BigInteger n = (bits & 0xF_FFFF_FFFF_FFFF) | (1L << 52), d = BigInteger.One;
        if (exponent > 0) n <<= exponent; else d <<= -exponent;
        var g = BigInteger.GreatestCommonDivisor(n, d);
        (n, d) = (n / g, d / g);
        if (d <= maxDen)
            return ((long)n, (long)d);

        BigInteger num0 = n, den0 = d, p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        while (true)
        {
            var a = BigInteger.Divide(n, d);
            var q2 = q0 + a * q1;
            if (q2 > maxDen) break;
            (p0, q0, p1, q1) = (p1, q1, p0 + a * p1, q2);
            (n, d) = (d, n - a * d);
        }
        var k = (maxDen - q0) / q1;
        var (bp, bq) = (p0 + k * p1, q0 + k * q1);
        // Pick p1/q1 if it is at least as close to num0/den0 as bp/bq (exact comparison).
        var err2 = BigInteger.Abs(p1 * den0 - num0 * q1) * bq;
        var err1 = BigInteger.Abs(bp * den0 - num0 * bq) * q1;
        return err2 <= err1 ? ((long)p1, (long)q1) : ((long)bp, (long)bq);
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
    /// <summary>Width of the video.</summary>
    public int SourceWidth { get; }
    /// <summary>Height of the video.</summary>
    public int SourceHeight { get; }
    /// <summary>Width of the frames the detectors see (downscaled like PySceneDetect).</summary>
    public int Width { get; }
    /// <summary>Height of the frames the detectors see.</summary>
    public int Height { get; }
    /// <summary>Frame rate as OpenCV reports it to PySceneDetect (the average rate).</summary>
    public Fps Fps { get; }
    /// <summary>Container frame count from ffprobe; may be missing (0) or approximate.</summary>
    public long FrameCountHint { get; }

    readonly string _path;
    readonly bool _ffmpegResize;
    readonly int _decodeThreads;
    readonly IYuv420Converter? _yuv420;
    readonly long[] _pts;        // presentation timestamps of the frames, in display order
    readonly long _startPts;
    readonly double _timeBase;   // seconds per pts unit, as OpenCV's r2d(time_base)
    readonly string _pixelFormat;

    /// <param name="path">Video file; ffprobe reads its properties right away.</param>
    /// <param name="ffmpegResize">Let ffmpeg downscale (faster, results can differ from PySceneDetect).</param>
    /// <param name="decodeThreads">ffmpeg decoder threads; 0 = ffmpeg's choice. Each frame thread
    /// holds its own reference frames, so this trades memory (about 25 MB per thread at 1080p) for speed.</param>
    /// <param name="yuv420Converter">Enables the yuv420p fast path (see <see cref="IYuv420Converter"/>).</param>
    public VideoReader(string path, bool ffmpegResize = false, int decodeThreads = 4, IYuv420Converter? yuv420Converter = null)
    {
        _yuv420 = yuv420Converter;
        _path = path;
        _ffmpegResize = ffmpegResize;
        _decodeThreads = decodeThreads;
        // One demux-only pass: stream properties plus every packet's pts (no decoding).
        var stream = new Dictionary<string, string>();
        var pts = new List<long>();
        foreach (var line in Run("ffprobe", ["-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=width,height,pix_fmt,r_frame_rate,avg_frame_rate,time_base,start_pts,nb_frames:packet=pts",
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
        // OpenCV's CAP_PROP_FPS is the average frame rate (the nominal one only if that is unknown),
        // which matters for variable frame rate video.
        var avg = Fps.Parse(stream.GetValueOrDefault("avg_frame_rate", "0/0"));
        Fps = Fps.FromFloat(avg.Num > 0 && avg.Den > 0 ? avg.Value : Fps.Parse(stream["r_frame_rate"]).Value);
        FrameCountHint = long.TryParse(stream.GetValueOrDefault("nb_frames"), out var n) ? n : 0;
        (Width, Height) = DownscaledSize(SourceWidth, SourceHeight);
        _pixelFormat = stream.GetValueOrDefault("pix_fmt", "");

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
    public FrameTime Position(int frame)
    {
        if (frame < _pts.Length)
        {
            double ms = (_pts[frame] - _startPts) * _timeBase * 1000;
            long micros = (long)Math.Round(ms * 1000);
            if (micros > 0)
                return FrameTime.Pts(micros, 1_000_000, Fps);
        }
        // PySceneDetect's fallback uses frame_number - 1 with frame_number already advanced, i.e. this frame.
        return FrameTime.Pts((long)frame * Fps.Den, Fps.Num, Fps);
    }

    /// <summary>
    /// The frame PySceneDetect's OpenCV backend reads after seek(seconds): FrameTimecode(seconds).frame_num,
    /// i.e. Python's round(seconds * fps), ties to even on the double product. Matched scenedetect on
    /// all 120 thumbnails probed across 24, 25 and 29.97 fps clips.
    /// Differs: for variable frame rate video OpenCV reads forward to correct its estimate; this
    /// keeps the average-fps estimate, so thumbnails can be a few frames off there.
    /// </summary>
    public int FrameAt(double seconds) => (int)Math.Round(seconds * Fps.Value);

    /// <summary>
    /// The position PySceneDetect reads once decoding has stopped. OpenCV reports CAP_PROP_POS_MSEC
    /// as 0 at that point, so it is always the frame-number fallback for the last frame.
    /// The last scene ends one frame after it.
    /// </summary>
    public FrameTime PositionAfterDecoding(int frameCount) => FrameTime.Pts((long)(frameCount - 1) * Fps.Den, Fps.Num, Fps);

    /// <summary>PySceneDetect's compute_downscale_factor + resize size (Python round = half to even).</summary>
    public static (int W, int H) DownscaledSize(int width, int height, int minWidth = 256)
    {
        int largest = Math.Max(width, height);
        if (largest < minWidth)
            return (width, height);
        double factor = largest / (double)minWidth;
        return (Math.Max(1, (int)Math.Round(width / factor)), Math.Max(1, (int)Math.Round(height / factor)));
    }

    /// <summary>How frames get from ffmpeg to the downscaled BGR the detectors see.</summary>
    public string Pipeline => !_ffmpegResize && (Width, Height) != (SourceWidth, SourceHeight)
        // swscale's unscaled yuv420p converter (what OpenCV gets) needs an even height.
        ? (_yuv420 is not null && _pixelFormat == "yuv420p" && SourceHeight % 2 == 0 ? "yuv420p+sampled" : "bgr24+resize")
        : "ffmpeg-scale";

    /// <summary>
    /// Yields every decoded frame, downscaled. A background thread drains the pipe while the caller
    /// processes the current frame, so a yielded buffer is only valid until the next one.
    /// Pipelines: "yuv420p+sampled" (with a yuv420 converter) has ffmpeg send raw yuv420p (no conversion, half the bytes of BGR)
    /// and converts only the pixels cv2.resize reads; "bgr24+resize" has ffmpeg convert whole frames;
    /// "ffmpeg-scale" has ffmpeg downscale (--ffmpeg-resize, or when no resize is needed).
    /// </summary>
    public IEnumerable<byte[]> Frames()
    {
        string pipeline = Pipeline;
        bool resizeHere = pipeline != "ffmpeg-scale", yuv = pipeline == "yuv420p+sampled";
        // On Windows, stdout redirection uses a 4 KB pipe, which makes full-size frames crawl
        // (19 s vs 12 s for 5000 1080p frames), so ffmpeg writes to a named pipe with a big buffer.
        string pipeName = $"shotdetector_{Guid.NewGuid():N}";
        using var server = OperatingSystem.IsWindows()
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize: 8 << 20, outBufferSize: 0)
            : null;

        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = server is null, RedirectStandardError = true };
        // Emit every decoded frame once, like OpenCV does (no CFR dup/drop).
        string[] args = ["-v", "error", "-nostdin", "-y", "-threads", $"{_decodeThreads}", "-i", _path, "-map", "0:v:0", "-fps_mode", "passthrough"];
        args = pipeline switch
        {
            "yuv420p+sampled" => [.. args, "-f", "rawvideo", "-pix_fmt", "yuv420p"],
            // OpenCV converts to BGR with swscale's defaults (BT.601) and SWS_BICUBIC, ignoring
            // the stream's colour tags, so do the same.
            "bgr24+resize" => [.. args, "-vf", "scale=in_color_matrix=bt601:flags=bicubic,format=bgr24",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            _ => [.. args, "-vf", $"scale={Width}:{Height}:in_color_matrix=bt601:flags=bilinear,format=bgr24",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(server is null ? "-" : $@"\\.\pipe\{pipeName}");

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
        int readSize = yuv ? IYuv420Converter.FrameSize(SourceWidth, SourceHeight)
            : resizeHere ? SourceWidth * SourceHeight * 3
            : Width * Height * 3;
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
            var resizer = resizeHere ? new CvResize(SourceWidth, SourceHeight, Width, Height) : null;
            var small = new byte[Width * Height * 3];
            byte[]? previous = null;
            foreach (var buffer in full.GetConsumingEnumerable())
            {
                if (previous is not null)
                    free.Add(previous);
                if (resizer is null)
                {
                    previous = buffer;
                    yield return buffer;
                    continue;
                }
                if (yuv)
                    resizer.ResizeYuv420(buffer, small, _yuv420!);
                else
                    resizer.Resize(buffer, small);
                free.Add(buffer);
                yield return small;
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
