using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Numerics;
using FieldOrder = FrameReader.FieldOrder;
using StreamInfo = FrameReader.StreamInfo;
using StreamType = FrameReader.StreamType;

namespace ShotDetector;

/// <summary>How <see cref="VideoReader"/> gets downscaled BGR frames out of ffmpeg.</summary>
internal enum FramePipeline
{
    /// <summary>ffmpeg downscales (bilinear): fastest, but not what PySceneDetect computes.</summary>
    FfmpegScale,

    /// <summary>ffmpeg sends full-size BGR frames and <c>cv2.resize</c>'s port downscales them exactly.</summary>
    FullFrameResize,

    /// <summary>ffmpeg converts whole frames to BGR as above but sends only the pixels the exact resize reads (its remap filter): same result, a fraction of the bytes.</summary>
    SampledBgr,

    /// <summary>ffmpeg sends raw yuv420p; only the pixels the resize reads are converted (needs an <see cref="IYuv420Converter"/>).</summary>
    Yuv420Sampled,

    /// <summary>As <see cref="Yuv420Sampled"/> for yuv422p (needs an <see cref="IYuv420Converter"/> that is an <see cref="IYuv422Converter"/> too).</summary>
    Yuv422Sampled,

    /// <summary>As <see cref="Yuv420Sampled"/> for yuv420p10le (needs an <see cref="IYuv420Converter"/> that is an <see cref="IYuv420P10Converter"/> too).</summary>
    Yuv420P10Sampled,
}

