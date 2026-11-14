using System.Globalization;
using System.Text;
using System.Text.Json;
using ShotDetector;

/// <summary>
/// <c>shotdetect diff</c>: what changed between two cuts of the same programme, shot by shot (issue #771,
/// docs/decisions/2026-11-09-shotdetect-diff.md): a text table on standard output, JSON with <c>--json</c>; exit code 0
/// when nothing changed, 3 when something did. ShotDetector's own (<see cref="ShotDiff"/>), not scenedetect's.
/// </summary>
static class Diff
{
    const string Usage = """
        Usage: shotdetect diff <old> <new> [options]

        Which shots of the old cut are unchanged, moved, trimmed, added or removed in the new one, with each shot's
        frames in both cuts and the frames trimmed at its head and tail. Shots are matched by perceptual hashes of
        their frames, so re-encoding, scaling, a new grade or a small burnt-in logo don't count as changes. A text
        table on standard output; exit code 0 when nothing changed, 3 when something did (1: an error, 2: usage).
        Not in scenedetect.

              --json <file>                  Also write the report as JSON (- for standard output, then no table)
          -d, --detector <name>              Detector that finds the shots in both cuts: adaptive (default), content,
                                             threshold, hist or hash
          -t, --threshold <n>                Its threshold (default: the detector's)
          -m, --min-scene-len <time>         Minimum shot length (default 0.6s)
              --ffmpeg-dir <dir>             Folder with FFmpeg 8's libraries or ffmpeg and ffprobe
              --decoder <auto|inprocess|process>  As for detection (default auto)
        """;

