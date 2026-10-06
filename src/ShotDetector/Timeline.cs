using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace ShotDetector;

/// <summary>
/// The shot list in editors' timeline formats, as scenedetect's save-edl, save-fcp, save-otio and save-qp
/// write them (byte for byte, except that the EDL's comment line names ShotDetector).
/// </summary>
public static class Timeline
{
    /// <summary>
    /// save-edl: a CMX 3600 edit decision list, one cut event per shot.
    /// </summary>
    /// <param name="result">The detection result.</param>
    /// <param name="title">The TITLE line ($VIDEO_NAME is the video's name); default the video's name.</param>
    /// <param name="reel">The reel name of every event.</param>
    /// <param name="startTimecode">SMPTE "HH:MM:SS:FF" (or "HHMMSSFF") added to every event, to match the source's own timecode.</param>
    public static string Edl(DetectionResult result, string? title = null, string reel = "AX", string? startTimecode = null)
    {
        var shots = result.Shots;
        int offset = startTimecode is { } tc && tc.Trim().Length > 0 && shots.Count > 0 ? ParseSmpte(tc, result.Video.Fps) : 0;
        var sb = new StringBuilder($"* CREATED WITH SHOTDETECTOR {Version}\n");
        sb.Append($"TITLE: {Template(title ?? "$VIDEO_NAME", result)}\nFCM: NON-DROP FRAME\n\n");
        for (int i = 0; i < shots.Count; i++)
        {
            string a = EdlTimecode(shots[i].Start.PlusFrames(offset)), b = EdlTimecode(shots[i].End.PlusFrames(offset));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1:000}  {reel} V     C        {a} {b} {a} {b}\n");
        }
        return sb.ToString();
    }

    /// <summary>save-fcp --format fcpx: Final Cut Pro X XML (FCPXML 1.9) with one asset clip per shot.</summary>
    /// <param name="result">The detection result; needs the video's path.</param>
    /// <param name="name">Name of the asset, event and project; default the video's name.</param>
    public static string Fcpx(DetectionResult result, string? name = null)
    {
        var (shots, video) = (result.Shots, result.Video);
        if (shots.Count == 0) throw new InvalidOperationException("No shots to export.");
        name = Template(name ?? "$VIDEO_NAME", result);
        var fps = video.Fps;
        string total = Rational(Exact(shots[^1].End.Minus(shots[0].Start)));
        var root = new Xml("fcpxml", ("version", "1.9"));
        var resources = root.Add("resources");
        resources.Add("format", ("id", "r1"), ("name", $"FFVideoFormat{video.SourceHeight}p{Math.Round(fps.Value * 100):0000}"),
            ("frameDuration", Rational(Reduce(fps.Den, fps.Num))), ("width", $"{video.SourceWidth}"), ("height", $"{video.SourceHeight}"));
        resources.Add("asset", ("id", "r2"), ("name", name), ("start", "0s"), ("duration", total), ("hasVideo", "1"), ("format", "r1"))
            .Add("media-rep", ("kind", "original-media"), ("src", FileUri(PathOf(result))));
        var spine = root.Add("library").Add("event", ("name", name)).Add("project", ("name", name))
            .Add("sequence", ("format", "r1"), ("duration", total), ("tcStart", "0s"), ("tcFormat", "NDF")).Add("spine");
        for (int i = 0; i < shots.Count; i++)
        {
            string start = Rational(Exact(shots[i].Start));
            spine.Add("asset-clip", ("name", $"Shot {i + 1}"), ("ref", "r2"), ("offset", start), ("start", start),
                ("duration", Rational(Exact(shots[i].End.Minus(shots[i].Start)))));
        }
        return root.Document();
    }

    /// <summary>save-fcp --format fcp7: Final Cut Pro 7 XML (xmeml 5), one clip item per shot.</summary>
    /// <param name="result">The detection result; needs the video's path.</param>
    /// <param name="name">Name of the project and sequence; default the video's name.</param>
    public static string Fcp7(DetectionResult result, string? name = null)
    {
        var (shots, video) = (result.Shots, result.Video);
        if (shots.Count == 0) throw new InvalidOperationException("No shots to export.");
        name = Template(name ?? "$VIDEO_NAME", result);
        double fps = video.Fps.Value;
        string ntsc = video.Fps.Den != 1 ? "True" : "False", timebase = Int(fps);
        void Rate(Xml parent)
        {
            var rate = parent.Add("rate");
            rate.Add("timebase").Text = timebase;
            rate.Add("ntsc").Text = ntsc;
        }
        var root = new Xml("xmeml", ("version", "5"));
        var project = root.Add("project");
        project.Add("name").Text = name;
        var sequence = project.Add("sequence");
        sequence.Add("name").Text = name;
        sequence.Add("duration").Text = Int(shots[^1].End.Minus(shots[0].Start).Seconds * fps);
        Rate(sequence);
        var timecode = sequence.Add("timecode");
        Rate(timecode);
        timecode.Add("frame").Text = "0";
        timecode.Add("displayformat").Text = "NDF";
        var videoNode = sequence.Add("media").Add("video");
        var chars = videoNode.Add("format").Add("samplecharacteristics");
        chars.Add("width").Text = $"{video.SourceWidth}";
        chars.Add("height").Text = $"{video.SourceHeight}";
        var track = videoNode.Add("track");
        // The source's duration: OpenCV's frame count from the base timecode (frame-based seconds).
        string sourceFrames = Int(FrameTime.Frame(video.OpenCvFrameCount, video.Fps).Seconds * fps);
        for (int i = 0; i < shots.Count; i++)
        {
            var clip = track.Add("clipitem");
            clip.Add("name").Text = $"Shot {i + 1}";
            clip.Add("enabled").Text = "TRUE";
            clip.Add("duration").Text = sourceFrames;
            Rate(clip);
            string start = Int(shots[i].Start.Seconds * fps), end = Int(shots[i].End.Seconds * fps);
            clip.Add("start").Text = start;
            clip.Add("end").Text = end;
            clip.Add("in").Text = start;
            clip.Add("out").Text = end;
            var file = clip.Add("file", ("id", "file1"));
            if (i == 0)
            {
                file.Add("name").Text = name;
                file.Add("pathurl").Text = FileUri(PathOf(result));
                file.Add("duration").Text = sourceFrames;
                Rate(file);
                var fileChars = file.Add("media").Add("video").Add("samplecharacteristics");
                fileChars.Add("width").Text = $"{video.SourceWidth}";
                fileChars.Add("height").Text = $"{video.SourceHeight}";
            }
            var link = clip.Add("link");
            link.Add("linkclipref").Text = "file1";
            link.Add("mediatype").Text = "video";
        }
        return root.Document();
    }

    /// <summary>
    /// save-otio: an OpenTimelineIO Timeline.1 JSON document with a video track (and an audio track) of one clip per
    /// shot. As scenedetect writes it, each clip's available range is 1980 frames, whatever the video's length.
    /// </summary>
    /// <param name="result">The detection result; needs the video's path.</param>
    /// <param name="name">The timeline's name; default "{video} (PySceneDetect)", scenedetect's.</param>
    /// <param name="audio">Include an audio track.</param>
    public static string Otio(DetectionResult result, string? name = null, bool audio = true)
    {
        string path = Path.GetFullPath(PathOf(result)), file = Path.GetFileName(path);
        double fps = result.Video.Fps.Value;
        string rate = Stats.PyFloat(fps);
        string Time(double value, int indent)
        {
            string pad = new(' ', indent);
            return $"{{\n{pad}    \"OTIO_SCHEMA\": \"RationalTime.1\",\n{pad}    \"rate\": {rate},\n{pad}    \"value\": {Stats.PyFloat(value)}\n{pad}}}";
        }
        static double Round6(double x) => FrameTime.RoundScaled(x, 1_000_000) / 1e6;  // Python's round(x, 6)
        string Clip(Shot s, string pad) => $$"""
            {
            {{pad}}    "OTIO_SCHEMA": "Clip.2",
            {{pad}}    "name": {{Json(file)}},
            {{pad}}    "source_range": {
            {{pad}}        "OTIO_SCHEMA": "TimeRange.1",
            {{pad}}        "duration": {{Time(Round6(s.End.Minus(s.Start).Seconds * fps), pad.Length + 8)}},
            {{pad}}        "start_time": {{Time(Round6(s.Start.Seconds * fps), pad.Length + 8)}}
            {{pad}}    },
            {{pad}}    "enabled": true,
            {{pad}}    "media_references": {
            {{pad}}        "DEFAULT_MEDIA": {
            {{pad}}            "OTIO_SCHEMA": "ExternalReference.1",
            {{pad}}            "name": {{Json(file)}},
            {{pad}}            "available_range": {
            {{pad}}                "OTIO_SCHEMA": "TimeRange.1",
            {{pad}}                "duration": {{Time(1980.0, pad.Length + 16)}},
            {{pad}}                "start_time": {{Time(0.0, pad.Length + 16)}}
            {{pad}}            },
            {{pad}}            "available_image_bounds": null,
            {{pad}}            "target_url": {{Json(path)}}
            {{pad}}        }
            {{pad}}    },
            {{pad}}    "active_media_reference_key": "DEFAULT_MEDIA"
            {{pad}}}
            """.Replace("\r\n", "\n");
        string Track(string trackName, string kind)
        {
            const string pad = "                    ";
            string clips = string.Join(",\n" + pad, result.Shots.Select(s => Clip(s, pad)));
            string children = result.Shots.Count == 0 ? "[]" : $"[\n{pad}{clips}\n                ]";
            return $$"""
                {
                                "OTIO_SCHEMA": "Track.1",
                                "name": {{Json(trackName)}},
                                "enabled": true,
                                "children": {{children}},
                                "kind": {{Json(kind)}}
                            }
                """.Replace("\r\n", "\n");
        }
        var tracks = new List<string> { Track("Video 1", "Video") };
        if (audio)
            tracks.Add(Track("Audio 1", "Audio"));
        return $$"""
            {
                "OTIO_SCHEMA": "Timeline.1",
                "name": {{Json(Template(name ?? "$VIDEO_NAME (PySceneDetect)", result))}},
                "global_start_time": {{Time(0.0, 4)}},
                "tracks": {
                    "OTIO_SCHEMA": "Stack.1",
                    "enabled": true,
                    "children": [
                        {{string.Join(",\n            ", tracks)}}
                    ]
                }
            }

            """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// save-qp: an x264/x265 QP file forcing a keyframe (I) at frame 0 and at every cut, for encoding the
    /// video so each shot starts on a keyframe.
    /// </summary>
    /// <param name="result">The detection result.</param>
    /// <param name="shiftStart">With a start time, number frames from it (scenedetect's default); false keeps the video's numbers.</param>
    public static string Qp(DetectionResult result, bool shiftStart = true)
    {
        var shots = result.Shots;
        long start = shots.Count > 0 ? shots[0].Start.FrameNum : 0, offset = shiftStart ? start : 0;
        var sb = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{(shiftStart ? 0 : start)} I -1\n");
        foreach (var cut in result.Cuts)
            sb.Append(CultureInfo.InvariantCulture, $"{cut.FrameNum - offset} I -1\n");
        return sb.ToString();
    }

    /// <summary>scenedetect's name templates: $VIDEO_NAME is the video's name.</summary>
    static string Template(string text, DetectionResult result) => text.Replace("$VIDEO_NAME", VideoName(result));

    static string Version => typeof(Timeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    static string PathOf(DetectionResult result) =>
        result.VideoPath ?? throw new InvalidOperationException("This export needs the video as a file path; the input was a Stream.");

    /// <summary>The video's name as scenedetect gives it: the file name without extension, cut at an image sequence's '%'.</summary>
    internal static string VideoName(DetectionResult result)
    {
        if (result.VideoPath is not { } path)
            return "stream";
        string name = Path.GetFileName(path);
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        int seq = name.IndexOf('%');
        return seq >= 0 ? name[..seq] : name;
    }

    /// <summary>_edl_timecode: HH:MM:SS:FF from the float seconds, frames as int((seconds * fps) % fps).</summary>
    static string EdlTimecode(FrameTime t)
    {
        double s = t.Seconds, fps = t.Fps.Value;
        int hours = (int)PyFloorDiv(s, 3600), minutes = (int)PyFloorDiv(PyMod(s, 3600), 60), seconds = (int)PyMod(s, 60);
        int frames = (int)PyMod(s * fps, fps);
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}");
    }

    /// <summary>Python's float %: fmod, moved into the divisor's sign.</summary>
    static double PyMod(double a, double b)
    {
        double m = a % b;
        if (m != 0 && (m < 0) != (b < 0)) m += b;
        return m;
    }

    /// <summary>Python's float // (float_floor_div): (a - fmod(a, b)) / b, floored, with its rounding correction.</summary>
    static double PyFloorDiv(double a, double b)
    {
        double mod = a % b, div = (a - mod) / b;
        if (mod != 0 && (b < 0) != (mod < 0))
            div -= 1.0;
        if (div == 0)
            return 0.0;
        double floor = Math.Floor(div);
        return div - floor > 0.5 ? floor + 1.0 : floor;
    }

    /// <summary>_parse_edl_start_timecode: SMPTE HH:MM:SS:FF or HHMMSSFF to a frame count.</summary>
    static int ParseSmpte(string value, Fps fps)
    {
        string v = value.Trim();
        string[] parts = v.Contains(':') ? v.Split(':')
            : v.Length == 8 && v.All(char.IsDigit) ? [v[..2], v[2..4], v[4..6], v[6..]]
            : throw new ArgumentException($"Invalid start timecode '{value}': expected HH:MM:SS:FF or 8 digits (HHMMSSFF).");
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit)))
            throw new ArgumentException($"Invalid start timecode '{value}': expected HH:MM:SS:FF or 8 digits (HHMMSSFF).");
        int[] n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        int maxFrames = (int)Math.Ceiling(fps.Value);
        if (n[1] >= 60 || n[2] >= 60 || n[3] >= maxFrames)
            throw new ArgumentException($"Invalid start timecode '{value}': MM<60, SS<60, FF<{maxFrames} required.");
        return (int)Math.Round((n[0] * 3600 + n[1] * 60 + n[2]) * fps.Value) + n[3];
    }

    /// <summary>Python's round() to an integer (half to even), as text.</summary>
    static string Int(double x) => ((long)Math.Round(x, MidpointRounding.ToEven)).ToString(CultureInfo.InvariantCulture);

    /// <summary>FrameTimecode's exact seconds: pts × time_base (a frame number counts in 1/fps).</summary>
    static (BigInteger Num, BigInteger Den) Exact(FrameTime t) =>
        t.TbDen > 0 ? Reduce(t.Value, t.TbDen) : Reduce((BigInteger)t.FrameNum * t.Fps.Den, t.Fps.Num);

    static (BigInteger Num, BigInteger Den) Reduce(BigInteger num, BigInteger den)
    {
        var g = BigInteger.GreatestCommonDivisor(num, den);
        return g.IsZero ? (0, 1) : (num / g, den / g);
    }

    /// <summary>FCPXML rational time: "n/ds", or "ns" for whole seconds.</summary>
    static string Rational((BigInteger Num, BigInteger Den) r) => r.Den.IsOne ? $"{r.Num}s" : $"{r.Num}/{r.Den}s";

    /// <summary>pathlib's as_uri of the absolute path: file:///C:/dir/a%20b.mp4 (Windows) or file:///dir/a%20b.mp4.</summary>
    static string FileUri(string path)
    {
        string full = Path.GetFullPath(path).Replace('\\', '/');
        static string Quote(string p) => string.Join('/', p.Split('/').Select(Uri.EscapeDataString));
        return OperatingSystem.IsWindows() && full.Length > 1 && full[1] == ':' ? "file:///" + full[..2] + Quote(full[2..]) : "file://" + Quote(full);
    }

    /// <summary>A JSON string as Python's json.dumps writes it (ensure_ascii).</summary>
    static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                _ when c < 0x20 || c > 0x7e => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    /// <summary>An element tree printed as Python's minidom toprettyxml(indent="  ") prints it.</summary>
    sealed class Xml(string name, params (string Key, string Value)[] attributes)
    {
        readonly List<Xml> _children = [];
        public string? Text { get; set; }

        public Xml Add(string child, params (string Key, string Value)[] attrs)
        {
            var x = new Xml(child, attrs);
            _children.Add(x);
            return x;
        }

        public string Document()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" ?>\n");
            Write(sb, "");
            return sb.ToString();
        }

        void Write(StringBuilder sb, string indent)
        {
            sb.Append(indent).Append('<').Append(name);
            foreach (var (k, v) in attributes)
                sb.Append(' ').Append(k).Append("=\"").Append(Escape(v, attribute: true)).Append('"');
            if (Text is not null)
                sb.Append('>').Append(Escape(Text, attribute: false)).Append("</").Append(name).Append(">\n");
            else if (_children.Count == 0)
                sb.Append("/>\n");
            else
            {
                sb.Append(">\n");
                foreach (var c in _children)
                    c.Write(sb, indent + "  ");
                sb.Append(indent).Append("</").Append(name).Append(">\n");
            }
        }

        static string Escape(string s, bool attribute)
        {
            s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return attribute ? s.Replace("\"", "&quot;") : s;
        }
    }
}
