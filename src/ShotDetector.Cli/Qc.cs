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
    public string Status => Pass switch { true => "pass", false => "fail", null => "info" };
}

/// <summary>
/// <c>shotdetect qc</c>: a file's delivery checks in one report, a text table on standard output and JSON with
/// <c>--json</c>, exit code 3 when any judged check fails (docs/decisions/2026-11-01-delivery-qc.md). Loudness for now
/// (FFmpeg's ebur128 through FrameReader, as <c>--loudness</c>); broadcast range, bars and tone, black and freeze
/// follow.
/// </summary>
static class Qc
{
    /// <summary>The checks <c>--checks</c> can name.</summary>
    static readonly string[] Known = ["loudness"];

    const string Usage = """
        Usage: shotdetect qc -i <file> [options]

        A file's delivery checks in one report: a text table on standard output, exit code 0 when every check passes,
        3 when any is out of range (1: an error, 2: usage). Not in scenedetect.

          -i, --input <file|url>             The file to check
              --checks <list>                Comma-separated checks to run (default all): loudness
              --preset <name>                Loudness limits: ebu-r128 (default; -23 LUFS +/- 0.5 LU, true peak -1 dBTP),
                                             atsc-a85 (-24 LKFS +/- 2, -2 dBTP), streaming-14 or streaming-16 (+/- 1, -1 dBTP)
              --loudness-target <LUFS>       Integrated loudness target (default: the preset's)
              --loudness-tolerance <LU>      How far from the target still passes (default: the preset's)
              --true-peak-max <dBTP>         Highest true peak that passes (default: the preset's)
              --json <file>                  Also write the report as JSON (- for standard output, then no table)
              --ffmpeg-dir <folder>          FFmpeg 8's shared libraries (default: the bundled ones, then the system's)
        """;

    /// <summary>Runs <c>shotdetect qc</c> with the arguments after <c>qc</c>; returns the exit code.</summary>
    public static int Run(string[] args)
    {
        string? input = null, jsonPath = null, ffmpegDir = null;
        var preset = QcPreset.EbuR128;
        double? target = null, tolerance = null, truePeakMax = null;
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
                    case "--json":
                        jsonPath = Next();
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
        try
        {
            FrameReader.FFmpegLibraries.Load(ffmpegDir);
            if (checks.Contains("loudness"))
            {
                using var audio = new FrameReader.AudioReader(input);
                report.AddRange(LoudnessChecks(new LoudnessVerdict(FrameReader.Loudness.Measure(audio), limits.Target, limits.Tolerance, limits.TruePeakMax)));
            }
        }
        catch (FrameReader.FrameReaderException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        string[] failing = [.. report.Where(c => c.Pass == false).Select(c => c.Name)];
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
