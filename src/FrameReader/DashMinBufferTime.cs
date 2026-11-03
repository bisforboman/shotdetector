using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FrameReader;

/// <summary>
/// The MPD's <c>minBufferTime</c> chosen by the caller (issue #717). FFmpeg 8.1's dash muxer has no option for it: it
/// writes twice the last whole segment's duration (with <c>ldash</c>, the longest GOP), so the final MPD is rewritten
/// after the trailer (docs/decisions/2026-11-03-dash-min-buffer-time.md).
/// </summary>
static class DashMinBufferTime
{
    /// <summary>Muxer options that serve the MPD while it is written, or delete it at the end: our rewrite of the final MPD would reach no player.</summary>
    static readonly string[] Conflicting = ["streaming", "ldash", "window_size", "remove_at_exit"];

    /// <summary>Checks that <paramref name="value"/> can be written and returns it as the MPD writes durations.</summary>
    internal static string Check(TimeSpan value, bool dash, string output, IReadOnlyDictionary<string, string>? muxerOptions)
    {
        if (value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException("options", value, "MinBufferTime can't be negative.");
        if (!dash)
            throw new ArgumentException("MinBufferTime is for DASH output (Container \"dash\", or an .mpd).", "options");

        // A protocol (two letters or more before ':', so not a Windows drive): the MPD isn't a file we can rewrite.
        if (Regex.IsMatch(output, "^[A-Za-z][A-Za-z0-9+.-]+:", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException($"MinBufferTime needs the MPD written to a local file, not \"{output}\".", "options");
        foreach (string key in Conflicting)
        {
            if (muxerOptions is not null && muxerOptions.TryGetValue(key, out string? v) && IsOn(v))
            {
                throw new ArgumentException(
                    $"MinBufferTime is written into the final MPD after muxing, which the dash muxer's {key}={v} makes useless: "
                    + "players read the MPD while it is written, or it is deleted. Leave out one or the other.",
                    "options");
            }
        }

        return Format(value);
    }

    /// <summary>Whether a bool or int option's value turns it on (FFmpeg's bool takes 0/1, true/false, on/off, yes/no).</summary>
    static bool IsOn(string value)
    {
        string v = value.Trim();
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            return n != 0;
        return !(v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// As the muxer's <c>write_time</c> writes a duration (<c>PT[hH][mM]s.fS</c>, e.g. <c>PT2.0S</c>, <c>PT1M30.5S</c>),
    /// with the fraction to the tick rather than cut to tenths.
    /// </summary>
    internal static string Format(TimeSpan value)
    {
        long hours = (long)value.TotalHours;
        var s = new StringBuilder("PT");
        if (hours != 0)
            s.Append(CultureInfo.InvariantCulture, $"{hours}H");
        if (hours != 0 || value.Minutes != 0)
            s.Append(CultureInfo.InvariantCulture, $"{value.Minutes}M");
        string fraction = (value.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        s.Append(CultureInfo.InvariantCulture, $"{value.Seconds}.{(fraction.Length == 0 ? "0" : fraction)}S");
        return s.ToString();
    }

    /// <summary>Replaces the <c>minBufferTime</c> attribute of the MPD element in <paramref name="mpd"/>.</summary>
    internal static void Write(string mpd, string duration)
    {
        string text = File.ReadAllText(mpd);
        var attribute = new Regex("(<MPD\\b[^>]*?\\sminBufferTime=\")[^\"]*(\")", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!attribute.IsMatch(text))
            throw new FrameReaderException(FrameReaderError.WriteFailed, $"\"{mpd}\" has no minBufferTime to set.");
        text = attribute.Replace(text, m => m.Groups[1].Value + duration + m.Groups[2].Value, 1);
        File.WriteAllText(mpd, text, new UTF8Encoding(false));
    }
}
