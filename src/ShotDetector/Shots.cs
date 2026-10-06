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
    internal static List<Shot> FromCuts(IEnumerable<FrameTime> cuts, FrameTime start, FrameTime end)
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
    public static string Csv(IEnumerable<Shot> shots, bool includeCutList = true)
    {
        var list = shots.ToList();
        var sb = new StringBuilder();
        // scenedetect's first row: "Timecode List:" and the cuts; with no cuts its fallback writes an empty row.
        // list-scenes --skip-cuts leaves it out.
        if (includeCutList)
            sb.Append(list.Count > 1 ? "Timecode List:," + string.Join(',', list.Skip(1).Select(s => s.Start.Timecode())) : "").Append('\n');
        sb.Append(
            "Scene Number,Start Frame,Start Timecode,Start Time (seconds),End Frame,End Timecode," +
            "End Time (seconds),Length (frames),Length (timecode),Length (seconds)\n");
        foreach (var s in list)
        {
            var d = s.Duration;
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.Number},{s.Start.FrameNum + 1},{s.Start.Timecode()},{s.Start.SecondsText()},{s.End.FrameNum},{s.End.Timecode()},{s.End.SecondsText()},{d.FrameNum},{d.Timecode()},{d.SecondsText()}\n"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The shot list as scenedetect's save-html (export-html) page, byte for byte: its CSS, a cut list table and
    /// a shot table, with each shot's images when given (links relative to the page, as file names or paths).
    /// </summary>
    /// <param name="shots">The shots.</param>
    /// <param name="images">Per shot, the images to show (e.g. from <see cref="Export.SaveImages"/>), relative to the page.</param>
    /// <param name="imageWidth">Width attribute of the images, if any.</param>
    /// <param name="imageHeight">Height attribute of the images, if any.</param>
    public static string Html(IEnumerable<Shot> shots, IReadOnlyList<IReadOnlyList<string>>? images = null, int? imageWidth = null, int? imageHeight = null)
    {
        var list = shots.ToList();
        static string Row(IEnumerable<string> cells, string tag = "td") => string.Join('\n', ["<tr>", .. cells.Select(c => $"<{tag}>{c}</{tag}>"), "</tr>"]);
        // urllib.parse.quote: letters, digits and "_.-~" stay, "/" separates; everything else is %XX (UTF-8).
        static string Quote(string path) => string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        string Image(string file)
        {
            string q = Quote(file);
            return $"<a href=\"{q}\" target=\"_blank\"><img src=\"{q}\"" + (imageHeight is { } h ? $" height=\"{h}\"" : "")
                + (imageWidth is { } w ? $" width=\"{w}\"" : "") + "></a>";
        }
        var rows = list.Select((s, i) =>
        {
            var d = s.Duration;
            string[] cells = [s.Number.ToString(CultureInfo.InvariantCulture), (s.Start.FrameNum + 1).ToString(CultureInfo.InvariantCulture),
                s.Start.Timecode(), s.Start.SecondsText(), s.End.FrameNum.ToString(CultureInfo.InvariantCulture), s.End.Timecode(),
                s.End.SecondsText(), d.FrameNum.ToString(CultureInfo.InvariantCulture), d.Timecode(), d.SecondsText()];
            return Row(images is null || i >= images.Count ? cells : [.. cells, .. images[i].Select(Image)]);
        });
        string[] header = ["Scene Number", "Start Frame", "Start Timecode", "Start Time (seconds)", "End Frame", "End Timecode",
            "End Time (seconds)", "Length (frames)", "Length (timecode)", "Length (seconds)"];
        return string.Join('\n', [
            "<style type=\"text/css\">", HtmlCss, "</style>",
            "<meta http-equiv=\"Content-Type\" content=\"text/html;charset=utf-8\">",
            "<table class=mytable>", Row(["Timecode List:", .. list.Skip(1).Select(s => s.Start.Timecode())]), "</table>", "<br />",
            "<table class=mytable>", Row(header, "th"), .. rows, "</table>", "<br />"]);
    }

    // scenedetect's default CSS for save-html, whitespace included.
    static readonly string HtmlCss = string.Join('\n', [
        "",
        "        table.mytable {",
        "            font-family: times;",
        "            font-size:12px;",
        "            color:#000000;",
        "            border-width: 1px;",
        "            border-color: #eeeeee;",
        "            border-collapse: collapse;",
        "            background-color: #ffffff;",
        "            width=100%;",
        "            max-width:550px;",
        "            table-layout:fixed;",
        "        }",
        "        table.mytable th {",
        "            border-width: 1px;",
        "            padding: 8px;",
        "            border-style: solid;",
        "            border-color: #eeeeee;",
        "            background-color: #e6eed6;",
        "            color:#000000;",
        "        }",
        "        table.mytable td {",
        "            border-width: 1px;",
        "            padding: 8px;",
        "            border-style: solid;",
        "            border-color: #eeeeee;",
        "        }",
        "        #code {",
        "            display:inline;",
        "            font-family: courier;",
        "            color: #3d9400;",
        "        }",
        "        #string {",
        "            display:inline;",
        "            font-weight: bold;",
        "        }",
        "        ",
    ]);

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
