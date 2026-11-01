using System.Globalization;
using System.Text.Json;

/// <summary>
/// A loudness preset: the integrated-loudness target, how far from it still passes, and the highest true peak
/// (docs/decisions/2026-11-01-delivery-qc.md).
/// </summary>
sealed record QcPreset(string Name, double Target, double Tolerance, double TruePeakMax)
{
    /// <summary>EBU R128, the default: -23 LUFS ±0.5 LU, true peak at most -1 dBTP.</summary>
    public static readonly QcPreset EbuR128 = new("ebu-r128", -23, 0.5, -1);

    /// <summary>The presets by name: EBU R128, ATSC A/85 (-24 LKFS ±2 LU, -2 dBTP), streaming at -14 and -16 LUFS (±1 LU, -1 dBTP).</summary>
    public static readonly IReadOnlyList<QcPreset> All =
    [
        EbuR128,
        new("atsc-a85", -24, 2, -2),
        new("streaming-14", -14, 1, -1),
        new("streaming-16", -16, 1, -1),
    ];

    public static QcPreset Named(string name) =>
        All.FirstOrDefault(p => p.Name == name.ToLowerInvariant())
        ?? throw new ArgumentException($"Unknown preset '{name}' ({string.Join(", ", All.Select(p => p.Name))})");
}

/// <summary>
/// One check of a qc report: its name, whether it passed (null: reported, not judged), what was measured and against
/// what limit, as text for the table and as numbers for the JSON.
/// </summary>
sealed record QcCheck(string Name, bool? Pass, string Measured, string Limit, Action<Utf8JsonWriter> Details)
{
    /// <summary>The check's spans for the HTML report's timeline (black, freeze, bars, tone), judged ones marked.</summary>
    public IReadOnlyList<QcSpan> Spans { get; init; } = [];

    public string Status => Pass switch { true => "pass", false => "fail", null => "info" };
}

/// <summary>
/// <c>shotdetect qc</c>: a file's delivery checks in one report, a text table on standard output and JSON with
/// <c>--json</c>, exit code 3 when any judged check fails (docs/decisions/2026-11-01-delivery-qc.md): loudness (FFmpeg's
/// ebur128 through FrameReader, as <c>--loudness</c>), broadcast range (signalstats' BRNG, YMIN and YMAX through
/// FrameReader's <c>PictureMeter</c>), bars and tone (<c>PictureMeter</c>'s colour bars, <c>ReferenceTone</c>); black and
/// freeze follow.
/// </summary>
static class Qc
{
    /// <summary>The checks <c>--checks</c> can name.</summary>
    static readonly string[] Known = ["loudness", "range", "bars-tone", "black", "freeze"];

    /// <summary>How long from the start bars and tone must overlap for <c>--require-bars-tone</c>.</summary>
    static readonly TimeSpan LineUpWindow = TimeSpan.FromMinutes(1);

    const string Usage = """
        Usage: shotdetect qc -i <file> [options]

        A file's delivery checks in one report: a text table on standard output, exit code 0 when every check passes,
        3 when any is out of range (1: an error, 2: usage). Not in scenedetect.

          -i, --input <file|url>             The file to check
              --checks <list>                Comma-separated checks to run (default all): loudness, range, bars-tone, black,
                                             freeze
              --preset <name>                Loudness limits: ebu-r128 (default; -23 LUFS +/- 0.5 LU, true peak -1 dBTP),
                                             atsc-a85 (-24 LKFS +/- 2, -2 dBTP), streaming-14 or streaming-16 (+/- 1, -1 dBTP)
              --loudness-target <LUFS>       Integrated loudness target (default: the preset's)
              --loudness-tolerance <LU>      How far from the target still passes (default: the preset's)
              --true-peak-max <dBTP>         Highest true peak that passes (default: the preset's)
              --brng-max <percent>           Most pixels of any frame outside broadcast range (signalstats' BRNG:
                                             luma outside 16-235, chroma outside 16-240 at 8 bits) that passes
                                             (default 1)
              --black-min <seconds>          Shortest black span (ffmpeg's blackdetect d; default 2)
              --freeze-min <seconds>         Shortest frozen picture (ffmpeg's freezedetect d; default 2)
              --edge <seconds>               Black and frozen pictures at the head and tail, within this of the start
                                             or end, are listed, not judged (default 10)
              --require-bars-tone            Fail unless colour bars and 1 kHz line-up tone overlap in the first
                                             minute (default: bars and tone are reported, never judged)
              --json <file>                  Also write the report as JSON (- for standard output, then no table)
              --html <file>                  Also write the report as one HTML page: the checks and a timeline
              --ffmpeg-dir <folder>          FFmpeg 8's shared libraries (default: the bundled ones, then the system's)
        """;

