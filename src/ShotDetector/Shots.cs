using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ShotDetector;

/// <summary>A shot spanning frames [Start, End), 0-based, like PySceneDetect's scene list.</summary>
public readonly record struct Shot(int Number, long Start, long End);

public static class Shots
{
    /// <summary>Port of get_scenes_from_cuts with start_in_scene=True (what the CLI uses).</summary>
    public static List<Shot> FromCuts(IReadOnlyList<int> cuts, long frameCount)
    {
        var shots = new List<Shot>();
        if (frameCount == 0)
            return shots;
        long start = 0;
        foreach (int cut in cuts.Distinct().Order())
        {
            shots.Add(new(shots.Count + 1, start, cut));
            start = cut;
        }
        shots.Add(new(shots.Count + 1, start, frameCount));
        return shots;
    }

    public static double Seconds(long frame, Fps fps) => frame * (double)fps.Den / fps.Num;

    /// <summary>HH:MM:SS.nnn, port of FrameTimecode.get_timecode (rounded to ms).</summary>
    public static string Timecode(long frame, Fps fps)
    {
        double secs = Seconds(frame, fps);
        long hrs = (long)(secs / 3600);
        secs -= hrs * 3600;
        long mins = (long)(secs / 60);
        secs = Math.Max(0.0, secs - mins * 60);
        secs = Math.Min(60.0, Math.Round(secs, 3));
        if ((int)secs == 60)
        {
            secs = 0;
            if (++mins >= 60) { mins = 0; hrs++; }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{hrs:00}:{mins:00}:{secs:00.000}");
    }

    /// <summary>Same layout as `scenedetect list-scenes`: start frame 1-based, end frame inclusive.</summary>
    public static string Table(IEnumerable<Shot> shots, Fps fps)
    {
        const string rule = "-----------------------------------------------------------------------";
        var sb = new StringBuilder();
        sb.AppendLine(rule)
          .AppendLine(" | Scene # | Start Frame |  Start Time  |  End Frame  |   End Time   |")
          .AppendLine(rule);
        foreach (var s in shots)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $" |  {s.Number,5}  | {s.Start + 1,11} | {Timecode(s.Start, fps)} | {s.End,11} | {Timecode(s.End, fps)} |"));
        return sb.Append(rule).ToString();
    }

    /// <summary>Same columns as PySceneDetect's scene list CSV (without its leading "Timecode List" row).</summary>
    public static string Csv(IEnumerable<Shot> shots, Fps fps)
    {
        var sb = new StringBuilder(
            "Scene Number,Start Frame,Start Timecode,Start Time (seconds),End Frame,End Timecode," +
            "End Time (seconds),Length (frames),Length (timecode),Length (seconds)\n");
        foreach (var s in shots)
        {
            long len = s.End - s.Start;
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.Number},{s.Start + 1},{Timecode(s.Start, fps)},{Seconds(s.Start, fps):F3},{s.End},{Timecode(s.End, fps)},{Seconds(s.End, fps):F3},{len},{Timecode(len, fps)},{Seconds(len, fps):F3}\n"));
        }
        return sb.ToString();
    }

    public static string Json(IEnumerable<Shot> shots, Fps fps) => JsonSerializer.Serialize(
        shots.Select(s => new
        {
            shot = s.Number,
            startFrame = s.Start + 1,
            endFrame = s.End,
            startTimecode = Timecode(s.Start, fps),
            endTimecode = Timecode(s.End, fps),
            durationFrames = s.End - s.Start,
            durationSeconds = Math.Round(Seconds(s.End - s.Start, fps), 3),
        }),
        new JsonSerializerOptions { WriteIndented = true });
}