/// <summary>Frame rate as an exact fraction (e.g. 30000/1001).</summary>
public readonly record struct Fps(int Num, int Den)
{
    /// <summary>The frame rate as a double, i.e. Python's float(frame_rate).</summary>
    public double Value => (double)Num / Den;

    /// <summary>The same fraction as FrameReader's <see cref="FrameReader.Rational"/> (e.g. a stream's rate from MediaProbe).</summary>
    public static implicit operator Fps(FrameReader.Rational rate) => new(rate.Num, rate.Den);

    /// <summary>The same fraction as FrameReader's <see cref="FrameReader.Rational"/>.</summary>
    public static implicit operator FrameReader.Rational(Fps fps) => new(fps.Num, fps.Den);

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
        if (exponent > 0)
            n <<= exponent;
        else
            d <<= -exponent;
        var g = BigInteger.GreatestCommonDivisor(n, d);
        (n, d) = (n / g, d / g);
        if (d <= maxDen)
            return ((long)n, (long)d);

        BigInteger num0 = n, den0 = d, p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        while (true)
        {
            var a = BigInteger.Divide(n, d);
            var q2 = q0 + a * q1;
            if (q2 > maxDen)
                break;
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
/// The video a detection read (<see cref="DetectionResult.Video"/>): its source and analysed sizes, frame rate, frame
/// positions and how it was decoded. Frames come from FFmpeg's libraries in this process or from the ffmpeg
/// executable (<see cref="VideoDecoder"/>), downscaled as PySceneDetect does by default (factor = max(width, height) /
/// 256 when that side is at least 256) with cv2.resize's exact arithmetic; with <see cref="DetectionOptions.FfmpegResize"/>,
/// swscale's bilinear scale instead: faster, but it averages over the whole footprint, so content_val is lower on fine
/// detail and cuts can differ from PySceneDetect's.
/// </summary>
internal sealed partial class VideoReader
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
        Width = SourceWidth,
        Height = SourceHeight,
        FrameRate = Fps,
        OpenCvFrameCount = OpenCvFrameCount > 0 ? OpenCvFrameCount : null,
        Duration = Duration,
        Codec = Codec,
        PixelFormat = _pixelFormat,
        FieldOrder = FieldOrder,
        Rotation = _rotation,
        Container = Container,
        HasAudio = HasAudio,
        Streams = Streams,
    };

    /// <summary>FFmpeg's name for the container (its demuxer).</summary>
    public string Container { get; }

    /// <summary>The decoded pixel format's FFmpeg name; empty when unknown.</summary>
    internal string PixelFormat => _pixelFormat;

    /// <summary>The display rotation in degrees, counterclockwise (0 when none).</summary>
    internal int Rotation => _rotation;

    /// <summary>Whether the file has an audio stream (<see cref="Streams"/>); null for a <see cref="Stream"/>.</summary>
    public bool? HasAudio => Streams?.Any(s => s.Type == StreamType.Audio);

    /// <summary>
    /// Every stream of the file or URL; null for a <see cref="Stream"/> (it can't be read again). Looked up on first use
    /// (in-process when the libraries load, else one ffprobe call: for a URL, another request), as detection doesn't
    /// need it.
    /// </summary>
    public IReadOnlyList<StreamInfo>? Streams => _stream is not null
        ? null
        : _streams ??= _probesInProcess ? InProcessProbe.Streams(_path, _ffmpegDirectory, _inputOptions) : FfprobeStreams();

    IReadOnlyList<StreamInfo>? _streams;

    /// <summary>The ffprobe entries <see cref="Streams"/> reads.</summary>
    internal const string StreamEntries = "stream=index,codec_type,codec_name,codec_tag_string,width,height,sample_rate,channels,channel_layout,"
        + "sample_fmt,bit_rate,duration_ts,time_base:stream_disposition=default,forced:stream_tags=language,title";

    IReadOnlyList<StreamInfo> FfprobeStreams()
    {
        string json = Run(_ffprobe, ["-v", "error", "-show_entries", StreamEntries, "-of", "json", .. _inputOptions, "-i", _path], CancellationToken.None);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<StreamInfo>();
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
            return result;
        foreach (var s in streams.EnumerateArray())
        {
            string? Text(System.Text.Json.JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : null;
            long? Long(string key) => long.TryParse(Text(s, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
            int? Int(string key) => int.TryParse(Text(s, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
            string type = Text(s, "codec_type") ?? "";
            var tags = s.TryGetProperty("tags", out var t) ? t : default;
            var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
            bool Flag(string key) => disposition.ValueKind == System.Text.Json.JsonValueKind.Object && Text(disposition, key) == "1";
            result.Add(new StreamInfo
            {
                Index = Int("index") ?? result.Count,
                Type = type switch
                {
                    "video" => StreamType.Video,
                    "audio" => StreamType.Audio,
                    "subtitle" => StreamType.Subtitle,
                    "data" => StreamType.Data,
                    "attachment" => StreamType.Attachment,
                    _ => StreamType.Unknown,
                },
                Codec = Text(s, "codec_name") ?? "unknown",
                CodecTag = Text(s, "codec_tag_string") ?? "",
                Language = tags.ValueKind == System.Text.Json.JsonValueKind.Object ? Text(tags, "language") : null,
                Title = tags.ValueKind == System.Text.Json.JsonValueKind.Object ? Text(tags, "title") : null,
                IsDefault = Flag("default"),
                IsForced = Flag("forced"),
                BitRate = Long("bit_rate"),
                DurationPts = Long("duration_ts"),
                TimeBase = Text(s, "time_base") is { } tb && tb.Split('/') is [var tbNum, var tbDen]
                    && int.TryParse(tbNum, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tbn)
                    && int.TryParse(tbDen, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tbd) ? new(tbn, tbd) : default,
                Width = type == "video" ? Int("width") : null,
                Height = type == "video" ? Int("height") : null,
                SampleRate = type == "audio" ? Int("sample_rate") : null,
                Channels = type == "audio" ? Int("channels") : null,
                ChannelLayout = type == "audio" && Text(s, "channel_layout") is { } l && l != "unknown" ? l : null,
                SampleFormat = type == "audio" ? Text(s, "sample_fmt") : null,
            });
        }

        return result;
    }

    /// <summary>ffprobe's printed seconds (six decimals) as a TimeSpan; null for N/A.</summary>
    internal static TimeSpan? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? TimeSpan.FromSeconds(s) : null;

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
    readonly int _frameBuffers;
    readonly string[] _inputOptions;   // ffmpeg/ffprobe options for the input (an image sequence's rate)
    readonly bool _fullFrames;         // tests: the full-frame pipeline instead of the sampled one
    readonly bool _alwaysBt601;        // compare.py on Windows: BT.601 whatever the tags, as the Windows OpenCV wheel
    readonly VideoDecoder _decoder;
    readonly string? _ffmpegDirectory;
    readonly int _rotation;            // the display rotation ffmpeg applies (degrees); 0 if none
    readonly bool _probesInProcess;    // read with FFmpeg's libraries rather than ffprobe
    readonly bool _urlInProcess;       // a URL whose protocol the libraries have: probed and decoded in-process

    /// <summary>The stream entries read from ffprobe (or the same in-process, <see cref="InProcessProbe"/>); files add :packet=pts.</summary>
    internal const string ProbeEntries = "stream=codec_name,field_order,width,height,pix_fmt,color_space,color_range,chroma_location,r_frame_rate,avg_frame_rate,time_base,start_pts,start_time,nb_frames,duration:stream_side_data=rotation:format=duration,format_name";
    readonly IYuv420Converter? _yuv420;
    readonly FrameReader.IRowDeinterlacer? _yadif;
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
    readonly (int W, int H) _codedSize; // the stream's size from its headers, before rotation
    readonly string? _chromaLocation;
    readonly bool? _xvidIdct;          // MPEG-4 Part 2: whether FFmpeg's decoder takes the Xvid IDCT (Mpeg4Idct); null if not known
    readonly YuvConversion? _yuvForVideo;         // _yuv420 configured for this video's layout and colours (4:2:0 or 4:2:2)

    /// <param name="path">Video file, or a URL (http(s), rtsp, ... ; read as a stream).</param>
    /// <param name="options">Uses FfmpegResize, DecodeThreads, Yuv420Converter, Compatibility, Crop, FfmpegDirectory, Streaming and ProbeBytes.</param>
    /// <param name="cancellationToken">Cancels the ffprobe run (the process is killed).</param>
    internal VideoReader(string path, DetectionOptions? options = null, CancellationToken cancellationToken = default)
        : this(path, null, options, cancellationToken)
    {
    }

    /// <summary>Probes only the headers (no packet timestamps): for <see cref="ShotDetection.Probe(string, DetectionOptions?, CancellationToken)"/>.</summary>
    internal static VideoReader Headers(string path, DetectionOptions? options, CancellationToken cancellationToken) =>
        new(path, null, options, cancellationToken, headersOnly: true);

    /// <param name="video">The video's bytes, read once, so the container headers must come first:
    /// mkv, webm, ts, mov, or an mp4 written with "faststart" (an ordinary mp4 keeps them at the end).</param>
    /// <param name="options">See <see cref="VideoReader(string, DetectionOptions?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels the ffprobe run (the process is killed).</param>
    internal VideoReader(Stream video, DetectionOptions? options = null, CancellationToken cancellationToken = default)
        : this("pipe:0", video, options, cancellationToken)
    {
    }

    /// <summary>A Stream whose first bytes (<paramref name="prefix"/>) were already read from it.</summary>
    internal VideoReader(Stream video, byte[] prefix, DetectionOptions? options, CancellationToken cancellationToken)
        : this("pipe:0", video, options, cancellationToken, prefix: prefix)
    {
    }

    VideoReader(string path,
        Stream? video,
        DetectionOptions? options,
        CancellationToken cancellationToken,
        bool headersOnly = false,
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
        _frameBuffers = o.FrameBuffers;
        _fullFrames = o.FullFrames;
        _alwaysBt601 = o.AlwaysBt601;
        _decoder = o.Decoder;
        _ffmpegDirectory = o.FfmpegDirectory;
        FfmpegExe = Executable(o.FfmpegDirectory, "ffmpeg");
        Streaming = video is not null || o.Streaming || IsUrl(path);

        // scenedetect's frame rate override; an image sequence (no timestamps of its own) is read at that rate.
        Fps? frameRate = o.FrameRate is not { } fr ? null
            : fr <= 0 ? throw new ArgumentException("FrameRate must be positive.")
            : Fps.FromFloat(fr);
        _inputOptions = video is not null ? StreamInputOptions
            : frameRate is { } r && path.Contains('%') && !IsUrl(path) ? ["-framerate", $"{r.Num}/{r.Den}", .. InputOptions(path)]
            : InputOptions(path);

        // One demux-only pass: stream properties, plus every packet's pts for a file (no decoding).
        // A Stream is probed from its first bytes, which are kept to feed ffmpeg later.
        string entries = ProbeEntries + (Streaming || headersOnly ? "" : ":packet=pts");
        string ffprobe = _ffprobe = Executable(o.FfmpegDirectory, "ffprobe");
        string[] probeArgs = ["-v", "error", .. _inputOptions, "-select_streams", "v:0", "-show_entries", entries, "-of", "default=nw=1", "-i", path];
        string probed;

        // A file, with FFmpeg's libraries available (as for decoding in-process): read the same with them, no ffprobe.
        // A Stream too: its first bytes, as ffprobe reads them from stdin.
        // A URL too, when the libraries have its protocol (FrameReader.Native: http and https), streamed
        // as a Stream is.
        bool libraries = _decoder != VideoDecoder.FfmpegProcess && (_decoder == VideoDecoder.InProcess || InProcess.CanLoad(_ffmpegDirectory));
        _urlInProcess = libraries && video is null && IsUrl(path) && InProcess.HasProtocol(new Uri(path).Scheme, _ffmpegDirectory);
        _probesInProcess = (video is not null || !Streaming || _urlInProcess) && libraries;

        // Auto: a URL the libraries can't open (a format they lack, as DASH without libxml2) goes to the executable.
        string? urlProbe = null;
        if (_urlInProcess && _decoder == VideoDecoder.Auto)
        {
            try
            {
                urlProbe = InProcessProbe.Properties(path, _ffmpegDirectory, packets: false, _inputOptions, cancellationToken: cancellationToken);
            }
            catch (ShotDetectionException)
            {
                _urlInProcess = false;
                _probesInProcess = false;
            }
        }

        if (urlProbe is not null)
        {
            probed = urlProbe;
        }
        else if (_probesInProcess && video is not null)
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
        {
            probed = InProcessProbe.Properties(path, _ffmpegDirectory, packets: !headersOnly && !Streaming, _inputOptions, cancellationToken: cancellationToken);
        }
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
        {
            probed = Run(ffprobe, probeArgs, cancellationToken);
        }

        var stream = new Dictionary<string, string>();
        var pts = new List<long>();
        bool missingPts = false;
        foreach (var line in probed.Split('\n', StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line[..eq], value = line[(eq + 1)..];
            if (key == "pts")
            {
                if (long.TryParse(value, CultureInfo.InvariantCulture, out long p))
                    pts.Add(p);
                else
                    missingPts = true;
            }
            else
            {
                stream[key] = value;
            }
        }

        if (!stream.ContainsKey("width"))
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"The input has no video stream: {path}");
        SourceWidth = int.Parse(stream["width"], CultureInfo.InvariantCulture);
        SourceHeight = int.Parse(stream["height"], CultureInfo.InvariantCulture);
        if ((long)SourceWidth * SourceHeight > FrameReader.FrameDecoderOptions.DefaultMaxPixels)
        {
            throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                $"The video's frames ({SourceWidth}x{SourceHeight}) are larger than 8192 x 8192 pixels: {path}");
        }

        _codedSize = (SourceWidth, SourceHeight);

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
            ? d <= 1
                ? (CropRegion.Width, CropRegion.Height)
                : (Math.Max(1, (int)Math.Round(CropRegion.Width / (double)d)), Math.Max(1, (int)Math.Round(CropRegion.Height / (double)d)))
            : DownscaledSize(effective.SourceWidth, effective.SourceHeight, CropRegion.Width, CropRegion.Height);
        _pixelFormat = stream.GetValueOrDefault("pix_fmt", "");

        // The first frame's chroma location and colour tags over the stream's: OpenCV converts each frame with its own
        // (sws_scale_frame), which are the codec's (an h264 stream's VUI) where a container's header can say otherwise.
        // Read only where they're used: by FastYuv, and by the executable's scale filter where swscale interpolates the
        // chroma (not 8-bit 4:2:0; the in-process default path converts with each frame's own).
        if (!headersOnly && (_yuv420 is not null || _pixelFormat is not ("yuv420p" or "yuvj420p")))
            PreferFrameTags(stream, FrameTags(path, ffprobe, cancellationToken));

        // Where the chroma samples sit, which OpenCV's converter (sws_scale_frame) takes from the frame; ffmpeg's command
        // line loses it on the way to its filters, so the scale filter is told (scenedetect's 10-bit frames, issue-free
        // for 8-bit 4:2:0, whose unscaled conversion doesn't interpolate chroma).
        _chromaLocation = stream.GetValueOrDefault("chroma_location") is { } loc and not ("unspecified" or "unknown" or "") ? loc : null;
        FieldOrder = stream.GetValueOrDefault("field_order") switch
        {
            "progressive" => FieldOrder.Progressive,
            "tt" => FieldOrder.TopFirst,
            "bb" => FieldOrder.BottomFirst,
            "tb" => FieldOrder.TopCodedBottomFirst,
            "bt" => FieldOrder.BottomCodedTopFirst,
            _ => FieldOrder.Unknown,
        };
        Codec = stream.GetValueOrDefault("codec_name", "");
        Container = stream.GetValueOrDefault("format_name", "");

        // MPEG-4 Part 2: whether FFmpeg's decoder takes the Xvid IDCT, from the codec tag, the extradata and the first
        // packet (one more short probe; for a URL a second connection, so a URL decodes as its file does on ARM too).
        if (Codec == "mpeg4" && !headersOnly)
            _xvidIdct = XvidIdct(path, ffprobe, cancellationToken);
        Duration = double.TryParse(stream.GetValueOrDefault("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double length)
            ? TimeSpan.FromSeconds(length)
            : null;
        _deinterlace = o.Deinterlace == DeinterlaceMode.On
            || (o.Deinterlace == DeinterlaceMode.Auto && FieldOrder is not (FieldOrder.Progressive or FieldOrder.Unknown));

        // Like OpenCV with FFmpeg 8 (and ffmpeg's own scale filter), conversion follows the colour tags.
        bool fullRange = _pixelFormat is "yuvj420p" or "yuvj422p" || stream.GetValueOrDefault("color_range") == "pc";
        string colorSpace = _alwaysBt601 ? "bt470bg" : stream.GetValueOrDefault("color_space", "");

        // 4:2:2 when the converter does that too (FastYuv's does): swscale takes the same unscaled converter for it. 10-bit
        // 4:2:0 too: swscale scales that one (interpolating the chroma from where it sits), so its converter is for the
        // size and chroma location as well, and declines sizes it doesn't reproduce.
        _yuvForVideo = _pixelFormat switch
        {
            "yuv420p" or "yuvj420p" => _yuv420?.ForColor(colorSpace, fullRange) is { } c420 ? new(c420.RowToBgr) : null,
            "yuv422p" or "yuvj422p" => (_yuv420 as IYuv422Converter)?.ForColor(colorSpace, fullRange) is { } c422 ? new(c422.RowToBgr) : null,

            // A frame at a time when the converter does that too (FastYuv's does): each chroma row filtered once per frame.
            "yuv420p10le" => (_yuv420 as IYuv420P10Converter)?.ForVideo(colorSpace, fullRange, _chromaLocation, _codedSize.W, _codedSize.H) is { } c10
                ? new(c10.RowToBgr, c10 as IYuv420P10FrameConverter)
                : null,
            _ => null,
        };

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
            pts = _probesInProcess ? InProcessProbe.FrameTimestamps(path, _ffmpegDirectory, _inputOptions) : FrameTimestamps(ffprobe, path, _inputOptions, cancellationToken);
        _pts = [.. pts];
    }

    static List<long> FrameTimestamps(string ffprobe, string path, string[] inputOptions, CancellationToken ct)
    {
        var result = new List<long>();
        long? framePts = null;
        foreach (var line in Run(ffprobe, FrameTimestampsArgs(path, inputOptions), ct).Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("pts="))
            {
                framePts = long.TryParse(line[4..], CultureInfo.InvariantCulture, out long p) && p != 0 ? p : null;
            }
            else if (line.StartsWith("pkt_dts="))
            {
                result.Add(framePts ?? (long.TryParse(line[8..], CultureInfo.InvariantCulture, out long d) ? d : 0));
                framePts = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the first frame a seek for <paramref name="startFrame"/> gave (its absolute time) is more than half a
    /// frame after that frame's: the frames between were lost.
    /// </summary>
    internal bool SeekLandedLate(double seconds, int startFrame) => seconds > _pts[startFrame] * _timeBase + 0.5 / Fps.Value;

    /// <summary>How many frames early a seek goes after one that landed late <paramref name="seekBack"/> frames early.</summary>
    internal static int SeekBackAgain(int seekBack) => seekBack * 16;

    /// <summary>
    /// The packet a decoded frame is, from its absolute showinfo time: the first packet after
    /// <paramref name="previous"/> within half a frame of it; if none is (a time the packet list doesn't
    /// have), simply the next one. A time before the previous packet's (timestamps that start over, as in two
    /// streams joined) is looked up from the start: scenedetect labels frames by their time, so its numbers go
    /// back there too.
    /// </summary>
    int PacketOf(double seconds, int previous)
    {
        double tolerance = 0.5 / Fps.Value;
        if (previous >= 0 && previous < _pts.Length && seconds < _pts[previous] * _timeBase - tolerance)
            previous = -1;
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
        message.Contains("moov atom not found") || (message.Contains("mp4,m4a,3gp,3g2,mj2 @") && message.Contains("partial file"))
            ? new ShotDetectionException(ShotDetectionError.InvalidInput,
                "The stream's container headers aren't at the start (an mp4 without \"faststart\"), so it can't be "
                + "read as a stream. Use mkv/webm/ts/mov, an mp4 written with -movflags +faststart, or a file path.",
                inner)
            : new ShotDetectionException(reason, message, inner);

    /// <summary>
    /// MPEG-1/2, MJPEG and MPEG-4 Part 2 with FFmpeg's C IDCT (in-process too, FrameDecoderOptions.CodecOptions): x86's
    /// default, its SIMD IDCT, gives the same frames, ARM's NEON one rounds differently, so every platform decodes these as
    /// x86 (and OpenCV there) does. Not MPEG-4 that FFmpeg decodes with the Xvid IDCT (<paramref name="xvidIdct"/>, see
    /// <see cref="Mpeg4Idct"/>), the same on every platform and not the C one; nor MPEG-4 not known to be either (null).
    /// </summary>
    internal static string[] IdctOptions(string codec, bool? xvidIdct = null) =>
        codec is "mpeg1video" or "mpeg2video" or "mjpeg" || (codec == "mpeg4" && xvidIdct == false) ? ["-idct", "simple"] : [];

    /// <summary>MPEG-4 Part 2: whether FFmpeg's decoder takes the Xvid IDCT for this input; null for other codecs or when not known.</summary>
    internal bool? UsesXvidIdct => _xvidIdct;

    /// <summary>
    /// <see cref="Mpeg4Idct.UsesXvidIdct"/> for this input: the codec tag, extradata and first video packet from FFmpeg's
    /// libraries when they probed it, else from ffprobe (<see cref="XvidIdctArgs"/>); a Stream's from its first bytes.
    /// Null when they can't be read (the decoder's default IDCT then).
    /// </summary>
    bool? XvidIdct(string path, string ffprobe, CancellationToken cancellationToken)
    {
        byte[]? prefix = _stream is null ? null : _prefix;
        try
        {
            return _probesInProcess
                ? InProcessProbe.XvidIdct(path, _ffmpegDirectory, _inputOptions, prefix, cancellationToken)
                : XvidIdct(Run(ffprobe, XvidIdctArgs(path, _inputOptions), cancellationToken, stdin: prefix));
        }
        catch (Exception e) when (e is ShotDetectionException or FrameReader.FrameReaderException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first decoded frame's chroma location and colour tags (<see cref="FrameTagsArgs"/>' output): from FFmpeg's
    /// libraries when they probed the input, else from ffprobe; a Stream's from its first bytes. Null when no frame
    /// decodes (the stream's tags then).
    /// </summary>
    string? FrameTags(string path, string ffprobe, CancellationToken cancellationToken)
    {
        byte[]? prefix = _stream is null ? null : _prefix;
        try
        {
            return _probesInProcess
                ? InProcessProbe.FrameTags(path, _ffmpegDirectory, _inputOptions, prefix, cancellationToken)
                : Run(ffprobe, FrameTagsArgs(path, _inputOptions), cancellationToken, stdin: prefix);
        }
        catch (Exception e) when (e is ShotDetectionException or FrameReader.FrameReaderException)
        {
            return null;
        }
    }

    /// <summary>ffprobe's arguments for the first video frame's chroma location and colour tags (it decodes that frame only).</summary>
    internal static string[] FrameTagsArgs(string path, string[] inputOptions) =>
        ["-v", "error", .. inputOptions, "-select_streams", "v:0", "-read_intervals", "%+#1",
            "-show_entries", "frame=chroma_location,color_space,color_range", "-of", "default=nw=1", "-i", path];

    /// <summary>
    /// The frame's chroma location, colour space and range (<paramref name="frameTags"/>, ffprobe's frame entries) in
    /// <paramref name="stream"/>'s place, each where the frame has one: the decoder's values, which a container's header
    /// can contradict.
    /// </summary>
    internal static void PreferFrameTags(Dictionary<string, string> stream, string? frameTags)
    {
        foreach (var line in (frameTags ?? "").Split('\n', StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line[..eq], value = line[(eq + 1)..];
            if (key is "chroma_location" or "color_space" or "color_range" && value is not ("unspecified" or "unknown" or ""))
                stream[key] = value;
        }
    }

    /// <summary>ffprobe's arguments for the codec tag, extradata and first packet of the first video stream (hex dumps).</summary>
    internal static string[] XvidIdctArgs(string path, string[] inputOptions) =>
        ["-v", "error", .. inputOptions, "-select_streams", "v:0", "-show_entries", "stream=codec_tag_string,extradata:packet=data",
            "-show_data", "-read_intervals", "%+#1", "-of", "default=nw=1", "-i", path];

    /// <summary><see cref="Mpeg4Idct.UsesXvidIdct"/> from <see cref="XvidIdctArgs"/>' output; null without a packet.</summary>
    internal static bool? XvidIdct(string probed)
    {
        string tag = "";
        List<byte> extradata = [];
        List<byte>? packet = null, into = null;
        foreach (var raw in probed.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            // A hex dump line: "00000010: 0120 00bc 0406 ...  . ......", the bytes in the 39 characters after the offset.
            if (into is not null && line.Length > 10 && line[8] == ':' && line[9] == ' ')
            {
                var hex = line.AsSpan(10, Math.Min(39, line.Length - 10));
                for (int i = 0; i + 1 < hex.Length; i++)
                {
                    if (hex[i] == ' ')
                        continue;
                    into.Add(byte.Parse(hex.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i++;
                }

                continue;
            }

            into = null;
            if (line.StartsWith("codec_tag_string=", StringComparison.Ordinal))
                tag = line["codec_tag_string=".Length..];
            else if (line == "extradata=")
                into = extradata;
            else if (line == "data=" && packet is null)
                into = packet = [];
        }

        return packet is null ? null : Mpeg4Idct.UsesXvidIdct(tag, [.. extradata], [.. packet]);
    }

    /// <summary>
    /// ffmpeg's decoder option against crafted files claiming huge frames (FrameReader's default, which in-process decoding
    /// gets): a frame of more than 8192 x 8192 pixels fails to decode.
    /// </summary>
    internal static readonly string[] MaxPixels = ["-max_pixels", FrameReader.FrameDecoderOptions.DefaultMaxPixels.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// A Stream's input (pipe:0, or FrameReader's own I/O in-process): nothing it names may open another protocol (a
    /// playlist piped in would otherwise reach files or the network), as a file reads only as a file.
    /// </summary>
    internal static readonly string[] StreamInputOptions = ["-protocol_whitelist", "pipe"];

    static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && uri.Scheme != Uri.UriSchemeFile;

    /// <summary>
    /// <see cref="DetectionResult.Warnings"/>' line for an http or https HLS playlist decoded by the ffmpeg executable,
    /// else null: the executable checks an https playlist's own certificate, but opens its segments, keys and variants
    /// without the check (FFmpeg's HLS demuxer doesn't pass tls_verify on, and an http playlist's input has none to
    /// pass), so any of them on https goes unchecked; in-process, FrameReader checks them too. The playlist isn't read
    /// to see whether it names any. A local playlist can't reach https (its whitelist is file,crypto).
    /// </summary>
    internal static string? ExecutableHlsWarning(string? path, string container, bool decodedInProcess) =>
        !decodedInProcess && container == "hls" && Uri.TryCreate(path, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? "decoded by ffmpeg, which fetches the playlist's https segments, keys and variants without checking their "
            + "certificates; in-process decoding (the FrameReader.Native packages) checks them."
            : null;

    /// <summary>
    /// FFmpeg options for an input path or URL: only the protocols it needs. A file (a plain path, or an FFmpeg
    /// pseudo-protocol string such as concat: or subfile:, which then fails) reads as a file; an http(s) or rtsp URL its
    /// own family, a second layer over FFmpeg's checks of what it serves (an HLS playlist's entries); another network
    /// URL (scheme://host: srt, udp, rtmp...) none, as before.
    /// </summary>
    internal static string[] InputOptions(string path)
    {
        string[] files = ["-protocol_whitelist", "file,crypto"];
        if (!IsUrl(path))
            return files;
        string scheme = new Uri(path).Scheme.ToLowerInvariant();
        return scheme switch
        {
            // A read that waits more than 30 s fails rather than hanging (rw_timeout, in microseconds); https checks the
            // server's certificate (tls_verify, which FFmpeg 8 leaves off; FrameReader turns it on in-process too). Not
            // for http: nothing there takes tls_verify, and ffmpeg refuses an option nothing took.
            "http" => ["-protocol_whitelist", "http,https,tcp,tls,crypto,httpproxy", "-rw_timeout", "30000000"],
            "https" => ["-protocol_whitelist", "http,https,tcp,tls,crypto,httpproxy", "-rw_timeout", "30000000", "-tls_verify", "1"],
            "rtsp" or "rtsps" => ["-protocol_whitelist", "rtsp,rtsps,rtp,srtp,udp,tcp,tls"],
            _ when path.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase) => [],
            _ => files,
        };
    }

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
            {
                return false; // not an ISO media file
            }

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
    /// ffprobe's decoding pass for per-frame timestamps: it decodes, so it gets the input's options (protocols, timeout,
    /// certificate check) and the frame size cap like every other read.
    /// </summary>
    internal static string[] FrameTimestampsArgs(string path, string[] inputOptions) =>
        ["-v", "error", .. inputOptions, .. MaxPixels, "-select_streams", "v:0", "-show_entries", "frame=pts,pkt_dts", "-of", "default=nw=1", "-i", path];

    /// <summary>
    /// The whole stream (<paramref name="prefix"/>, already read, then the rest) in a temporary file, deleted when the
    /// result is disposed: for an mp4 without "faststart", whose headers come last.
    /// </summary>
    internal static TempFile Spill(byte[] prefix, Stream video, CancellationToken cancellationToken)
    {
        var file = new TempFile("spill.mp4");
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
            if (n == 0)
                break;
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
            if (Position(mid).Seconds >= target)
                hi = mid;
            else
                lo = mid + 1;
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

    /// <summary>
    /// The clockwise quarter turn OpenCV gives the frames, in degrees (0, 90, 180, 270): the display matrix's angle alone
    /// (-round(av_display_rotation_get), so flips are ignored), applied to the converted image (cv::rotate). ffmpeg's
    /// autorotate rotates the planes before converting and applies flips, which differs where conversion interpolates
    /// chroma (4:2:2 at 90 degrees, 10-bit): never used.
    /// </summary>
    internal int Turn => ((-_rotation) % 360 + 360) % 360;

    /// <summary>
    /// ffmpeg's filters for <see cref="Turn"/>, on bgr24: a pure rearrangement, as cv::rotate. The YUV pipelines send the
    /// planes as decoded and turn after converting
    /// (<see cref="CvResize.ResizeYuv(byte[], byte[], YuvConversion, int, int, int, int, int)"/>), as turning the planes
    /// first would differ where the chroma layout doesn't survive the turn (4:2:2 at 90 and 270, odd sizes).
    /// </summary>
    string TurnFilters => Turn switch { 90 => ",transpose=clock", 180 => ",hflip,vflip", 270 => ",transpose=cclock", _ => "" };

    /// <summary>How frames get from ffmpeg to the downscaled BGR the detectors see.</summary>
    public FramePipeline Pipeline => !_ffmpegResize && (Width, Height) != (CropRegion.Width, CropRegion.Height)

        // swscale's unscaled yuv420p/yuv422p converter (what OpenCV gets) needs an even decoded height (10-bit: any).
        ? (_yuvForVideo is not null && !_sizeChanges && (TenBit || _codedSize.H % 2 == 0) ? YuvPipeline
            : _fullFrames || (!DecodesInProcess && !SampledPays(CropRegion.Width, CropRegion.Height, Width, Height))
                ? FramePipeline.FullFrameResize : FramePipeline.SampledBgr)
        : FramePipeline.FfmpegScale;

    /// <summary>Whether the video is 4:2:2 (yuv422p or yuvj422p): a chroma row for every row.</summary>
    bool Yuv422 => _pixelFormat is "yuv422p" or "yuvj422p";

    /// <summary>Whether the video is 10-bit 4:2:0 (yuv420p10le): two bytes a sample.</summary>
    bool TenBit => _pixelFormat == "yuv420p10le";

    /// <summary>The YUV pipeline for the video's layout.</summary>
    FramePipeline YuvPipeline => TenBit ? FramePipeline.Yuv420P10Sampled : Yuv422 ? FramePipeline.Yuv422Sampled : FramePipeline.Yuv420Sampled;

    /// <summary>Whether a pipeline is one of the YUV ones (raw planes from the decoder, converted where the resize reads).</summary>
    internal static bool IsYuv(FramePipeline pipeline) =>
        pipeline is FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled or FramePipeline.Yuv420P10Sampled;

    /// <summary>Size in bytes of one of the YUV pipelines' frames: tightly packed planes, at the decoded size.</summary>
    int YuvFrameSize => TenBit ? IYuv420P10Converter.FrameSize(_codedSize.W, _codedSize.H)
        : Yuv422 ? IYuv422Converter.FrameSize(_codedSize.W, _codedSize.H)
        : IYuv420Converter.FrameSize(_codedSize.W, _codedSize.H);

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
    /// and converts only the pixels cv2.resize reads (yuv422p the same way, with a converter for it); "bgr24+resize" has
    /// ffmpeg convert whole frames; "ffmpeg-scale" has ffmpeg downscale (--ffmpeg-resize, or when no resize is needed).
    /// </summary>
    internal IEnumerable<byte[]> Frames(CancellationToken cancellationToken = default) => Frames(0, null, cancellationToken);

    // The executable's YUV pipeline met a frame of another size or pixel format than the stream's: ffmpeg scales or
    // converts such frames in YUV (to the stream's size and -pix_fmt) before the converter sees them, where OpenCV does
    // it inside its one conversion to BGR. From then on this video takes the BGR pipelines, which convert them as OpenCV
    // does.
    bool _sizeChanges;

    /// <summary>
    /// Yields frames <paramref name="startFrame"/> .. <paramref name="startFrame"/> + <paramref name="count"/> - 1
    /// (count null: to the end), downscaled; see <see cref="Frames(CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// A seek goes <paramref name="seekBack"/> frames early. Where it lands after the wanted frame anyway (MPEG-TS has no
    /// index: libavformat's seek finds a packet by time, and decoding starts at the next keyframe after it), frames would
    /// be lost, so the read starts over from <see cref="SeekBackAgain"/> frames early, up to from the start.
    /// </remarks>
    internal IEnumerable<byte[]> Frames(int startFrame, int? count, CancellationToken cancellationToken = default, int seekBack = 2)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count is <= 0)
            yield break;
        if (Streaming && startFrame > 0)
            throw new NotSupportedException("A streamed input can't be seeked.");
        if (DecodesInProcess)
        {
            bool any = false;
            foreach (var frame in FramesInProcess(startFrame, count, cancellationToken, seekBack))
            {
                any = true;
                yield return frame;
            }

            // Auto: libraries that can't decode this codec (AV1 needs dav1d, which FrameReader.Native leaves out)
            // give no frames at all; the ffmpeg executable decodes it instead, from here on.
            // A Stream can't be read twice: what it gave is what there is.
            if (any || _decoder != VideoDecoder.Auto || ExpectedFrames <= startFrame || _stream is not null)
                yield break;
            _inProcessGaveNothing = true;
        }

        var pipeline = Pipeline;
        bool resizeHere = pipeline != FramePipeline.FfmpegScale, yuv = IsYuv(pipeline);

        // On Windows, stdout redirection uses a 4 KB pipe, which makes full-size frames crawl
        // (19 s vs 12 s for 5000 1080p frames), so ffmpeg writes to a named pipe with a big buffer.
        string pipeName = $"shotdetector_{Guid.NewGuid():N}";
        using var server = OperatingSystem.IsWindows()
            ? new NamedPipeServerStream(pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, // only this user (ffmpeg, started by us) can connect
                inBufferSize: 8 << 20,
                outBufferSize: 0)
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
            int early = Math.Max(startFrame - seekBack, 0);
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
            ? ""
            : $",crop={CropRegion.Width}:{CropRegion.Height}:{CropRegion.X}:{CropRegion.Y}";

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

        // Rotated as OpenCV does (Turn), not by ffmpeg's autorotate.
        string[] args = ["-v", "info", "-nostats", .. noStdin, "-y",
            "-threads", $"{_decodeThreads ?? DefaultDecodeThreads(Environment.ProcessorCount)}", .. seek, .. _inputOptions, .. IdctOptions(Codec, _xvidIdct), .. MaxPixels, "-noautorotate", "-i", _path,
            .. mapInputs, .. sampled ? Array.Empty<string>() : ["-map", "0:v:0"], "-fps_mode", "passthrough", .. limit];
        select = ["-vf", (select.Length > 0 ? select[1] + "," : "") + "showinfo=checksum=0"];

        // Deinterlacing first, so the frames a seek decodes early still feed yadif, as in the copy it stands for.
        if (_deinterlace)
            select = ["-vf", "yadif" + (select.Length > 0 ? "," + select[1] : "")];
        string prefix = select.Length > 0 ? select[1] + "," : "";

        // The stream's size, explicitly: a frame of another size (a resolution change) is scaled to it in the same
        // conversion, as OpenCV's sws_scale_frame does into its buffer (ffmpeg would convert, then scale at the encoder).
        string toBgr = $"scale={_codedSize.W}:{_codedSize.H}:flags=bicubic" + (_chromaLocation is { } cl ? ":in_chroma_loc=" + cl : "")
            + (_alwaysBt601 ? ":in_color_matrix=bt601" : "") + ",format=bgr24" + TurnFilters;
        args = pipeline switch
        {
            // The source's own format, so ffmpeg passes the samples through untouched; turned after converting.
            FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled or FramePipeline.Yuv420P10Sampled => [.. args, "-vf", select[1], "-f", "rawvideo", "-pix_fmt", _pixelFormat],

            // Like OpenCV: SWS_BICUBIC, colour matrix and range from the stream's tags (BT.601 if untagged).
            FramePipeline.FullFrameResize => [.. args, "-vf", prefix + toBgr + crop,
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            FramePipeline.SampledBgr => [.. args, "-filter_complex",
                $"[0:v]{prefix}{toBgr}[c];[c][1:v][2:v]remap=format=color[out]", "-map", "[out]",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
            _ => [.. args, "-vf", prefix + toBgr + crop + $",scale={Width}:{Height}:flags=bilinear",
                "-f", "rawvideo", "-pix_fmt", "bgr24"],
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(server is null ? "-" : $@"\\.\pipe\{pipeName}");

        using var proc = Start(psi);
        using var livePts = new BlockingCollection<(long Pts, bool NewLayout)>();
        string streamSize = $"{_codedSize.W}x{_codedSize.H}"; // OpenCV's buffer, as scale= in the BGR pipelines
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
                        // showinfo sees the decoded frame, before the output's -pix_fmt conversion.
                        var format = FormatField().Match(line);
                        livePts.Add((long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture),
                            frame.Groups[2].Value != streamSize || (format.Success && format.Groups[1].Value != _pixelFormat)));
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
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            } // ffmpeg stopped reading
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
        int readSize = yuv ? YuvFrameSize
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
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    full.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });

        int emitted = 0;
        bool again = false;
        int againBack = seekBack;
        try
        {
            var small = new byte[Width * Height * 3];
            byte[]? previous = null;
            foreach (var buffer in full.GetConsumingEnumerable(cancel.Token))
            {
                if (previous is not null)
                    free.Add(previous);

                // The frame's timestamp precedes it on stderr; without one the frame can't be placed.
                if (!livePts.TryTake(out var info, Timeout.Infinite, cancel.Token))
                    throw Failed(ShotDetectionError.DecodeFailed, $"ffmpeg gave no timestamp for frame {_livePts.Count + _livePackets.Count}: {stderr.Result}");
                long pts = info.Pts;

                // The seek landed after the wanted frame: again from further back.
                if (emitted == 0 && seek.Length > 1 && SeekLandedLate(pts * liveTimeBase, startFrame))
                {
                    again = true;
                    againBack = SeekBackAgain(seekBack);
                    break;
                }

                // A new size or pixel format on a YUV pipeline: read again on a BGR pipeline (after the finally
                // below). Files only: a stream can't be read twice.
                if (yuv && info.NewLayout && _stream is null && !Streaming)
                {
                    _sizeChanges = again = true;
                    break;
                }

                if (Streaming)
                    _livePts.Add(pts); // from the start already: ffmpeg shifts input timestamps to start at 0
                else
                    _livePackets.Add(PacketOf(pts * liveTimeBase, _livePackets.Count > 0 ? _livePackets[^1] : startFrame - 1));
                if (resizer is null)
                {
                    previous = buffer;
                    emitted++;
                    yield return buffer;
                    continue;
                }

                if (yuv)
                    resizer.ResizeYuv(buffer, small, _yuvForVideo!, _codedSize.W, _codedSize.H, Turn, CropRegion.X, CropRegion.Y);
                else if (sampled)
                    resizer.ResizeSampled(buffer, small);
                else
                    resizer.Resize(buffer, small);
                free.Add(buffer);
                emitted++;
                yield return small;
            }

            // No frame at all: the seek landed after the last keyframe.
            if (emitted == 0 && !again && seek.Length > 1)
            {
                again = true;
                againBack = SeekBackAgain(seekBack);
            }

            if (!again)
            {
                reader.Wait(); // rethrows read errors
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw Failed(ShotDetectionError.DecodeFailed, $"ffmpeg failed ({proc.ExitCode}): {stderr.Result}");
            }
        }
        finally
        {
            // Caller stopped early (or we failed): stop the reader and ffmpeg before disposing.
            cancel.Cancel();
            Kill(proc);
            try
            {
                reader.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            try
            {
                feeder.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            if (maps is not null)
            {
                try
                {
                    Directory.Delete(maps, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        // From the same start (a seek by time could land in the other encode: joined encodes' times start over),
        // without the frames already given.
        if (again)
        {
            foreach (var frame in Frames(startFrame, count, cancellationToken, againBack).Skip(emitted))
                yield return frame;
        }
    }

    /// <summary>
    /// The default decoder threads: OpenCV's, the CPUs available up to 16 (Environment.ProcessorCount honours container
    /// limits), on both paths. Frame-threaded decoders conceal damaged data differently with each thread count, so only
    /// OpenCV's gives scenedetect's frames for corrupt files (checked on Linux: exact at the CPU count, different at 1-3
    /// and 16 on 4 CPUs); clean video decodes the same with any count (docs/decisions/, 2026-10-11; issue #12 before).
    /// </summary>
    internal static int DefaultDecodeThreads(int cpus) => Math.Clamp(cpus, 1, 16);

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

    [System.Text.RegularExpressions.GeneratedRegex(@"\] n:\s*\d+ pts:\s*(-?\d+) .* s:(\d+x\d+) ")]
    private static partial System.Text.RegularExpressions.Regex ShowInfoLine();

    [System.Text.RegularExpressions.GeneratedRegex(@" fmt:(\S+) ")]
    private static partial System.Text.RegularExpressions.Regex FormatField();

    [System.Text.RegularExpressions.GeneratedRegex(@"config in time_base: (\d+)/(\d+)")]
    private static partial System.Text.RegularExpressions.Regex TimeBaseLine();

    /// <summary>Kills a process unless it has already exited (which can happen at any moment, so no check-then-kill).</summary>
    static void Kill(Process proc)
    {
        try
        {
            proc.Kill();
        }
        catch (InvalidOperationException)
        {
        }
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
                $"Could not start {psi.FileName}: {e.Message}. Install ffmpeg (which includes ffprobe) and put it on PATH, "
                + "or set DetectionOptions.FfmpegDirectory.",
                e);
        }
    }

    /// <summary>
    /// That executable in <paramref name="directory"/>, else next to the app, else the first on PATH (absolute folders
    /// only). Not left to the process start, which would also look in the current folder (Windows' CreateProcess and
    /// .NET on Unix both do), where a file someone else put there would run.
    /// </summary>
    internal static string Executable(string? directory, string name)
    {
        string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (directory is not null)
            return Path.Combine(directory, file);
        string beside = Path.Combine(AppContext.BaseDirectory, file);
        if (File.Exists(beside))
            return beside;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim('"'), file);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return candidate;
        }

        return beside; // not found: starting it fails with FfmpegNotFound
    }

    /// <summary>Runs ffmpeg/ffprobe to completion and returns its stdout; cancelling kills it.</summary>
    internal static string Run(string exe,
        IEnumerable<string> args,
        CancellationToken cancellationToken,
        byte[]? stdin = null,
        ShotDetectionError failure = ShotDetectionError.InvalidInput)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        using var proc = Start(psi);
        if (stdin is not null)
        {
            _ = Task.Run(() =>
            {
                // The process may stop reading before the end (ffprobe needs only the headers).
                try
                {
                    proc.StandardInput.BaseStream.Write(stdin);
                    proc.StandardInput.Close();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            });
        }

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

/// <summary>A temporary file in a new folder of its own (only the user can open it on Unix), both deleted on Dispose.</summary>
internal sealed class TempFile(string name) : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-").FullName;

    public string Path => System.IO.Path.Combine(_dir, name);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
