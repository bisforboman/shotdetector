using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShotDetector;

/// <summary>A shot from Start up to (not including) End, like PySceneDetect's scene list.</summary>
public readonly record struct Shot(int Number, FrameTime Start, FrameTime End)
{
    /// <summary>End minus start, as PySceneDetect computes a scene's length.</summary>
    public FrameTime Duration => End.Minus(Start);
}

/// <summary>Builds shot lists and formats them like scenedetect's list-scenes.</summary>
public static class Shots
{
    /// <summary>Port of get_scenes_from_cuts with start_in_scene=True (what the CLI uses).</summary>
    public static List<Shot> FromCuts(IEnumerable<FrameTime> cuts, FrameTime start, FrameTime end)
    {
        var shots = new List<Shot>();
        foreach (var cut in cuts.DistinctBy(c => c.FrameNum).OrderBy(c => c.FrameNum))
        {
            shots.Add(new(shots.Count + 1, start, cut));
            start = cut;
        }
        shots.Add(new(shots.Count + 1, start, end));
        return shots;
    }

    /// <summary>Same layout as `scenedetect list-scenes`: start frame 1-based, end frame inclusive.</summary>
    public static string Table(IEnumerable<Shot> shots)
    {
        const string rule = "-----------------------------------------------------------------------";
        var sb = new StringBuilder();
        sb.AppendLine(rule)
          .AppendLine(" | Scene # | Start Frame |  Start Time  |  End Frame  |   End Time   |")
          .AppendLine(rule);
        foreach (var s in shots)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $" |  {s.Number,5}  | {s.Start.FrameNum + 1,11} | {s.Start.Timecode()} | {s.End.FrameNum,11} | {s.End.Timecode()} |"));
        return sb.Append(rule).ToString();
    }

    /// <summary>Same columns as PySceneDetect's scene list CSV (without its leading "Timecode List" row).</summary>
    public static string Csv(IEnumerable<Shot> shots)
    {
        var sb = new StringBuilder(
            "Scene Number,Start Frame,Start Timecode,Start Time (seconds),End Frame,End Timecode," +
            "End Time (seconds),Length (frames),Length (timecode),Length (seconds)\n");
        foreach (var s in shots)
        {
            var d = s.Duration;
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.Number},{s.Start.FrameNum + 1},{s.Start.Timecode()},{s.Start.SecondsText()},{s.End.FrameNum},{s.End.Timecode()},{s.End.SecondsText()},{d.FrameNum},{d.Timecode()},{d.SecondsText()}\n"));
        }
        return sb.ToString();
    }

    /// <summary>The shot list as indented JSON.</summary>
    public static string Json(IEnumerable<Shot> shots) => JsonSerializer.Serialize(
        shots.Select(s => new ShotJson(
            s.Number,
            s.Start.FrameNum + 1,
            s.End.FrameNum,
            s.Start.Timecode(),
            s.End.Timecode(),
            s.Duration.FrameNum,
            double.Parse(s.Duration.SecondsText(), CultureInfo.InvariantCulture))).ToList(),
        ShotJsonContext.Default.ListShotJson);
}

internal sealed record ShotJson(
    int Shot, long StartFrame, long EndFrame, string StartTimecode, string EndTimecode, long DurationFrames, double DurationSeconds);

// Source-generated serializer: no reflection, so it also works trimmed / Native AOT.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<ShotJson>))]
internal partial class ShotJsonContext : JsonSerializerContext;