    /// <summary>Runs <c>shotdetect diff</c> with the arguments after <c>diff</c>; returns the exit code.</summary>
    public static int Run(string[] args)
    {
        var inputs = new List<string>();
        string? jsonPath = null;
        var options = new DetectionOptions();
        var detector = new DetectorSettings(DetectorKind.Adaptive);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--json":
                        jsonPath = Next();
                        break;
                    case "-d" or "--detector":
                        detector = new DetectorSettings(Next() switch
                        {
                            "adaptive" => DetectorKind.Adaptive,
                            "content" => DetectorKind.Content,
                            "threshold" => DetectorKind.Threshold,
                            "hist" => DetectorKind.Histogram,
                            "hash" => DetectorKind.Hash,
                            var d => throw new ArgumentException($"Unknown detector '{d}'"),
                        })
                        { Threshold = detector.Threshold };
                        break;
                    case "-t" or "--threshold":
                        detector = detector with { Threshold = double.Parse(Next(), CultureInfo.InvariantCulture) };
                        break;
                    case "-m" or "--min-scene-len":
                        options = options with { MinSceneLength = Next() };
                        break;
                    case "--ffmpeg-dir":
                        options = options with { FfmpegDirectory = Next() };
                        break;
                    case "--decoder":
                        options = options with
                        {
                            Decoder = Next() switch
                            {
                                "auto" => VideoDecoder.Auto,
                                "process" => VideoDecoder.FfmpegProcess,
                                "inprocess" => VideoDecoder.InProcess,
                                var d => throw new ArgumentException($"Unknown decoder '{d}' (auto, inprocess or process)"),
                            },
                        };
                        break;
                    case "-h" or "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    case var arg when arg.StartsWith('-') && arg.Length > 1:
                        throw new ArgumentException($"Unknown option {arg}");
                    default:
                        inputs.Add(args[i]);
                        break;
                }
            }

            if (inputs.Count != 2)
                throw new ArgumentException("diff needs two videos: the old cut and the new one");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
            return 2;
        }

        options = options with { Detectors = [detector] };
        CutPrint a, b;
        List<ShotDiffEntry> report;
        try
        {
            a = ShotDiff.Read(inputs[0], options);
            b = ShotDiff.Read(inputs[1], options);
            report = ShotDiff.Compare(a, b);
        }
        catch (Exception e) when (e is ShotDetectionException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        if (jsonPath is not null)
        {
            string json = Json(inputs[0], a, inputs[1], b, report);
            if (jsonPath == "-")
                Console.WriteLine(json);
            else
                File.WriteAllText(jsonPath, json);
        }

        if (jsonPath != "-")
            Console.Write(Table(a, b, report));
        return report.All(r => r.Change == ShotChange.Unchanged) ? 0 : 3;
    }

    /// <summary>The changes counted, in the report's order of kinds.</summary>
    static IEnumerable<(string Name, int Count)> Summary(List<ShotDiffEntry> report) =>
        Enum.GetValues<ShotChange>().Select(c => (Name(c), report.Count(r => r.Change == c)));

    static string Name(ShotChange change) => change.ToString().ToLowerInvariant();

    /// <summary>The report as text: a summary line, then a row per shot.</summary>
    internal static string Table(CutPrint a, CutPrint b, List<ShotDiffEntry> report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Old cut: {a.Result.Shots.Count} shots; new cut: {b.Result.Shots.Count} shots. ");
        text.AppendLine(string.Join(", ", Summary(report).Select(s => $"{s.Count} {s.Name}")));
        text.AppendLine();
        text.AppendLine($"{"Change",-10} {"Old cut",-36} {"New cut",-36} {"Head",5} {"Tail",5}");
        foreach (var r in report)
        {
            string Shot(CutPrint cut, int? index, int? lastIndex)
            {
                if (index is not { } i || lastIndex is not { } last)
                    return "-";
                Shot s = cut.Result.Shots[i], e = cut.Result.Shots[last];
                string number = last == i ? $"#{s.Number}" : $"#{s.Number}-#{e.Number}";
                return $"{number} {s.Start.Timecode()}-{e.End.Timecode()}";
            }

            bool matched = r.A is not null && r.B is not null;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{Name(r.Change),-10} {Shot(a, r.A, r.LastA),-36} {Shot(b, r.B, r.LastB),-36} {(matched ? r.Head.ToString(CultureInfo.InvariantCulture) : ""),5} {(matched ? r.Tail.ToString(CultureInfo.InvariantCulture) : ""),5}");
        }

        return text.ToString();
    }

    /// <summary>
    /// The report as JSON: each cut, the changes counted, then a row per shot in the new cut's order with removed shots
    /// where they stood; a shot's frames and timecodes as the shot list's JSON gives them (startFrame 1-based, endFrame
    /// its last frame). A row for a run of shots (a cut in one cut only) gives its first shot and lastShot, and the
    /// run's frames and timecodes; lastShot is shot for a single shot.
    /// </summary>
    internal static string Json(string pathA, CutPrint a, string pathB, CutPrint b, List<ShotDiffEntry> report)
    {
        using var stream = new MemoryStream();
        using (var j = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            void Cut(string name, string path, CutPrint cut)
            {
                j.WriteStartObject(name);
                j.WriteString("path", path);
                j.WriteNumber("frameRate", cut.Result.Video.FrameRate.Value);
                j.WriteNumber("frames", cut.Result.FrameCount);
                j.WriteNumber("shots", cut.Result.Shots.Count);
                j.WriteEndObject();
            }

            void Shot(string name, CutPrint cut, int? index, int? lastIndex)
            {
                if (index is not { } i || lastIndex is not { } last)
                {
                    j.WriteNull(name);
                    return;
                }

                Shot s = cut.Result.Shots[i], e = cut.Result.Shots[last];
                j.WriteStartObject(name);
                j.WriteNumber("shot", s.Number);
                j.WriteNumber("lastShot", e.Number);
                j.WriteNumber("startFrame", s.Start.FrameNum + 1);
                j.WriteNumber("endFrame", e.End.FrameNum);
                j.WriteString("startTimecode", s.Start.Timecode());
                j.WriteString("endTimecode", e.End.Timecode());
                j.WriteEndObject();
            }

            j.WriteStartObject();
            Cut("a", pathA, a);
            Cut("b", pathB, b);
            j.WriteStartObject("summary");
            foreach (var (name, count) in Summary(report))
                j.WriteNumber(name, count);
            j.WriteEndObject();
            j.WriteStartArray("shots");
            foreach (var r in report)
            {
                j.WriteStartObject();
                j.WriteString("status", Name(r.Change));
                Shot("a", a, r.A, r.LastA);
                Shot("b", b, r.B, r.LastB);
                if (r.A is not null && r.B is not null)
                {
                    j.WriteNumber("headTrim", r.Head);
                    j.WriteNumber("tailTrim", r.Tail);
                }
                else
                {
                    j.WriteNull("headTrim");
                    j.WriteNull("tailTrim");
                }

                j.WriteEndObject();
            }

            j.WriteEndArray();
            j.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
