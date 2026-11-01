using System.Globalization;
using System.Net;
using System.Text;

/// <summary>A span on a qc timeline lane: black, freeze, bars or tone, from start to end (null: to the video's end).</summary>
sealed record QcSpan(string Lane, double Start, double? End, bool Judged);

/// <summary>
/// What the HTML report draws beside the checks: the file's length, each frame's BRNG (time, share) when the range check
/// ran, and the audio's waveform when there is audio.
/// </summary>
sealed class QcTimeline
{
    public double Duration { get; set; }

    public List<(double Time, double Brng)> Brng { get; } = [];

    public double BrngMax { get; set; }

    public FrameReader.WaveformData? Waveform { get; set; }
}

/// <summary>
/// <c>shotdetect qc --html</c>: the report as one self-contained page, no scripts (docs/decisions/2026-11-01-delivery-qc.md,
/// item 14): the checks table, then one inline SVG timeline with a lane each for black, freeze, bars and tone (judged
/// spans in red), the frames' BRNG against its limit, and the audio's waveform. Well-formed XHTML, so it parses as XML.
/// </summary>
static class QcHtml
{
    /// <summary>The timeline's drawing width in SVG units, and its lanes' height.</summary>
    const double Width = 1000, Left = 80, Right = 30, Inner = Width - Left - Right, Lane = 26, Plot = 60;

    static readonly string[] SpanLanes = ["black", "freeze", "bars", "tone"];