    /// <summary>Runs <c>shotdetect qc</c> with the arguments after <c>qc</c>; returns the exit code.</summary>
    public static int Run(string[] args)
    {
        string? input = null, jsonPath = null, htmlPath = null, ffmpegDir = null;
        var preset = QcPreset.EbuR128;
        double? target = null, tolerance = null, truePeakMax = null;
        double brngMax = 1;
        bool requireBarsTone = false;
        double blackMin = 2, freezeMin = 2, edge = 10;
        var checks = new List<string>(Known);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                double NextDouble() => double.Parse(Next(), CultureInfo.InvariantCulture);
                switch (args[i])
                {
                    case "-i" or "--input":
                        input = Next();
                        break;
                    case "--checks":
                        checks = [.. Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToLowerInvariant())];
                        if (checks.FirstOrDefault(c => !Known.Contains(c)) is { } unknown)
                            throw new ArgumentException($"Unknown check '{unknown}' ({string.Join(", ", Known)})");
                        if (checks.Count == 0)
                            throw new ArgumentException("--checks needs at least one check");
                        break;
                    case "--preset":
                        preset = QcPreset.Named(Next());
                        break;
                    case "--loudness-target":
                        target = NextDouble();
                        break;
                    case "--loudness-tolerance":
                        tolerance = NextDouble();
                        break;
                    case "--true-peak-max":
                        truePeakMax = NextDouble();
                        break;
                    case "--brng-max":
                        brngMax = NextDouble();
                        if (!(brngMax is >= 0 and <= 100))
                            throw new ArgumentException("--brng-max is a percentage, 0 to 100");
                        break;
                    case "--black-min":
                        blackMin = NextDouble();
                        if (!(blackMin >= 0))
                            throw new ArgumentException("--black-min is seconds, 0 or more");
                        break;
                    case "--freeze-min":
                        freezeMin = NextDouble();
                        if (!(freezeMin >= 0))
                            throw new ArgumentException("--freeze-min is seconds, 0 or more");
                        break;
                    case "--edge":
                        edge = NextDouble();
                        if (!(edge >= 0))
                            throw new ArgumentException("--edge is seconds, 0 or more");
                        break;
                    case "--require-bars-tone":
                        requireBarsTone = true;
                        break;
                    case "--json":
                        jsonPath = Next();
                        break;
                    case "--html":
                        htmlPath = Next();
                        break;
                    case "--ffmpeg-dir":
                        ffmpegDir = Next();
                        break;
                    case "-h" or "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    default:
                        throw new ArgumentException($"Unknown option {args[i]}");
                }
            }

            if (input is null)
                throw new ArgumentException("Missing -i <file>");
            if (input == "-")
                throw new ArgumentException("qc needs a file or URL");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
            return 2;
        }

        var limits = preset with
        {
            Target = target ?? preset.Target,
            Tolerance = tolerance ?? preset.Tolerance,
            TruePeakMax = truePeakMax ?? preset.TruePeakMax,
        };
        var report = new List<QcCheck>();
        var timeline = new QcTimeline { BrngMax = brngMax / 100 };
        try
        {
            FrameReader.FFmpegLibraries.Load(ffmpegDir);
            var info = FrameReader.MediaProbe.Probe(input);
            timeline.Duration = info.Duration?.TotalSeconds ?? 0;
            if (checks.Contains("loudness"))
            {
                if (info.HasAudio)
                {
                    using var audio = new FrameReader.AudioReader(input);
                    report.AddRange(LoudnessChecks(new LoudnessVerdict(FrameReader.Loudness.Measure(audio), limits.Target, limits.Tolerance, limits.TruePeakMax)));
                }
                else
                {
                    report.Add(new("loudness", null, "no audio", "", j => j.WriteNull("measured")));
                }
            }

            bool range = checks.Contains("range"), barsTone = checks.Contains("bars-tone"), freeze = checks.Contains("freeze");
            List<(TimeSpan Start, TimeSpan End)> bars = [];
            if ((range || barsTone || freeze) && info.Video is { } video)
            {
                var (measures, timeBase, first) = Measure(input, (int)Math.Round(video.Rotation ?? 0));
                TimeSpan Time(long pts) => TimeSpan.FromSeconds((double)(pts - first) * timeBase.Num / timeBase.Den);
                bars = [.. measures.BarsSpans().Select(b => (Time(b.StartPts), Time(b.EndPts)))];
                if (htmlPath is not null && range)
                {
                    for (int i = 0; i < measures.Count; i++)
                        timeline.Brng.Add((Time(measures.Pts(i)).TotalSeconds, measures.Brng(i)));
                    timeline.Duration = Math.Max(timeline.Duration, timeline.Brng[^1].Time);
                }

                if (range)
                    report.Add(RangeCheck(measures, brngMax, Time));
                if (barsTone)
                {
                    IReadOnlyList<FrameReader.ToneSegment> tone = [];
                    if (info.HasAudio)
                    {
                        using var audio = new FrameReader.AudioReader(input);
                        tone = FrameReader.ReferenceTone.Find(audio);
                    }

                    report.Add(BarsToneCheck(bars, tone, requireBarsTone));
                }
            }
            else if (range || barsTone)
            {
                if (range)
                    report.Add(new("broadcast-range", null, "no video", "", j => j.WriteNull("measured")));
                if (barsTone)
                    report.Add(new("bars-tone", requireBarsTone ? false : null, "no video", requireBarsTone ? "bars and tone in the first minute" : "", j => j.WriteNull("measured")));
            }

            bool black = checks.Contains("black");
            if ((black || freeze) && info.Video is null)
            {
                if (black)
                    report.Add(new("black", null, "no video", "", j => j.WriteNull("measured")));
                if (freeze)
                    report.Add(new("freeze", null, "no video", "", j => j.WriteNull("measured")));
            }
            else if (black || freeze)
            {
                // ffmpeg's blackdetect and freezedetect on one detection pass (DetectionOptions.BlackDetect and
                // FreezeDetect), the whole video; black spans also for the freezes inside them.
                var result = ShotDetector.ShotDetection.Detect(input, new ShotDetector.DetectionOptions
                {
                    FfmpegDirectory = ffmpegDir,
                    BlackDetect = new() { MinDuration = TimeSpan.FromSeconds(blackMin) },
                    FreezeDetect = freeze ? new() { MinDuration = TimeSpan.FromSeconds(freezeMin) } : null,
                });
                double end = result.Shots[^1].End.Seconds;
                timeline.Duration = Math.Max(timeline.Duration, end);
                if (black)
                    report.Add(BlackCheck(result.BlackSpans ?? [], end, blackMin, edge));
                if (freeze)
                    report.Add(FreezeCheck(result.FreezeSpans ?? [], result.BlackSpans ?? [], bars, end, freezeMin, edge));
            }
        }
        catch (Exception e) when (e is FrameReader.FrameReaderException or InvalidOperationException or ShotDetector.ShotDetectionException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        string[] failing = [.. report.Where(c => c.Pass == false).Select(c => c.Name)];
        if (htmlPath is not null)
        {
            try
            {
                if (timeline.Duration > 0 && FrameReader.MediaProbe.Probe(input).HasAudio)
                {
                    // About four points per drawn unit of the timeline.
                    using var audio = new FrameReader.AudioReader(input);
                    timeline.Waveform = FrameReader.WaveformData.Read(audio, new() { PixelsPerSecond = Math.Max(1, (int)Math.Ceiling(4000 / timeline.Duration)) });
                }
            }
            catch (FrameReader.FrameReaderException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }

            File.WriteAllText(htmlPath, QcHtml.Page(input, limits, report, failing, timeline));
        }

        if (jsonPath is not null)
        {
            string json = Json(input, limits, report, failing);
            if (jsonPath == "-")
                Console.WriteLine(json);
            else
                File.WriteAllText(jsonPath, json + Environment.NewLine);
        }

        if (jsonPath != "-")
            Console.WriteLine(string.Join(Environment.NewLine, Table(report, failing, limits)));
        return failing.Length == 0 ? 0 : 3;
    }

    /// <summary>
    /// Every frame through FrameReader's <c>PictureMeter</c>, as decoded (no autorotate: the meter turns what it must),
    /// so the figures are what FFmpeg's filters give on the same frames; the time base and the first frame's pts.
    /// </summary>
    static (FrameReader.PictureMeasures Measures, FrameReader.Rational TimeBase, long First) Measure(string input, int rotation)
    {
        using var reader = new FrameReader.VideoFrameReader(input, new() { Decoder = new() { Autorotate = false } });
        FrameReader.PictureMeter? meter = null;
        long first = 0;
        while (reader.TryTake(out var frame))
        {
            using (frame)
            {
                if (meter is null)
                {
                    var format = FrameReader.PictureFormat.Of(frame, reader.TimeBase, rotation)
                        ?? throw new InvalidOperationException($"qc's picture checks read planar YUV, not {frame.PixelFormat}.");
                    meter = new FrameReader.PictureMeter(format);
                    first = frame.Pts;
                }

                meter.Add(frame);
            }
        }

        return meter is null ? throw new InvalidOperationException("The video has no frames.") : (meter.Measures, reader.TimeBase, first);
    }

    static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// Broadcast range: every frame's BRNG (signalstats' share of pixels outside 16-235 luma or 16-240 chroma, scaled at
    /// more bits) at most <paramref name="brngMax"/> percent; the worst frame, how many frames are over, and the whole
    /// file's YMIN and YMAX reported. Colour bars frames are left out (a test signal: SMPTE bars' PLUGE is below black
    /// on purpose, 1.25% BRNG in ffmpeg's smptebars), and counted.
    /// </summary>
    internal static QcCheck RangeCheck(FrameReader.PictureMeasures m, double brngMax, Func<long, TimeSpan> time)
    {
        int worst = -1, over = 0, bars = 0, yMin = int.MaxValue, yMax = int.MinValue;
        for (int i = 0; i < m.Count; i++)
        {
            if (m.IsBars(i))
            {
                bars++;
                continue;
            }

            if (worst < 0 || m.Brng(i) > m.Brng(worst))
                worst = i;
            if (m.Brng(i) * 100 > brngMax)
                over++;
            yMin = Math.Min(yMin, m.YMin(i));
            yMax = Math.Max(yMax, m.YMax(i));
        }

        if (worst < 0)
            return new("broadcast-range", null, "only colour bars", "", j => j.WriteNumber("barsFrames", bars));
        double brng = m.Brng(worst);
        var at = time(m.Pts(worst));
        string skipped = bars > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({bars} bars frames not judged)") : "";
        return new("broadcast-range",
            over == 0,
            string.Create(CultureInfo.InvariantCulture, $"{brng * 100:0.###}% at {Clock(at)}, Y {yMin}-{yMax}{skipped}"),
            string.Create(CultureInfo.InvariantCulture, $"<= {brngMax}% per frame"),
            j =>
            {
                j.WriteNumber("measured", brng);
                j.WriteNumber("at", at.TotalSeconds);
                j.WriteNumber("max", brngMax / 100);
                j.WriteNumber("framesOver", over);
                j.WriteNumber("frames", m.Count);
                j.WriteNumber("barsFrames", bars);
                j.WriteNumber("yMin", yMin);
                j.WriteNumber("yMax", yMax);
            });
    }

    /// <summary>
    /// Bars and tone: the colour bars' spans and the line-up tone's segments (with their level); judged only when
    /// required, then passing when a bars span and a tone segment overlap in the first minute.
    /// </summary>
    internal static QcCheck BarsToneCheck(IReadOnlyList<(TimeSpan Start, TimeSpan End)> bars, IReadOnlyList<FrameReader.ToneSegment> tone, bool require)
    {
        bool lineUp = bars.Any(b => tone.Any(t => Overlap(b.Start, b.End, t.Start, t.End) is { } o && o.Start < LineUpWindow));
        string Spans(IEnumerable<(TimeSpan Start, TimeSpan End)> spans) => string.Join(", ", spans.Select(s => $"{Clock(s.Start)}-{Clock(s.End)}"));
        string measured = $"bars {(bars.Count == 0 ? "none" : Spans(bars))}; tone {(tone.Count == 0 ? "none" : Spans(tone.Select(t => (t.Start, t.End))))}";
        return new("bars-tone", require ? lineUp : null, measured, require ? "bars and tone in the first minute" : "", j =>
        {
            j.WriteBoolean("lineUp", lineUp);
            j.WriteStartArray("bars");
            foreach (var b in bars)
            {
                j.WriteStartObject();
                j.WriteNumber("start", b.Start.TotalSeconds);
                j.WriteNumber("end", b.End.TotalSeconds);
                j.WriteEndObject();
            }

            j.WriteEndArray();
            j.WriteStartArray("tone");
            foreach (var t in tone)
            {
                j.WriteStartObject();
                j.WriteNumber("start", t.Start.TotalSeconds);
                j.WriteNumber("end", t.End.TotalSeconds);
                Value(j, "level", t.Level);
                j.WriteEndObject();
            }

            j.WriteEndArray();
        })
        {
            Spans = [.. bars.Select(b => new QcSpan("bars", b.Start.TotalSeconds, b.End.TotalSeconds, false)),
                .. tone.Select(t => new QcSpan("tone", t.Start.TotalSeconds, t.End.TotalSeconds, false))],
        };
    }

    /// <summary>
    /// Black: blackdetect's spans of <paramref name="blackMin"/> seconds or more, each judged only mid-programme, when it
    /// starts at least <paramref name="edge"/> seconds after the start and ends at least that long before the video's
    /// <paramref name="end"/>; spans at the head and tail (slates, fades) are listed, not judged.
    /// </summary>
    internal static QcCheck BlackCheck(IReadOnlyList<ShotDetector.BlackSpan> spans, double end, double blackMin, double edge)
    {
        bool Judged(ShotDetector.BlackSpan b) => b.Start >= edge && b.End <= end - edge;
        var mid = spans.Where(Judged).ToList();
        static string Span(ShotDetector.BlackSpan b) => $"{Clock(TimeSpan.FromSeconds(b.Start))}-{Clock(TimeSpan.FromSeconds(b.End))}";
        string measured = spans.Count == 0 ? "none"
            : mid.Count == 0 ? $"{spans.Count} at the head or tail: {string.Join(", ", spans.Select(Span))}"
            : $"{mid.Count} mid-programme: {string.Join(", ", mid.Select(Span))}";
        return new("black",
            mid.Count == 0,
            measured,
            string.Create(CultureInfo.InvariantCulture, $"none of {blackMin} s+ past {edge} s from either end"),
            j =>
            {
                j.WriteNumber("minDuration", blackMin);
                j.WriteNumber("edge", edge);
                j.WriteNumber("judgedSpans", mid.Count);
                j.WriteStartArray("spans");
                foreach (var b in spans)
                {
                    j.WriteStartObject();
                    j.WriteNumber("start", b.Start);
                    j.WriteNumber("end", b.End);
                    j.WriteNumber("duration", b.Duration);
                    j.WriteBoolean("judged", Judged(b));
                    j.WriteEndObject();
                }

                j.WriteEndArray();
            })
        {
            Spans = [.. spans.Select(b => new QcSpan("black", b.Start, b.End, Judged(b)))],
        };
    }

    /// <summary>
    /// Freeze: freezedetect's still pictures of <paramref name="freezeMin"/> seconds or more, each judged only
    /// mid-programme (at least <paramref name="edge"/> seconds from the start and from the video's
    /// <paramref name="end"/>; one still at the end runs to it) and not inside a black span or colour bars, which are
    /// still by design.
    /// </summary>
    internal static QcCheck FreezeCheck(IReadOnlyList<ShotDetector.FreezeSpan> spans,
        IReadOnlyList<ShotDetector.BlackSpan> black,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> bars,
        double end,
        double freezeMin,
        double edge)
    {
        double End(ShotDetector.FreezeSpan f) => f.End ?? end;
        bool Inside(ShotDetector.FreezeSpan f) =>
            black.Any(b => f.Start >= b.Start && End(f) <= b.End)
            || bars.Any(b => f.Start >= b.Start.TotalSeconds && End(f) <= b.End.TotalSeconds);
        bool Judged(ShotDetector.FreezeSpan f) => f.Start >= edge && End(f) <= end - edge && !Inside(f);
        var mid = spans.Where(Judged).ToList();
        bool pass = mid.Count == 0;
        string Span(ShotDetector.FreezeSpan f) => $"{Clock(TimeSpan.FromSeconds(f.Start))}-{(f.End is { } e ? Clock(TimeSpan.FromSeconds(e)) : "end")}";
        string measured = spans.Count == 0 ? "none"
            : mid.Count == 0 ? $"{spans.Count} not judged (head, tail, black or bars): {string.Join(", ", spans.Select(Span))}"
            : $"{mid.Count} mid-programme: {string.Join(", ", mid.Select(Span))}";
        return new("freeze",
            pass,
            measured,
            string.Create(CultureInfo.InvariantCulture, $"none of {freezeMin} s+ past {edge} s from either end"),
            j =>
            {
                j.WriteNumber("minDuration", freezeMin);
                j.WriteNumber("edge", edge);
                j.WriteNumber("judgedSpans", mid.Count);
                j.WriteStartArray("spans");
                foreach (var f in spans)
                {
                    j.WriteStartObject();
                    j.WriteNumber("start", f.Start);
                    if (f.End is { } e)
                        j.WriteNumber("end", e);
                    else
                        j.WriteNull("end");
                    j.WriteBoolean("judged", Judged(f));
                    j.WriteBoolean("insideBlackOrBars", Inside(f));
                    j.WriteEndObject();
                }

                j.WriteEndArray();
            })
        {
            Spans = [.. spans.Select(f => new QcSpan("freeze", f.Start, f.End, Judged(f)))],
        };
    }

    static (TimeSpan Start, TimeSpan End)? Overlap(TimeSpan a0, TimeSpan a1, TimeSpan b0, TimeSpan b1) =>
        a0 < b1 && b0 < a1 ? (a0 > b0 ? a0 : b0, a1 < b1 ? a1 : b1) : null;

    static string Db(double v) => double.IsNegativeInfinity(v) ? "-inf" : v.ToString("0.0", CultureInfo.InvariantCulture);

    static string Number(double v) => v.ToString(CultureInfo.InvariantCulture);

    static void Value(Utf8JsonWriter j, string name, double v)
    {
        if (double.IsFinite(v))
            j.WriteNumber(name, v);
        else
            j.WriteNull(name);
    }

    /// <summary>The loudness checks: integrated loudness and true peak judged, the loudness range and sample peak reported.</summary>
    internal static IEnumerable<QcCheck> LoudnessChecks(LoudnessVerdict v)
    {
        var r = v.Result;
        yield return new("integrated-loudness", v.IntegratedPass, $"{Db(r.Integrated)} LUFS", $"{Number(v.Target)} +/- {Number(v.Tolerance)} LU", j =>
        {
            Value(j, "measured", r.Integrated);
            j.WriteNumber("target", v.Target);
            j.WriteNumber("tolerance", v.Tolerance);
        });
        yield return new("true-peak", v.TruePeakPass, $"{Db(r.TruePeak)} dBTP", $"<= {Number(v.TruePeakMax)} dBTP", j =>
        {
            Value(j, "measured", r.TruePeak);
            j.WriteNumber("max", v.TruePeakMax);
        });
        yield return new("loudness-range", null, $"{Db(r.Range)} LU", "", j =>
        {
            Value(j, "measured", r.Range);
            Value(j, "low", r.RangeLow);
            Value(j, "high", r.RangeHigh);
        });
        yield return new("sample-peak", null, $"{Db(r.SamplePeak)} dBFS", "", j => Value(j, "measured", r.SamplePeak));
    }

    /// <summary>The text report: a row per check (name, measured, limit, result), then the verdict naming what failed.</summary>
    internal static IEnumerable<string> Table(IReadOnlyList<QcCheck> checks, IReadOnlyList<string> failing, QcPreset limits)
    {
        int name = Math.Max(5, checks.Max(c => c.Name.Length)), measured = Math.Max(8, checks.Max(c => c.Measured.Length)),
            limit = Math.Max(5, checks.Max(c => c.Limit.Length));
        yield return $"{"Check".PadRight(name)}  {"Measured".PadRight(measured)}  {"Limit".PadRight(limit)}  Result";
        foreach (var c in checks)
            yield return $"{c.Name.PadRight(name)}  {c.Measured.PadRight(measured)}  {c.Limit.PadRight(limit)}  {c.Status.ToUpperInvariant()}";
        yield return failing.Count == 0
            ? $"qc: pass ({limits.Name})"
            : $"qc: FAIL ({limits.Name}): {string.Join(", ", failing)}";
    }

    // By hand with Utf8JsonWriter (no reflection, for Native AOT). Full precision; digital silence's peaks are null.
    internal static string Json(string input, QcPreset limits, IReadOnlyList<QcCheck> checks, IReadOnlyList<string> failing)
    {
        var buffer = new MemoryStream();
        using (var j = new Utf8JsonWriter(buffer, new() { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("file", input);
            j.WriteString("preset", limits.Name);
            j.WriteBoolean("pass", failing.Count == 0);
            j.WriteStartArray("failing");
            foreach (string f in failing)
                j.WriteStringValue(f);
            j.WriteEndArray();
            j.WriteStartArray("checks");
            foreach (var c in checks)
            {
                j.WriteStartObject();
                j.WriteString("name", c.Name);
                j.WriteString("status", c.Status);
                c.Details(j);
                j.WriteEndObject();
            }

            j.WriteEndArray();
            j.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
