using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Numerics;
using System.Globalization;

namespace ShotDetector;

/// <summary>How <see cref="VideoReader"/> gets downscaled BGR frames out of ffmpeg.</summary>
public enum FramePipeline
{
    /// <summary>ffmpeg downscales (bilinear): fastest, but not what PySceneDetect computes.</summary>
    FfmpegScale,
    /// <summary>ffmpeg sends full-size BGR frames and <c>cv2.resize</c>'s port downscales them exactly.</summary>
    FullFrameResize,
    /// <summary>ffmpeg sends raw yuv420p; only the pixels the resize reads are converted (needs an <see cref="IYuv420Converter"/>).</summary>
    Yuv420Sampled,
}

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
    internal static (long Num, long Den) LimitDenominator(double x, long maxDen)
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

    /// <summary>Which PySceneDetect release's frame rate, downscaling and positions this reader reproduces.</summary>
    public PySceneDetectVersion Compatibility { get; }

    /// <summary>The part of the (upright) frame analysed, in pixels: left, top, width, height. The whole frame unless cropped.</summary>
    public (int X, int Y, int Width, int Height) CropRegion { get; }

    /// <summary>Frames the stream is expected to have (from its packets), for progress; 0 if unknown.</summary>
    public int ExpectedFrames => _pts.Length > 0 ? _pts.Length : (int)FrameCountHint;

    /// <summary>The ffmpeg executable used (see <see cref="DetectionOptions.FfmpegDirectory"/>).</summary>
    internal string FfmpegExe { get; }

    readonly string _path;
    readonly bool _ffmpegResize;
    readonly int _decodeThreads;
    readonly IYuv420Converter? _yuv420;
    readonly long[] _pts;        // presentation timestamps of the frames, in display order
    readonly long _startPts;
    readonly double _timeBase;   // seconds per pts unit, as OpenCV's r2d(time_base)
    readonly string _pixelFormat;
    readonly IYuv420Converter? _yuv420ForVideo;   // _yuv420 configured for this video's colours

    /// <param name="path">Video file; ffprobe reads its properties right away.</param>
    /// <param name="options">Uses FfmpegResize, DecodeThreads, Yuv420Converter, Compatibility and FfmpegDirectory.</param>
    /// <param name="cancellationToken">Cancels the ffprobe run (the process is killed).</param>
    public VideoReader(string path, DetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new DetectionOptions();
        var compatibility = o.Compatibility;
        Compatibility = compatibility;
        _yuv420 = o.Yuv420Converter;
        _path = path;
        _ffmpegResize = o.FfmpegResize;
        _decodeThreads = o.DecodeThreads;
        FfmpegExe = Executable(o.FfmpegDirectory, "ffmpeg");
        // One demux-only pass: stream properties plus every packet's pts (no decoding).
        var stream = new Dictionary<string, string>();
        var pts = new List<long>();
        foreach (var line in Run(Executable(o.FfmpegDirectory, "ffprobe"), ["-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=width,height,pix_fmt,color_space,color_range,r_frame_rate,avg_frame_rate,time_base,start_pts,nb_frames:stream_side_data=rotation:packet=pts",
                     "-of", "default=nw=1", path], cancellationToken).Split('\n', StringSplitOptions.TrimEntries))
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
        // Phone video is often stored sideways with a rotation tag. ffmpeg and OpenCV both rotate it
        // upright when decoding, so for ±90° the frames we get (and OpenCV's reported size) are
        // height x width.
        if (double.TryParse(stream.GetValueOrDefault("rotation"), CultureInfo.InvariantCulture, out double rotation)
            && Math.Abs(Math.Round(rotation)) % 180 == 90)
            (SourceWidth, SourceHeight) = (SourceHeight, SourceWidth);
        // OpenCV's CAP_PROP_FPS is the average frame rate (the nominal one only if that is unknown),
        // which matters for variable frame rate video.
        var avg = Fps.Parse(stream.GetValueOrDefault("avg_frame_rate", "0/0"));
        var rate = avg.Num > 0 && avg.Den > 0 ? avg : Fps.Parse(stream["r_frame_rate"]);
        // 0.7.1 turns it into a clean fraction (framerate_to_fraction); 0.6.4 uses the float as is.
        Fps = compatibility == PySceneDetectVersion.V0_6_4 ? rate : Fps.FromFloat(rate.Value);
        FrameCountHint = long.TryParse(stream.GetValueOrDefault("nb_frames"), out var n) ? n : 0;
        // Crop like scenedetect: inclusive corners. The "effective size" its downscale factor comes
        // from is one pixel larger than the crop in both directions (its crop setter adds 1 to the far
        // corner and detect_scenes adds 1 again), while the frame is sliced to the real crop. Reproduced as is.
        CropRegion = (0, 0, SourceWidth, SourceHeight);
        var effective = (SourceWidth, SourceHeight);
        if (o.Crop is { } c)
        {
            int minX = Math.Min(c.X0, c.X1), minY = Math.Min(c.Y0, c.Y1), maxX = Math.Max(c.X0, c.X1), maxY = Math.Max(c.Y0, c.Y1);
            if (minX < 0 || minY < 0)
                throw new ArgumentException("Crop coordinates must be >= 0.");
            if (minX >= SourceWidth || minY >= SourceHeight)
                throw new ArgumentException("Crop starts outside the video.");
            CropRegion = (minX, minY, Math.Min(maxX + 1, SourceWidth) - minX, Math.Min(maxY + 1, SourceHeight) - minY);
            effective = (CropRegion.Width + 1, CropRegion.Height + 1);
        }
        (Width, Height) = compatibility == PySceneDetectVersion.V0_6_4
            ? DownscaledSize064(effective.Item1, effective.Item2, CropRegion.Width, CropRegion.Height)
            : DownscaledSize(effective.Item1, effective.Item2, CropRegion.Width, CropRegion.Height);
        _pixelFormat = stream.GetValueOrDefault("pix_fmt", "");
        // Like OpenCV with FFmpeg 8 (and ffmpeg's own scale filter), conversion follows the colour tags.
        bool fullRange = _pixelFormat == "yuvj420p" || stream.GetValueOrDefault("color_range") == "pc";
        _yuv420ForVideo = _yuv420?.ForColor(stream.GetValueOrDefault("color_space", ""), fullRange);

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
    /// In 0.6.4 a position is just the frame number.
    /// </summary>
    public FrameTime Position(int frame)
    {
        if (Compatibility == PySceneDetectVersion.V0_6_4)
            return FrameTime.Frame064(frame, Fps);
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
    /// The frame a start time selects: the one scenedetect's OpenCV seek lands on for constant frame
    /// rate video (round(seconds × fps)). On variable frame rate video OpenCV's seek is an artifact of
    /// its frame counting (it can land a second off), so there this is the first frame at or after
    /// half a frame before the time instead.
    /// </summary>
    public int SeekFrame(double seconds)
    {
        if (seconds <= 0 || _pts.Length == 0)
            return 0;
        int estimate = Math.Clamp(FrameAt(seconds), 0, _pts.Length - 1);
        if (Math.Abs(Position(estimate).Seconds - seconds) <= 1.0 / Fps.Value)
            return estimate;
        double target = seconds - 0.5 / Fps.Value;
        int lo = 0, hi = _pts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Position(mid).Seconds >= target) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// The position PySceneDetect reads once decoding has stopped. OpenCV reports CAP_PROP_POS_MSEC
    /// as 0 at that point, so it is always the frame-number fallback for the last frame.
    /// The last scene ends one frame after it.
    /// </summary>
    public FrameTime PositionAfterDecoding(int frameCount) => Compatibility == PySceneDetectVersion.V0_6_4
        ? FrameTime.Frame064(frameCount - 1, Fps)
        : FrameTime.Pts((long)(frameCount - 1) * Fps.Den, Fps.Num, Fps);

    /// <summary>
    /// PySceneDetect 0.6.4's downscale: a whole-number factor width // 256 (from the width only), then
    /// cv2.resize to (round(w / factor), round(h / factor)); no resize when the factor is 1.
    /// </summary>
    internal static (int W, int H) DownscaledSize064(int width, int height, int minWidth = 256) => DownscaledSize064(width, height, width, height, minWidth);

    /// <summary>The factor comes from the effective size, the result from the frame's actual size.</summary>
    internal static (int W, int H) DownscaledSize064(int effectiveWidth, int effectiveHeight, int width, int height, int minWidth = 256)
    {
        int factor = effectiveWidth < minWidth ? 1 : effectiveWidth / minWidth;
        return factor <= 1 ? (width, height) : ((int)Math.Round(width / (double)factor), (int)Math.Round(height / (double)factor));
    }

    /// <summary>PySceneDetect 0.7.1's compute_downscale_factor + resize size (Python round = half to even).</summary>
    internal static (int W, int H) DownscaledSize(int width, int height, int minWidth = 256) => DownscaledSize(width, height, width, height, minWidth);

    /// <summary>The factor comes from the effective size, the result from the frame's actual size.</summary>
    internal static (int W, int H) DownscaledSize(int effectiveWidth, int effectiveHeight, int width, int height, int minWidth = 256)
    {
        int largest = Math.Max(effectiveWidth, effectiveHeight);
        if (largest < minWidth)
            return (width, height);
        double factor = largest / (double)minWidth;
        return (Math.Max(1, (int)Math.Round(width / factor)), Math.Max(1, (int)Math.Round(height / factor)));
    }

    /// <summary>How frames get from ffmpeg to the downscaled BGR the detectors see.</summary>
    public FramePipeline Pipeline => !_ffmpegResize && (Width, Height) != (CropRegion.Width, CropRegion.Height)
        // swscale's unscaled yuv420p converter (what OpenCV gets) needs an even height.
        ? (_yuv420ForVideo is not null && _pixelFormat is "yuv420p" or "yuvj420p" && SourceHeight % 2 == 0 ? FramePipeline.Yuv420Sampled : FramePipeline.FullFrameResize)
        : FramePipeline.FfmpegScale;

    /// <summary>
    /// Yields every decoded frame, downscaled. A background thread drains the pipe while the caller
    /// processes the current frame, so a yielded buffer is only valid until the next one.
    /// Cancelling <paramref name="cancellationToken"/> kills ffmpeg and throws OperationCanceledException.
    /// Pipelines: "yuv420p+sampled" (with a yuv420 converter) has ffmpeg send raw yuv420p (no conversion, half the bytes of BGR)
    /// and converts only the pixels cv2.resize reads; "bgr24+resize" has ffmpeg convert whole frames;
    /// "ffmpeg-scale" has ffmpeg downscale (--ffmpeg-resize, or when no resize is needed).
    /// </summary>
    public IEnumerable<byte[]> Frames(CancellationToken cancellationToken = default) => Frames(0, null, cancellationToken);

    /// <summary>
    /// Yields frames <paramref name="startFrame"/> .. <paramref name="startFrame"/> + <paramref name="count"/> - 1
    /// (count null: to the end), downscaled; see <see cref="Frames(CancellationToken)"/>.
    /// </summary>
    public IEnumerable<byte[]> Frames(int startFrame, int? count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count is <= 0)
            yield break;
        var pipeline = Pipeline;
        bool resizeHere = pipeline != FramePipeline.FfmpegScale, yuv = pipeline == FramePipeline.Yuv420Sampled;
        // On Windows, stdout redirection uses a 4 KB pipe, which makes full-size frames crawl
        // (19 s vs 12 s for 5000 1080p frames), so ffmpeg writes to a named pipe with a big buffer.
        string pipeName = $"shotdetector_{Guid.NewGuid():N}";
        using var server = OperatingSystem.IsWindows()
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize: 8 << 20, outBufferSize: 0)
            : null;

        var psi = new ProcessStartInfo(FfmpegExe) { RedirectStandardOutput = server is null, RedirectStandardError = true };
        // Emit every decoded frame once, like OpenCV does (no CFR dup/drop).
        // Seeking exactly: which frame ffmpeg's -ss starts on depends on the codec, so seek two
        // frames early (to a keyframe before that), keep the original timestamps, and let a select
        // filter drop everything before the wanted frame's pts.
        string[] seek = [], select = [];
        if (startFrame > 0 && startFrame < _pts.Length)
        {
            int early = Math.Max(startFrame - 2, 0);
            if (early > 0)
                seek = ["-ss", ((_pts[early] - _startPts) * _timeBase).ToString("R", CultureInfo.InvariantCulture), "-copyts"];
            select = ["-vf", $"select=gte(pts\\,{_pts[startFrame]})"];
        }
        string[] limit = count is { } n ? ["-frames:v", n.ToString(CultureInfo.InvariantCulture)] : [];
        // The crop (an exact pixel copy) comes after the colour conversion, as OpenCV converts the
        // whole frame and scenedetect slices the result; in the yuv420p pipeline it happens here instead.
        string crop = CropRegion.Width == SourceWidth && CropRegion.Height == SourceHeight
            ? "" : $",crop={CropRegion.Width}:{CropRegion.Height}:{CropRegion.X}:{CropRegion.Y}";
        string[] args = ["-v", "error", "-nostdin", "-y", "-threads", $"{_decodeThreads}", .. seek, "-i", _path, "-map", "0:v:0", "-fps_mode", "passthrough", .. limit];
        string prefix = select.Length > 0 ? select[1] + "," : "";
        args = pipeline switch
        {
            // The source's own format, so ffmpeg passes the samples through untouched.
            FramePipeline.Yuv420Sampled => [.. args, .. select, "-f", "rawvideo", "-pix_fmt", _pixelFormat],
            // Like OpenCV: SWS_BICUBIC, colour matrix and range from the stream's tags (BT.601 if untagged).
            FramePipeline.FullFrameResize => [.. args, "-vf", prefix + "scale=flags=bicubic,format=bgr24" + crop,
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            _ => [.. args, "-vf", prefix + "scale=flags=bicubic,format=bgr24" + crop + $",scale={Width}:{Height}:flags=bilinear",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(server is null ? "-" : $@"\\.\pipe\{pipeName}");

        using var proc = Start(psi);
        var stderr = proc.StandardError.ReadToEndAsync();
        Stream input = server ?? proc.StandardOutput.BaseStream;
        if (server is not null)
        {
            var connected = server.WaitForConnectionAsync(cancellationToken);
            try
            {
                if (Task.WaitAny([connected, proc.WaitForExitAsync(cancellationToken)], cancellationToken) != 0)
                    throw new InvalidOperationException($"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                throw;
            }
        }

        // The reader thread only drains the pipe (the bottleneck); resizing happens on the caller's
        // thread. Three buffers: one being filled, one queued, one being resized/processed.
        // Cancelled by the caller's token, or by us when the caller stops iterating.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var free = new BlockingCollection<byte[]>();
        using var full = new BlockingCollection<byte[]>();
        int readSize = yuv ? IYuv420Converter.FrameSize(SourceWidth, SourceHeight)
            : resizeHere ? CropRegion.Width * CropRegion.Height * 3
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
            // Stopped: cancelled, or (if it ever outlived the grace period below) its buffers were disposed.
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                try { full.CompleteAdding(); } catch (ObjectDisposedException) { }
            }
        });

        try
        {
            var resizer = resizeHere ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
            var small = new byte[Width * Height * 3];
            byte[]? previous = null;
            foreach (var buffer in full.GetConsumingEnumerable(cancel.Token))
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
                    resizer.ResizeYuv420(buffer, small, _yuv420ForVideo!, SourceWidth, SourceHeight, CropRegion.X, CropRegion.Y);
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
            Kill(proc);
            try { reader.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    /// <summary>Kills a process unless it has already exited (which can happen at any moment, so no check-then-kill).</summary>
    static void Kill(Process proc)
    {
        try { proc.Kill(); } catch (InvalidOperationException) { }
    }

    static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException(
                $"Could not start {psi.FileName}: {e.Message}. Install ffmpeg (which includes ffprobe) and put it on PATH, " +
                "or set DetectionOptions.FfmpegDirectory.", e);
        }
    }

    /// <summary>"ffmpeg" (found on PATH) or that executable in <paramref name="directory"/>.</summary>
    static string Executable(string? directory, string name) => directory is null
        ? name
        : Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>Runs ffmpeg/ffprobe to completion and returns its stdout; cancelling kills it.</summary>
    internal static string Run(string exe, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Start(psi);
        using var kill = cancellationToken.Register(() =>
        {
            Kill(proc);
        });
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} failed ({proc.ExitCode}): {stderr.Result}");
        return output;
    }
}