    static string E(string text) => WebUtility.HtmlEncode(text);

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Page(string input, QcPreset limits, IReadOnlyList<QcCheck> checks, IReadOnlyList<string> failing, QcTimeline timeline)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n");
        sb.Append(CultureInfo.InvariantCulture, $"<title>qc: {E(Path.GetFileName(input))}</title>\n");
        sb.Append("""
            <style>
            body { font-family: system-ui, sans-serif; margin: 2em; color: #222; }
            table { border-collapse: collapse; margin: 1em 0; }
            th, td { border: 1px solid #ccc; padding: 0.3em 0.6em; text-align: left; vertical-align: top; }
            td.pass { color: #1a7f37; font-weight: bold; } td.fail { color: #cf222e; font-weight: bold; } td.info { color: #57606a; }
            .verdict.pass { color: #1a7f37; } .verdict.fail { color: #cf222e; }
            svg text { font-size: 11px; fill: #444; }
            </style>

            """.Replace("\r\n", "\n"));
        sb.Append("</head>\n<body>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<h1>{E(Path.GetFileName(input))}</h1>\n<p>{E(input)}: preset {E(limits.Name)}, ShotDetector qc</p>\n");
        bool pass = failing.Count == 0;
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"verdict {(pass ? "pass" : "fail")}\"><strong>{(pass ? "Pass" : "FAIL: " + E(string.Join(", ", failing)))}</strong></p>\n");
        sb.Append("<table>\n<tr><th>Check</th><th>Measured</th><th>Limit</th><th>Result</th></tr>\n");
        foreach (var c in checks)
            sb.Append(CultureInfo.InvariantCulture, $"<tr id=\"check-{E(c.Name)}\"><td>{E(c.Name)}</td><td>{E(c.Measured)}</td><td>{E(c.Limit)}</td><td class=\"{c.Status}\">{c.Status.ToUpperInvariant()}</td></tr>\n");
        sb.Append("</table>\n");
        if (timeline.Duration > 0)
            sb.Append(Svg(checks, timeline));
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The x of a time on the timeline.</summary>
    static double X(double t, double duration) => Left + (Math.Clamp(t, 0, duration) / duration * Inner);

    static string Svg(IReadOnlyList<QcCheck> checks, QcTimeline timeline)
    {
        double d = timeline.Duration;
        var spans = checks.SelectMany(c => c.Spans).ToList();
        var lanes = SpanLanes.Where(l => spans.Any(s => s.Lane == l) || checks.Any(c => Owns(c.Name, l))).ToList();
        bool brng = timeline.Brng.Count > 0, wave = timeline.Waveform is { Length: > 0 };
        double height = 20 + (lanes.Count * Lane) + (brng ? Plot + 6 : 0) + (wave ? Plot + 6 : 0) + 20;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<h2>Timeline</h2>\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(Width)}\" height=\"{N(height)}\" viewBox=\"0 0 {N(Width)} {N(height)}\">\n");
        double y = 10;
        foreach (string lane in lanes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<g class=\"lane\" id=\"lane-{lane}\"><text x=\"0\" y=\"{N(y + 15)}\">{lane}</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Left)}\" y=\"{N(y)}\" width=\"{N(Inner)}\" height=\"{N(Lane - 6)}\" fill=\"#f6f8fa\" />");
            foreach (var s in spans.Where(s => s.Lane == lane))
            {
                double x0 = X(s.Start, d), x1 = X(s.End ?? d, d);
                string fill = s.Judged ? "#cf222e" : lane == "bars" ? "#8250df" : lane == "tone" ? "#0969da" : "#57606a";
                sb.Append(CultureInfo.InvariantCulture, $"<rect class=\"span{(s.Judged ? " judged" : "")}\" x=\"{N(x0)}\" y=\"{N(y)}\" width=\"{N(Math.Max(1, x1 - x0))}\" height=\"{N(Lane - 6)}\" fill=\"{fill}\"><title>{lane} {N(s.Start)}-{(s.End is { } e ? N(e) : "end")} s{(s.Judged ? ", judged" : "")}</title></rect>");
            }

            sb.Append("</g>\n");
            y += Lane;
        }

        if (brng)
        {
            // Each frame's BRNG as a bar from the base, scaled so the limit sits at a third of the plot (taller
            // values clipped at the top); the limit as a red line.
            double top = Math.Max(timeline.BrngMax * 3, timeline.Brng.Max(b => b.Brng));
            double scale = top > 0 ? Plot / top : 0, baseY = y + Plot;
            sb.Append(CultureInfo.InvariantCulture, $"<g class=\"lane\" id=\"lane-brng\"><text x=\"0\" y=\"{N(y + 15)}\">BRNG</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Left)}\" y=\"{N(y)}\" width=\"{N(Inner)}\" height=\"{N(Plot)}\" fill=\"#f6f8fa\" />");
            var path = new StringBuilder();
            foreach (var (t, v) in Bucket(timeline.Brng, d))
                path.Append(CultureInfo.InvariantCulture, $"M{N(X(t, d))} {N(baseY)}V{N(baseY - Math.Min(Plot, v * scale))}");
            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{path}\" stroke=\"#bf8700\" fill=\"none\" />");
            double limitY = baseY - Math.Min(Plot, timeline.BrngMax * scale);
            sb.Append(CultureInfo.InvariantCulture, $"<line class=\"limit\" x1=\"{N(Left)}\" y1=\"{N(limitY)}\" x2=\"{N(Width - Right)}\" y2=\"{N(limitY)}\" stroke=\"#cf222e\" stroke-dasharray=\"4 3\" />");
            sb.Append("</g>\n");
            y += Plot + 6;
        }

        if (timeline.Waveform is { Length: > 0 } w)
        {
            // The first channel's (or the channels averaged) min and max per pixel, as audiowaveform's .dat holds them.
            double mid = y + (Plot / 2), half = Plot / 2, full = 32768.0;
            sb.Append(CultureInfo.InvariantCulture, $"<g class=\"lane\" id=\"lane-audio\"><text x=\"0\" y=\"{N(y + 15)}\">audio</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Left)}\" y=\"{N(y)}\" width=\"{N(Inner)}\" height=\"{N(Plot)}\" fill=\"#f6f8fa\" />");
            var path = new StringBuilder();
            double secondsPerPoint = (double)w.SamplesPerPixel / w.SampleRate;
            int points = (int)Math.Ceiling(Inner);
            for (int p = 0; p < points; p++)
            {
                int from = (int)(p * d / points / secondsPerPoint), to = Math.Max(from + 1, (int)((p + 1) * d / points / secondsPerPoint));
                if (from >= w.Length)
                    break;
                int min = 0, max = 0;
                for (int i = from; i < Math.Min(to, w.Length); i++)
                {
                    min = Math.Min(min, w.Min(0, i));
                    max = Math.Max(max, w.Max(0, i));
                }

                double x = Left + p + 0.5;
                path.Append(CultureInfo.InvariantCulture, $"M{N(x)} {N(mid - (max / full * half))}V{N(mid - (min / full * half))}");
            }

            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{path}\" stroke=\"#0969da\" fill=\"none\" />");
            sb.Append("</g>\n");
            y += Plot + 6;
        }

        // The time axis: about ten ticks at a round step.
        double step = Step(d / 10);
        sb.Append("<g class=\"axis\">");
        for (double t = 0; t <= d + 1e-9; t += step)
        {
            double x = X(t, d);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{N(x)}\" y1=\"{N(y)}\" x2=\"{N(x)}\" y2=\"{N(y + 4)}\" stroke=\"#888\" /><text x=\"{N(x)}\" y=\"{N(y + 16)}\" text-anchor=\"middle\">{Clock(t)}</text>");
        }

        sb.Append("</g>\n</svg>\n");
        return sb.ToString();
    }

    /// <summary>Whether a check's spans go on a lane, so a check that ran shows its lane even with no spans.</summary>
    static bool Owns(string check, string lane) => check == lane || (check == "bars-tone" && lane is "bars" or "tone");

    /// <summary>The frames' BRNG, at most one point per drawn unit: the largest in each, so a short spike still shows.</summary>
    static IEnumerable<(double Time, double Brng)> Bucket(List<(double Time, double Brng)> frames, double duration)
    {
        int buckets = (int)Inner;
        var best = new (double Time, double Brng)?[buckets];
        foreach (var f in frames)
        {
            int b = Math.Clamp((int)(f.Time / duration * buckets), 0, buckets - 1);
            if (best[b] is not { } current || f.Brng > current.Brng)
                best[b] = f;
        }

        return best.Where(b => b is not null).Select(b => b!.Value);
    }

    /// <summary>A round step of at least <paramref name="raw"/> seconds: 1, 2, 5, 10, 15, 30 s, 1, 2, 5, 10, 15, 30 min, 1 h...</summary>
    static double Step(double raw)
    {
        foreach (double s in (double[])[0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600])
        {
            if (s >= raw)
                return s;
        }

        return Math.Ceiling(raw / 3600) * 3600;
    }

    static string Clock(double t) => TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
