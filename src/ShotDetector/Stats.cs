using System.Globalization;
using System.Text;

namespace ShotDetector;

/// <summary>
/// Per-frame metrics, written in the layout of PySceneDetect's stats file (`scenedetect -s`):
/// 1-based frame number, timecode, then metric columns in sorted order; "None" where a frame has
/// no value for a column. Differs: no delta_edges column (edges are not ported).
/// </summary>
public sealed class Stats
{
    readonly SortedDictionary<int, Dictionary<string, double>> _rows = [];
    readonly SortedSet<string> _keys = new(StringComparer.Ordinal); // Python sorts by code point

    /// <summary>Records a metric for a frame (0-based index).</summary>
    public void Set(int frame, string key, double value)
    {
        _keys.Add(key);
        if (!_rows.TryGetValue(frame, out var row))
            _rows[frame] = row = [];
        row[key] = value;
    }

    /// <summary>A recorded metric, or null.</summary>
    public double? Get(int frame, string key) =>
        _rows.TryGetValue(frame, out var row) && row.TryGetValue(key, out var v) ? v : null;

    /// <param name="position">Maps a frame index to its position (rows are keyed by position).</param>
    public string Csv(Func<int, FrameTime> position)
    {
        var sb = new StringBuilder("Frame Number,Timecode");
        foreach (var key in _keys)
            sb.Append(',').Append(key);
        sb.Append('\n');
        foreach (var (frame, row) in _rows)
        {
            var at = position(frame);
            sb.Append(at.FrameNum + 1).Append(',').Append(at.Timecode());
            foreach (var key in _keys)
                sb.Append(',').Append(row.TryGetValue(key, out var v) ? PyFloat(v) : "None");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Formats like Python's repr(float): shortest round-trip, "1.0" not "1", "1e-05" not "1E-05".</summary>
    public static string PyFloat(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOf('E');
        if (e >= 0)
        {
            int exponent = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            return $"{s[..e]}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
        }
        return s.Contains('.') ? s : s + ".0";
    }
}
