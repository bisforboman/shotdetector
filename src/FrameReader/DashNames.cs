using System.Text.RegularExpressions;

namespace FrameReader;

/// <summary>
/// DASH segment names around FFmpeg's dash muxer, which has neither: folders in <c>init_seg_name</c>/<c>media_seg_name</c>
/// are created first, and <c>$ContentType$</c> (video, audio, text: Shaka Packager's <c>video/</c> and <c>audio/</c>
/// layout) is written as a token per representation, then renamed and put into the MPD's templates (issue #191).
/// </summary>
static class DashNames
{
    const string Placeholder = "$ContentType$";
    static readonly string[] NameOptions = ["init_seg_name", "media_seg_name"];

    /// <summary>The token a representation's content type is written as until <see cref="Finish"/>.</summary>
    static string Token(int representation) => $"ct{representation}ct";

    /// <summary>
    /// Swaps <c>$ContentType$</c> for the per-representation token (by way of <c>$RepresentationID$</c>) and creates the
    /// folders the names need. <paramref name="types"/> is each output stream's content type: its representation's.
    /// </summary>
    /// <returns>Whether <see cref="Finish"/> has names to put back.</returns>
    internal static bool Prepare(Dictionary<string, string> options, string mpd, IReadOnlyList<string> types)
    {
        bool contentType = false;
        foreach (string key in NameOptions)
        {
            if (!options.TryGetValue(key, out string? name) || !name.Contains(Placeholder, StringComparison.Ordinal))
                continue;
            if (!name.Contains("$RepresentationID$", StringComparison.Ordinal) && types.Distinct().Count() < types.Count)
            {
                throw new ArgumentException(
                    $"{key} \"{name}\" names two streams of one content type alike: add $RepresentationID$ too.", "options");
            }

            options[key] = name.Replace(Placeholder, "ct$RepresentationID$ct", StringComparison.Ordinal);
            contentType = true;
        }

        // The muxer creates no folders: make each representation's, where the name's folder part is known up front.
        string dir = Path.GetDirectoryName(Path.GetFullPath(mpd))!;
        foreach (string key in NameOptions)
        {
            if (!options.TryGetValue(key, out string? name) || Path.GetDirectoryName(name) is not { Length: > 0 } folder)
                continue;
            for (int i = 0; i < types.Count; i++)
            {
                string resolved = folder.Replace("$RepresentationID$", i.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                if (!resolved.Contains('$'))
                    Directory.CreateDirectory(Path.Combine(dir, resolved));
            }
        }

        return contentType;
    }

    /// <summary>Renames the tokens in files and folders to the content types and puts <c>$ContentType$</c>'s values into the MPD.</summary>
    internal static void Finish(string mpd, IReadOnlyList<string> types)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(mpd))!;

        // Deepest first, so a folder's contents are renamed before the folder itself.
        foreach (string path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        {
            string name = Path.GetFileName(path), renamed = name;
            for (int i = 0; i < types.Count; i++)
                renamed = renamed.Replace(Token(i), types[i], StringComparison.Ordinal);
            if (renamed == name)
                continue;
            string target = Path.Combine(Path.GetDirectoryName(path)!, renamed);
            if (Directory.Exists(path))
                Directory.Move(path, target);
            else
                File.Move(path, target, overwrite: true);
        }

        string text = File.ReadAllText(mpd);
        var timeout = TimeSpan.FromSeconds(1);
        text = Regex.Replace(text,
            "<Representation\\b[^>]*>.*?</Representation>",
            rep =>
            {
                string id = Regex.Match(rep.Value, "\\bid=\"([^\"]*)\"", RegexOptions.None, timeout).Groups[1].Value;
                return int.TryParse(id, out int i) && i < types.Count
                    ? rep.Value.Replace("ct$RepresentationID$ct", types[i], StringComparison.Ordinal)
                    : rep.Value;
            },
            RegexOptions.Singleline,
            timeout);
        File.WriteAllText(mpd, text, new System.Text.UTF8Encoding(false));
    }

    /// <summary>A stream's DASH content type, as Shaka Packager names its folders.</summary>
    internal static string ContentType(FFmpeg.AutoGen.AVMediaType type) => type switch
    {
        FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO => "video",
        FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_AUDIO => "audio",
        FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_SUBTITLE => "text",
        _ => "data",
    };
}
