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
    /// <summary>ffmpeg converts whole frames to BGR as above but sends only the pixels the exact resize reads (its remap filter): same result, a fraction of the bytes.</summary>
    SampledBgr,
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
public sealed partial class VideoReader
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
    internal long FrameCountHint { get; }

    /// <summary>The frame count OpenCV reports (CAP_PROP_FRAME_COUNT), which scenedetect takes as the video's end.</summary>
    internal long OpenCvFrameCount { get; }

    /// <summary>The video stream's field order (progressive, interlaced, or unknown).</summary>
    public FieldOrder FieldOrder { get; }

    /// <summary>The video codec's FFmpeg name.</summary>
    public string Codec { get; }

    /// <summary>The container's duration; null if unknown.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Whether frames are deinterlaced (<see cref="DetectionOptions.Deinterlace"/>, Auto resolved).</summary>
    public bool Deinterlaces => _deinterlace;

    /// <summary>These properties as a <see cref="VideoInfo"/>.</summary>
    public VideoInfo Info => new()
    {
        Width = SourceWidth, Height = SourceHeight, FrameRate = Fps, FrameCount = OpenCvFrameCount > 0 ? OpenCvFrameCount : null,
        Duration = Duration, Codec = Codec, PixelFormat = _pixelFormat, FieldOrder = FieldOrder, Rotation = _rotation,
        Container = Container, HasAudio = HasAudio,
    };

    /// <summary>FFmpeg's name for the container (its demuxer).</summary>
    public string Container { get; }

    /// <summary>
    /// Whether the file has an audio stream; null for streamed input. Looked up on first use (in-process when the
    /// libraries load, else one ffprobe call), as detection doesn't need it.
    /// </summary>
    public bool? HasAudio => _hasAudio ??= _stream is not null || Streaming ? null
        : _probesInProcess ? InProcessProbe.HasAudio(_path, _ffmpegDirectory)
        : Run(_ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", .. _inputOptions, _path],
            CancellationToken.None).Trim().Length > 0;
    bool? _hasAudio;
    readonly string _ffprobe;

    /// <summary>Which PySceneDetect release's frame rate, downscaling and positions this reader reproduces.</summary>
    public PySceneDetectVersion Compatibility { get; }

    /// <summary>The part of the (upright) frame analysed, in pixels: left, top, width, height. The whole frame unless cropped.</summary>
    public (int X, int Y, int Width, int Height) CropRegion { get; }

    /// <summary>Frames the stream is expected to have (from its packets), for progress; 0 if unknown (always when streaming).</summary>
    public int ExpectedFrames => _pts.Length > 0 ? _pts.Length : (int)FrameCountHint;

    /// <summary>
    /// True when the input is read once, start to end, without probing all of it first: a
    /// <see cref="Stream"/>, a URL, or <see cref="DetectionOptions.Streaming"/>. Frame timestamps then
    /// come from ffmpeg as frames arrive, the frame count is unknown, and seeking is not available.
    /// </summary>
    public bool Streaming { get; }

    /// <summary>The ffmpeg executable used (see <see cref="DetectionOptions.FfmpegDirectory"/>).</summary>
    internal string FfmpegExe { get; }

    readonly string _path;             // file path or URL; "pipe:0" for a Stream
    readonly Stream? _stream;
    readonly byte[] _prefix = [];      // what was read from _stream to probe it; fed to ffmpeg first
    readonly bool _ffmpegResize;
    readonly bool _deinterlace;        // DeinterlaceMode.On, or Auto with an interlaced stream
    readonly int? _decodeThreads;
    readonly string[] _inputOptions;   // ffmpeg/ffprobe options for the input (an image sequence's rate)
    readonly bool _fullFrames;         // tests: the full-frame pipeline instead of the sampled one
    readonly VideoDecoder _decoder;
    readonly string? _ffmpegDirectory;
    readonly int _rotation;            // the display rotation ffmpeg applies (degrees); 0 if none
    readonly bool _probesInProcess;    // read with FFmpeg's libraries rather than ffprobe

    /// <summary>The stream entries read from ffprobe (or the same in-process, <see cref="InProcessProbe"/>); files add :packet=pts.</summary>
    internal const string ProbeEntries = "stream=codec_name,field_order,width,height,pix_fmt,color_space,color_range,r_frame_rate,avg_frame_rate,time_base,start_pts,start_time,nb_frames,duration:stream_side_data=rotation:format=duration,format_name";
    readonly IYuv420Converter? _yuv420;
    readonly IYadif? _yadif;
    readonly long[] _pts;              // presentation timestamps of the frames, in display order (not when streaming)
    readonly List<long> _livePts = []; // the same, as frames arrive, when streaming (from the stream's start)
    // For a file in 0.7.1 mode: the packet (index into _pts) of each decoded frame since _liveStart. Normally
    // frame i is packet i, but a decoder drops frames it can't decode (corrupt data), and OpenCV then labels
    // every later frame by its own time, so frame numbers skip. Read from showinfo, as frames arrive.
    readonly List<int> _livePackets = [];
    int _liveStart;
    long _startPts;
    double _timeBase;                  // seconds per pts unit, as OpenCV's r2d(time_base)
    readonly double _startSeconds;
    readonly string _pixelFormat;
    readonly IYuv420Converter? _yuv420ForVideo;   // _yuv420 configured for this video's colours

    /// <param name="path">Video file, or a URL (http(s), rtsp, ... ; read as a stream).</param>
    /// <param name="options">Uses FfmpegResize, DecodeThreads, Yuv420Converter, Compatibility, Crop, FfmpegDirectory, Streaming and ProbeBytes.</param>
    /// <param name="cancellationToken">Cancels the ffprobe run (the process is killed).</param>
    internal VideoReader(string path, DetectionOptions? options = null, CancellationToken cancellationToken = default)
        : this(path, null, options, cancellationToken) { }

    /// <summary>Probes only the headers (no packet timestamps): for <see cref="ShotDetection.Probe(string, DetectionOptions?, CancellationToken)"/>.</summary>
    internal static VideoReader Headers(string path, DetectionOptions? options, CancellationToken cancellationToken) =>
        new(path, null, options, cancellationToken, headersOnly: true);

    /// <param name="video">The video's bytes, read once, so the container headers must come first:
    /// mkv, webm, ts, mov, or an mp4 written with "faststart" (an ordinary mp4 keeps them at the end).</param>
    /// <param name="options">See <see cref="VideoReader(string, DetectionOptions?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels the ffprobe run (the process is killed).</param>
    internal VideoReader(Stream video, DetectionOptions? options = null, CancellationToken cancellationToken = default)
        : this("pipe:0", video, options, cancellationToken) { }

    /// <summary>A Stream whose first bytes (<paramref name="prefix"/>) were already read from it.</summary>
    internal VideoReader(Stream video, byte[] prefix, DetectionOptions? options, CancellationToken cancellationToken)
        : this("pipe:0", video, options, cancellationToken, prefix: prefix) { }

    VideoReader(string path, Stream? video, DetectionOptions? options, CancellationToken cancellationToken, bool headersOnly = false,
        byte[]? prefix = null)
    {
        var o = options ?? new DetectionOptions();
        var compatibility = o.Compatibility;
        Compatibility = compatibility;
        _yuv420 = o.Yuv420Converter;
        _yadif = o.Yadif;
        _path = path;
        _stream = video;
        _ffmpegResize = o.FfmpegResize;
        _decodeThreads = o.DecodeThreads;
        _fullFrames = o.FullFrames;
        _decoder = o.Decoder;
        _ffmpegDirectory = o.FfmpegDirectory;
        FfmpegExe = Executable(o.FfmpegDirectory, "ffmpeg");
        Streaming = video is not null || o.Streaming || IsUrl(path);
        // scenedetect's frame rate override; an image sequence (no timestamps of its own) is read at that rate.
        Fps? frameRate = o.FrameRate is not { } fr ? null
            : fr <= 0 ? throw new ArgumentException("FrameRate must be positive.")
            : Fps.FromFloat(fr);
        _inputOptions = frameRate is { } r && video is null && path.Contains('%') && !IsUrl(path)
            ? ["-framerate", $"{r.Num}/{r.Den}"] : [];
        // One demux-only pass: stream properties, plus every packet's pts for a file (no decoding).
        // A Stream is probed from its first bytes, which are kept to feed ffmpeg later.
        string entries = ProbeEntries + (Streaming || headersOnly ? "" : ":packet=pts");
        string ffprobe = _ffprobe = Executable(o.FfmpegDirectory, "ffprobe");
        string[] probeArgs = ["-v", "error", .. _inputOptions, "-select_streams", "v:0", "-show_entries", entries, "-of", "default=nw=1", path];
        string probed;
        // A file, with FFmpeg's libraries available (as for decoding in-process): read the same with them, no ffprobe.
        // A Stream too: its first bytes, as ffprobe reads them from stdin.
        _probesInProcess = (video is not null || !Streaming) && _decoder != VideoDecoder.FfmpegProcess
            && (_decoder == VideoDecoder.InProcess || InProcess.CanLoad(_ffmpegDirectory));
        if (_probesInProcess && video is not null)
        {
            _prefix = prefix ?? ReadPrefix(video, o.ProbeBytes, cancellationToken);
            try
            {
                probed = InProcessProbe.Properties(path, _ffmpegDirectory, packets: false, streamPrefix: _prefix);
            }
            catch (ShotDetectionException e)
            {
                throw Failed(e.Reason, e.Message, e);
            }
        }
        else if (_probesInProcess)
            probed = InProcessProbe.Properties(path, _ffmpegDirectory, packets: !headersOnly, _inputOptions);
        else if (video is not null)
        {
            _prefix = prefix ?? ReadPrefix(video, o.ProbeBytes, cancellationToken);
            try
            {
                probed = Run(ffprobe, probeArgs, cancellationToken, stdin: _prefix);
            }
            catch (ShotDetectionException e)
            {
                throw Failed(e.Reason, e.Message, e);
            }
        }
        else
            probed = Run(ffprobe, probeArgs, cancellationToken);
        var stream = new Dictionary<string, string>();
        var pts = new List<long>();
        bool missingPts = false;
        foreach (var line in probed.Split('\n', StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq], value = line[(eq + 1)..];
            if (key == "pts")
            {
                if (long.TryParse(value, CultureInfo.InvariantCulture, out long p)) pts.Add(p); else missingPts = true;
            }
            else
                stream[key] = value;
        }
        if (!stream.ContainsKey("width"))
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"The input has no video stream: {path}");
        SourceWidth = int.Parse(stream["width"], CultureInfo.InvariantCulture);
        SourceHeight = int.Parse(stream["height"], CultureInfo.InvariantCulture);
        // Phone video is often stored sideways with a rotation tag. ffmpeg and OpenCV both rotate it
        // upright when decoding, so for ±90° the frames we get (and OpenCV's reported size) are
        // height x width.
        if (double.TryParse(stream.GetValueOrDefault("rotation"), CultureInfo.InvariantCulture, out double rotation))
            _rotation = (int)Math.Round(rotation) % 360;
        if (Math.Abs(_rotation) % 180 == 90)
            (SourceWidth, SourceHeight) = (SourceHeight, SourceWidth);
        // OpenCV's CAP_PROP_FPS is the average frame rate (the nominal one only if that is unknown),
        // which matters for variable frame rate video.
        var avg = Fps.Parse(stream.GetValueOrDefault("avg_frame_rate", "0/0"));
        var rate = avg.Num > 0 && avg.Den > 0 ? avg : Fps.Parse(stream["r_frame_rate"]);
        // scenedetect turns it into a clean fraction (framerate_to_fraction).
        Fps = frameRate ?? Fps.FromFloat(rate.Value);
        FrameCountHint = long.TryParse(stream.GetValueOrDefault("nb_frames"), out var n) ? n : 0;
        // OpenCV's CAP_PROP_FRAME_COUNT: the stream's frame count, else round(duration * fps), where the
        // duration is the container's (format=duration is printed after the stream's, so it wins here).
        OpenCvFrameCount = FrameCountHint > 0 ? FrameCountHint
            : double.TryParse(stream.GetValueOrDefault("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs)
                ? (long)Math.Floor(secs * rate.Value + 0.5) : 0;
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
        (Width, Height) = o.Downscale is { } d
            ? d <= 1 ? (CropRegion.Width, CropRegion.Height)
                : (Math.Max(1, (int)Math.Round(CropRegion.Width / (double)d)), Math.Max(1, (int)Math.Round(CropRegion.Height / (double)d)))
            : DownscaledSize(effective.Item1, effective.Item2, CropRegion.Width, CropRegion.Height);
        _pixelFormat = stream.GetValueOrDefault("pix_fmt", "");
        FieldOrder = stream.GetValueOrDefault("field_order") switch
        {
            "progressive" => FieldOrder.Progressive, "tt" => FieldOrder.TopFirst, "bb" => FieldOrder.BottomFirst,
            "tb" => FieldOrder.TopCodedBottomFirst, "bt" => FieldOrder.BottomCodedTopFirst, _ => FieldOrder.Unknown,
        };
        Codec = stream.GetValueOrDefault("codec_name", "");
        Container = stream.GetValueOrDefault("format_name", "");
        Duration = double.TryParse(stream.GetValueOrDefault("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double length)
            ? TimeSpan.FromSeconds(length) : null;
        _deinterlace = o.Deinterlace == DeinterlaceMode.On
            || o.Deinterlace == DeinterlaceMode.Auto && FieldOrder is not (FieldOrder.Progressive or FieldOrder.Unknown);
        // Like OpenCV with FFmpeg 8 (and ffmpeg's own scale filter), conversion follows the colour tags.
        bool fullRange = _pixelFormat == "yuvj420p" || stream.GetValueOrDefault("color_range") == "pc";
        _yuv420ForVideo = _yuv420?.ForColor(stream.GetValueOrDefault("color_space", ""), fullRange);

        var tb = Fps.Parse(stream["time_base"]);
        _timeBase = tb.Num / (double)tb.Den;
        _startPts = long.TryParse(stream.GetValueOrDefault("start_pts"), out var s) ? s : 0;
        _startSeconds = double.TryParse(stream.GetValueOrDefault("start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var st) ? st : 0;
        pts.Sort(); // packets come in decode order
        // Some containers (MPEG-PS) carry no pts on every packet. OpenCV then takes the frame's
        // dts (its pts when set and non-zero, else pkt_dts), in output order: get exactly that
        // from a decoding pass, which only such files pay for.
        _ptsFromFrames = missingPts;
        if (missingPts)
            pts = _probesInProcess ? InProcessProbe.FrameTimestamps(path, _ffmpegDirectory, _inputOptions) : FrameTimestamps(ffprobe, path, cancellationToken);
        _pts = [.. pts];
    }

    static List<long> FrameTimestamps(string ffprobe, string path, CancellationToken ct)
    {
        var result = new List<long>();
        long? framePts = null;
        string[] args = ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts,pkt_dts", "-of", "default=nw=1", path];
        foreach (var line in Run(ffprobe, args, ct).Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("pts="))
                framePts = long.TryParse(line[4..], CultureInfo.InvariantCulture, out long p) && p != 0 ? p : null;
            else if (line.StartsWith("pkt_dts="))
            {
                result.Add(framePts ?? (long.TryParse(line[8..], CultureInfo.InvariantCulture, out long d) ? d : 0));
                framePts = null;
            }
        }
        return result;
    }

    /// <summary>
    /// The packet a decoded frame is, from its absolute showinfo time: the first packet after
    /// <paramref name="previous"/> within half a frame of it; if none is (a time the packet list doesn't
    /// have), simply the next one.
    /// </summary>
    int PacketOf(double seconds, int previous)
    {
        double tolerance = 0.5 / Fps.Value;
        for (int i = previous + 1; i < _pts.Length; i++)
        {
            double t = _pts[i] * _timeBase;
            if (Math.Abs(t - seconds) <= tolerance)
                return i;
            if (t > seconds + tolerance)
                break;
        }
        return Math.Min(previous + 1, Math.Max(_pts.Length - 1, 0));
    }

    /// <summary>An ffmpeg/ffprobe failure, explained when it is the common streaming one.</summary>
    static ShotDetectionException Failed(ShotDetectionError reason, string message, Exception? inner = null) =>
        message.Contains("moov atom not found") || message.Contains("mp4,m4a,3gp,3g2,mj2 @") && message.Contains("partial file")
            ? new ShotDetectionException(ShotDetectionError.InvalidInput,
                "The stream's container headers aren't at the start (an mp4 without \"faststart\"), so it can't be " +
                "read as a stream. Use mkv/webm/ts/mov, an mp4 written with -movflags +faststart, or a file path.", inner)
            : new ShotDetectionException(reason, message, inner);

    static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && uri.Scheme != Uri.UriSchemeFile;

    /// <summary>
    /// Whether an MP4/MOV's media data (mdat) comes before its headers (moov) in these first bytes: an mp4 without
    /// "faststart", which can't be read from a pipe. False for other containers and when the bytes don't tell.
    /// </summary>
    internal static bool HeadersLast(ReadOnlySpan<byte> data)
    {
        long pos = 0;
        while (pos + 8 <= data.Length)
        {
            var box = data[(int)pos..];
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box);
            var type = box.Slice(4, 4);
            if (pos == 0 && !(type.SequenceEqual("ftyp"u8) || type.SequenceEqual("wide"u8) || type.SequenceEqual("free"u8)
                || type.SequenceEqual("skip"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("moov"u8)))
                return false; // not an ISO media file
            if (type.SequenceEqual("moov"u8))
                return false;
            if (type.SequenceEqual("mdat"u8))
                return true;
            if (size == 1)
            {
                if (box.Length < 16)
                    return false;
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
            }
            if (size < 8)
                return false; // to the end of the file (0), or not a box
            pos += size;
        }
        return false;
    }

    /// <summary>
    /// The whole stream (<paramref name="prefix"/>, already read, then the rest) in a temporary file, deleted when the
    /// result is disposed: for an mp4 without "faststart", whose headers come last.
    /// </summary>
    internal static TempFile Spill(byte[] prefix, Stream video, CancellationToken cancellationToken)
    {
        var file = new TempFile(Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.mp4"));
        try
        {
            using var output = File.Create(file.Path);
            output.Write(prefix);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = video.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
            }
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static byte[] ReadPrefix(Stream video, int bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[bytes];
        int total = 0;
        while (total < bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = video.Read(buffer, total, bytes - total);
            if (n == 0) break;
            total += n;
        }
        return buffer[..total];
    }

    /// <summary>
    /// The decoded frame's position as PySceneDetect's OpenCV backend reports it: CAP_PROP_POS_MSEC
    /// ((pts - start) * time_base * 1000) rounded to µs, or (frame - 1) / fps when that is not positive.
    /// Assumes OpenCV's best-effort timestamps equal the sorted packet pts, which holds for the
    /// sample clips (mp4, mov, m4v, mkv, ogg) but not for every stream (e.g. missing pts).
    /// </summary>
    public FrameTime Position(int frame)
    {
        int packet = frame - _liveStart is int k && k >= 0 && k < _livePackets.Count ? _livePackets[k] : frame;
        long? pts = Streaming ? (frame < _livePts.Count ? _livePts[frame] : null) : (packet < _pts.Length ? _pts[packet] : null);
        if (pts is { } p)
        {
            double ms = (Streaming ? p : p - _startPts) * _timeBase * 1000;
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
    internal int FrameAt(double seconds) => (int)Math.Round(seconds * Fps.Value);

    /// <summary>
    /// The frame a start time selects: the one scenedetect's OpenCV seek lands on for constant frame
    /// rate video (round(seconds × fps)). On variable frame rate video OpenCV's seek is an artifact of
    /// its frame counting (it can land a second off), so there this is the first frame at or after
    /// half a frame before the time instead.
    /// </summary>
    internal int SeekFrame(double seconds)
    {
        if (Streaming)
            throw new NotSupportedException("A streamed input can't be seeked; StartTime isn't available.");
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
    internal FrameTime PositionAfterDecoding(int frameCount) => FrameTime.Pts((long)(frameCount - 1) * Fps.Den, Fps.Num, Fps);

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
        ? (_yuv420ForVideo is not null && _pixelFormat is "yuv420p" or "yuvj420p" && SourceHeight % 2 == 0 ? FramePipeline.Yuv420Sampled
            : _fullFrames || !DecodesInProcess && !SampledPays(CropRegion.Width, CropRegion.Height, Width, Height)
                ? FramePipeline.FullFrameResize : FramePipeline.SampledBgr)
        : FramePipeline.FfmpegScale;

    /// <summary>
    /// Whether the ffmpeg executable should send only the pixels the resize reads (its remap filter) rather than whole
    /// frames: when they are under a quarter of the frame. Above that, remap and its looping maps cost ffmpeg more than
    /// the pipe saves (Linux, 2 CPUs: whole frames 37-41% faster at 640x360 and 426x240, where the resize reads 64% and
    /// all of the pixels; equal at 1280x534, 16%; sampled far ahead at 1080p, 7%). Same results either way.
    /// </summary>
    internal static bool SampledPays(int sourceWidth, int sourceHeight, int width, int height) =>
        Math.Min(1.0, 2.0 * width / sourceWidth) * Math.Min(1.0, 2.0 * height / sourceHeight) < 0.25;

    /// <summary>
    /// Yields every decoded frame, downscaled. A background thread drains the pipe while the caller
    /// processes the current frame, so a yielded buffer is only valid until the next one.
    /// Cancelling <paramref name="cancellationToken"/> kills ffmpeg and throws OperationCanceledException.
    /// Pipelines: "yuv420p+sampled" (with a yuv420 converter) has ffmpeg send raw yuv420p (no conversion, half the bytes of BGR)
    /// and converts only the pixels cv2.resize reads; "bgr24+resize" has ffmpeg convert whole frames;
    /// "ffmpeg-scale" has ffmpeg downscale (--ffmpeg-resize, or when no resize is needed).
    /// </summary>
    internal IEnumerable<byte[]> Frames(CancellationToken cancellationToken = default) => Frames(0, null, cancellationToken);

    /// <summary>
    /// Yields frames <paramref name="startFrame"/> .. <paramref name="startFrame"/> + <paramref name="count"/> - 1
    /// (count null: to the end), downscaled; see <see cref="Frames(CancellationToken)"/>.
    /// </summary>
    internal IEnumerable<byte[]> Frames(int startFrame, int? count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count is <= 0)
            yield break;
        if (Streaming && startFrame > 0)
            throw new NotSupportedException("A streamed input can't be seeked.");
        if (DecodesInProcess)
        {
            bool any = false;
            foreach (var frame in FramesInProcess(startFrame, count, cancellationToken))
            {
                any = true;
                yield return frame;
            }
            // Auto: libraries that can't decode this codec (AV1 needs dav1d, which ShotDetector.Native leaves out)
            // give no frames at all; the ffmpeg executable decodes it instead, from here on.
            // A Stream can't be read twice: what it gave is what there is.
            if (any || _decoder != VideoDecoder.Auto || ExpectedFrames <= startFrame || _stream is not null)
                yield break;
            _inProcessGaveNothing = true;
        }
        var pipeline = Pipeline;
        bool resizeHere = pipeline != FramePipeline.FfmpegScale, yuv = pipeline == FramePipeline.Yuv420Sampled;
        // On Windows, stdout redirection uses a 4 KB pipe, which makes full-size frames crawl
        // (19 s vs 12 s for 5000 1080p frames), so ffmpeg writes to a named pipe with a big buffer.
        string pipeName = $"shotdetector_{Guid.NewGuid():N}";
        using var server = OperatingSystem.IsWindows()
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize: 8 << 20, outBufferSize: 0)
            : null;

        var psi = new ProcessStartInfo(FfmpegExe)
        {
            RedirectStandardOutput = server is null,
            RedirectStandardError = true,
            RedirectStandardInput = _stream is not null,
        };
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
            // yadif halves the time base, so after it compare times: halfway between the frame before and the wanted one.
            select = ["-vf", _deinterlace
                ? $"select=gte(t\\,{((_pts[startFrame - 1] + _pts[startFrame]) / 2.0 * _timeBase).ToString("R", CultureInfo.InvariantCulture)})"
                : $"select=gte(pts\\,{_pts[startFrame]})"];
        }
        string[] limit = count is { } n ? ["-frames:v", n.ToString(CultureInfo.InvariantCulture)] : [];
        // The crop (an exact pixel copy) comes after the colour conversion, as OpenCV converts the
        // whole frame and scenedetect slices the result; in the yuv420p pipeline it happens here instead.
        string crop = CropRegion.Width == SourceWidth && CropRegion.Height == SourceHeight
            ? "" : $",crop={CropRegion.Width}:{CropRegion.Height}:{CropRegion.X}:{CropRegion.Y}";
        // Each frame's timestamp is read from showinfo's log line, so ffmpeg logs at info level and the lines are
        // parsed as they arrive: when streaming there is no packet list, and for a file it says which packet it is.
        // Absolute timestamps for files, so they compare with the packet list as they are (without
        // -copyts ffmpeg shifts by the container's start, which can differ from the video stream's).
        if (!Streaming && seek.Length == 0)
            seek = ["-copyts"];
        string[] noStdin = _stream is null ? ["-nostdin"] : [];
        // SampledBgr: two 16-bit maps (source column and row of every pixel the resize reads, crop included)
        // that remap uses to keep just those pixels of the converted frame.
        bool sampled = pipeline == FramePipeline.SampledBgr;
        var resizer = resizeHere ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        string? maps = sampled ? WriteMaps(resizer!) : null;
        string[] mapInputs = maps is null ? [] : ["-loop", "1", "-i", Path.Combine(maps, "x.pgm"), "-loop", "1", "-i", Path.Combine(maps, "y.pgm")];
        string[] args = ["-v", "info", "-nostats", .. noStdin, "-y",
            "-threads", $"{_decodeThreads ?? DefaultDecodeThreads(pipeline, Environment.ProcessorCount)}", .. seek, .. _inputOptions, "-i", _path,
            .. mapInputs, .. sampled ? Array.Empty<string>() : ["-map", "0:v:0"], "-fps_mode", "passthrough", .. limit];
        select = ["-vf", (select.Length > 0 ? select[1] + "," : "") + "showinfo=checksum=0"];
        // Deinterlacing first, so the frames a seek decodes early still feed yadif, as in the copy it stands for.
        if (_deinterlace)
            select = ["-vf", "yadif" + (select.Length > 0 ? "," + select[1] : "")];
        string prefix = select.Length > 0 ? select[1] + "," : "";
        args = pipeline switch
        {
            // The source's own format, so ffmpeg passes the samples through untouched.
            FramePipeline.Yuv420Sampled => [.. args, .. select, "-f", "rawvideo", "-pix_fmt", _pixelFormat],
            // Like OpenCV: SWS_BICUBIC, colour matrix and range from the stream's tags (BT.601 if untagged).
            FramePipeline.FullFrameResize => [.. args, "-vf", prefix + "scale=flags=bicubic,format=bgr24" + crop,
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            FramePipeline.SampledBgr => [.. args, "-filter_complex",
                $"[0:v]{prefix}scale=flags=bicubic,format=bgr24[c];[c][1:v][2:v]remap=format=color[out]", "-map", "[out]",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            _ => [.. args, "-vf", prefix + "scale=flags=bicubic,format=bgr24" + crop + $",scale={Width}:{Height}:flags=bilinear",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(server is null ? "-" : $@"\\.\pipe\{pipeName}");

        using var proc = Start(psi);
        using var livePts = new BlockingCollection<long>();
        double liveTimeBase = _timeBase;
        if (!Streaming)
        {
            _livePackets.Clear();
            _liveStart = startFrame;
        }
        var log = new System.Text.StringBuilder();
        // stderr: the frames' timestamps come out of it, and anything else is kept for errors.
        var stderr = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) is not null)
                {
                    var frame = ShowInfoLine().Match(line);
                    if (frame.Success)
                    {
                        livePts.Add(long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture));
                        continue;
                    }
                    var timeBase = TimeBaseLine().Match(line);
                    if (timeBase.Success)
                    {
                        // The filter's time base can differ from the container's (mp4: 1/1000000).
                        liveTimeBase = long.Parse(timeBase.Groups[1].Value, CultureInfo.InvariantCulture) / (double)long.Parse(timeBase.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (Streaming)
                            _timeBase = liveTimeBase;
                        continue;
                    }
                    if (log.Length < 16_000 && !line.StartsWith("  ") && !line.StartsWith("Input #") && !line.StartsWith("Output #") && !line.StartsWith("Stream mapping"))
                        log.AppendLine(line);
                }
                livePts.CompleteAdding();
                return log.ToString();
            });
        // A Stream is fed to ffmpeg's stdin: the probed prefix first, then the rest.
        var feeder = _stream is null ? Task.CompletedTask : Task.Run(() =>
        {
            try
            {
                var stdin = proc.StandardInput.BaseStream;
                stdin.Write(_prefix);
                _stream.CopyTo(stdin);
                stdin.Close();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // ffmpeg stopped reading
        });
        Stream input = server ?? proc.StandardOutput.BaseStream;
        if (server is not null)
        {
            var connected = server.WaitForConnectionAsync(cancellationToken);
            try
            {
                if (Task.WaitAny([connected, proc.WaitForExitAsync(cancellationToken)], cancellationToken) != 0)
                    throw Failed(ShotDetectionError.DecodeFailed, $"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
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
            : sampled ? resizer!.SourceCols.Length * resizer.SourceRows.Length * 3
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
            var small = new byte[Width * Height * 3];
            byte[]? previous = null;
            foreach (var buffer in full.GetConsumingEnumerable(cancel.Token))
            {
                if (previous is not null)
                    free.Add(previous);
                // The frame's timestamp precedes it on stderr; without one the frame can't be placed.
                if (!livePts.TryTake(out long pts, Timeout.Infinite, cancel.Token))
                    throw Failed(ShotDetectionError.DecodeFailed, $"ffmpeg gave no timestamp for frame {_livePts.Count + _livePackets.Count}: {stderr.Result}");
                if (Streaming)
                    _livePts.Add(pts); // from the start already: ffmpeg shifts input timestamps to start at 0
                else
                    _livePackets.Add(PacketOf(pts * liveTimeBase, _livePackets.Count > 0 ? _livePackets[^1] : startFrame - 1));
                if (resizer is null)
                {
                    previous = buffer;
                    yield return buffer;
                    continue;
                }
                if (yuv)
                    resizer.ResizeYuv420(buffer, small, _yuv420ForVideo!, SourceWidth, SourceHeight, CropRegion.X, CropRegion.Y);
                else if (sampled)
                    resizer.ResizeSampled(buffer, small);
                else
                    resizer.Resize(buffer, small);
                free.Add(buffer);
                yield return small;
            }
            reader.Wait(); // rethrows read errors
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw Failed(ShotDetectionError.DecodeFailed, $"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
        }
        finally
        {
            // Caller stopped early (or we failed): stop the reader and ffmpeg before disposing.
            cancel.Cancel();
            Kill(proc);
            try { reader.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            try { feeder.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            if (maps is not null)
                try { Directory.Delete(maps, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The default decoder threads: one fewer than the CPUs available (Environment.ProcessorCount honours container
    /// limits), leaving one for the conversion and our scoring; at least 1, at most 8 on the yuv420p path (bound by
    /// decoding) and 4 elsewhere. With 2 CPUs, 1 thread gave the same wall time as 2-8 for ~9% less CPU (issue #12).
    /// In-process there's no ffmpeg process to leave room for: all the CPUs, as OpenCV uses (with 2 CPUs, 10-23% less
    /// wall time than 1 thread for 30-48% more CPU; the user chose speed, decisions.md).
    /// </summary>
    internal static int DefaultDecodeThreads(FramePipeline pipeline, int cpus, bool inProcess = false) =>
        Math.Clamp(inProcess ? cpus : cpus - 1, 1, pipeline == FramePipeline.Yuv420Sampled ? 8 : 4);

    /// <summary>remap's maps as 16-bit PGM files in a new temp folder: x.pgm (source column) and y.pgm (source row).</summary>
    string WriteMaps(CvResize resizer)
    {
        string dir = Directory.CreateTempSubdirectory("shotdetector-").FullName;
        int w = resizer.SourceCols.Length, h = resizer.SourceRows.Length;
        void Write(string name, Func<int, int, int> value)
        {
            using var f = File.Create(Path.Combine(dir, name));
            f.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n65535\n"));
            var row = new byte[w * 2];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(x * 2), (ushort)value(x, y));
                f.Write(row);
            }
        }
        Write("x.pgm", (x, _) => CropRegion.X + resizer.SourceCols[x]);
        Write("y.pgm", (_, y) => CropRegion.Y + resizer.SourceRows[y]);
        return dir;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\] n:\s*\d+ pts:\s*(-?\d+) ")]
    private static partial System.Text.RegularExpressions.Regex ShowInfoLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"config in time_base: (\d+)/(\d+)")]
    private static partial System.Text.RegularExpressions.Regex TimeBaseLine();

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
            throw new ShotDetectionException(ShotDetectionError.FfmpegNotFound,
                $"Could not start {psi.FileName}: {e.Message}. Install ffmpeg (which includes ffprobe) and put it on PATH, " +
                "or set DetectionOptions.FfmpegDirectory.", e);
        }
    }

    /// <summary>"ffmpeg" (found on PATH) or that executable in <paramref name="directory"/>.</summary>
    static string Executable(string? directory, string name) => directory is null
        ? name
        : Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    /// <summary>Runs ffmpeg/ffprobe to completion and returns its stdout; cancelling kills it.</summary>
    internal static string Run(string exe, IEnumerable<string> args, CancellationToken cancellationToken, byte[]? stdin = null,
        ShotDetectionError failure = ShotDetectionError.InvalidInput)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        using var proc = Start(psi);
        if (stdin is not null)
            _ = Task.Run(() =>
            {
                // The process may stop reading before the end (ffprobe needs only the headers).
                try { proc.StandardInput.BaseStream.Write(stdin); proc.StandardInput.Close(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        using var kill = cancellationToken.Register(() =>
        {
            Kill(proc);
        });
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new ShotDetectionException(failure, $"{exe} failed ({proc.ExitCode}): {stderr.Result}");
        return output;
    }
}

/// <summary>A temporary file, deleted on Dispose.</summary>
internal sealed class TempFile(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
