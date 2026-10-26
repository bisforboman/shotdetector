using System.Diagnostics;
using System.Globalization;

namespace ShotDetector;

/// <summary>
/// Per-frame metrics, written in the layout of PySceneDetect's stats file (`scenedetect -s`):
/// 1-based frame number, timecode, then metric columns in sorted order; "None" where a frame has
/// no value for a column. Differs: no delta_edges column (edges are not ported).
/// </summary>
public sealed class Stats
{
    internal Stats()
    {
    }

    // Columnar: one array of values per key, indexed by frame - _first, and per frame the keys it has, in the order they
    // were first set on it (a state of _orders, 0 = no row): a film's hours of frames hold a few doubles each, not a
    // dictionary (about 400 bytes a frame before).
    readonly List<string> _names = []; // in the order first set
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    readonly List<double[]> _columns = [];
    readonly List<int[]> _orders = [[]]; // each state's keys, in the order set
    readonly Dictionary<(int State, int Key), int> _next = [];
    int[] _rows = []; // each frame's state
    int _first;
    int _length; // frames from _first to the last recorded one

    /// <summary>Records a metric for a frame (0-based index).</summary>
    internal void Set(int frame, string key, double value)
    {
        int k = KeyIndex(key);
        int i = Slot(frame);
        int state = _rows[i];
        if (Array.IndexOf(_orders[state], k) < 0)
            _rows[i] = Next(state, k);
        _columns[k][i] = value;
    }

    /// <summary>Copies another's rows for frames from..to-1.</summary>
    internal void Merge(Stats other, int from, int to)
    {
        foreach (var (frame, key, value) in other.Entries(from, to))
            Set(frame, key, value);
    }

    /// <summary>The recorded metrics of frames from..to-1, in frame order.</summary>
    internal IEnumerable<(int Frame, string Key, double Value)> Entries(int from, int to)
    {
        long start = Math.Max(from, (long)_first), end = Math.Min(to, (long)_first + _length);
        for (long frame = start; frame < end; frame++)
        {
            int i = (int)(frame - _first);
            foreach (int k in _orders[_rows[i]])
                yield return ((int)frame, _names[k], _columns[k][i]);
        }
    }

    /// <summary>A recorded metric, or null.</summary>
    public double? Get(int frame, string key)
    {
        long i = (long)frame - _first;
        return i >= 0 && i < _length && _index.TryGetValue(key, out int k) && Array.IndexOf(_orders[_rows[i]], k) >= 0
            ? _columns[k][i]
            : null;
    }

    /// <summary>The stats file's text: <see cref="WriteCsv(TextWriter, Func{int, FrameTime})"/> into a string.</summary>
    /// <param name="position">Maps a frame index to its position (rows are keyed by position).</param>
    public string Csv(Func<int, FrameTime> position)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(writer, position);
        return writer.ToString();
    }

    /// <summary>
    /// Writes the stats file (<see cref="Csv"/>'s text, character for character) to <paramref name="writer"/>, a row at a
    /// time, without building the whole file first.
    /// </summary>
    /// <param name="writer">Where the text goes.</param>
    /// <param name="position">Maps a frame index to its position (rows are keyed by position).</param>
    /// <remarks>
    /// It streams while every recorded frame's position comes after the one before (by seconds, then frame number): a
    /// first pass over the positions checks that, then each row is written as it is formatted. Where positions repeat or
    /// go back (two encodes joined), rows sharing a position are merged and all of them sorted before the first is
    /// written: their values are held in memory (not their text) and the rows still go to the writer one at a time.
    /// <paramref name="position"/> is called up to twice per recorded frame and must give the same answer each time.
    /// The caller owns the writer: this method neither flushes nor disposes it.
    /// </remarks>
    public void WriteCsv(TextWriter writer, Func<int, FrameTime> position) => WriteCsv(writer, position, merge: false);

    /// <param name="writer">Where the text goes.</param>
    /// <param name="position">Maps a frame index to its position (rows are keyed by position).</param>
    /// <param name="merge">Takes the merging path even where positions come in order (tests compare the two).</param>
    internal void WriteCsv(TextWriter writer, Func<int, FrameTime> position, bool merge)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(position);
        int[] sorted = [.. Enumerable.Range(0, _names.Count).Order(Comparer<int>.Create((a, b) => string.CompareOrdinal(_names[a], _names[b])))]; // Python sorts by code point
        writer.Write("Frame Number,Timecode");
        foreach (int k in sorted)
        {
            writer.Write(',');
            writer.Write(_names[k]);
        }

        writer.Write('\n');
        if (merge || !InOrder(position))
        {
            MergedByPosition(writer, sorted, position);
            return;
        }

        // Every row's position comes after the one before: each row is its own, in frame order, written as it comes.
        for (int i = 0; i < _length; i++)
        {
            if (_rows[i] == 0)
                continue;
            int[] keys = _orders[_rows[i]];
            Position(writer, position(_first + i));
            foreach (int k in sorted)
                Value(writer, Array.IndexOf(keys, k) >= 0 ? _columns[k][i] : null);
            writer.Write('\n');
        }
    }

    /// <summary>Whether every recorded frame's position comes <see cref="After"/> the one before.</summary>
    bool InOrder(Func<int, FrameTime> position)
    {
        FrameTime? previous = null;
        for (int i = 0; i < _length; i++)
        {
            if (_rows[i] == 0)
                continue;
            var at = position(_first + i);
            if (!After(at, previous))
                return false;
            previous = at;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="at"/> sorts after <paramref name="previous"/> by (Seconds, FrameNum) and is another
    /// position: for frame numbers and timestamps below 2^53 one Key gives one (Seconds, FrameNum), so a later one is
    /// another Key.
    /// </summary>
    static bool After(FrameTime at, FrameTime? previous) =>
        at.TbDen >= 0 && at.TbDen <= 1L << 53 && at.Value is >= -(1L << 53) and <= 1L << 53
        && (previous is not { } p || at.Seconds > p.Seconds || (at.Seconds == p.Seconds && at.FrameNum > p.FrameNum));

    /// <summary>
    /// The rows where positions repeat or go back (two encodes joined): scenedetect keys its stats by FrameTimecode (equal
    /// when the exact times are), so a later frame's metrics overwrite an earlier one's at the same position, key by key;
    /// metrics the later frame doesn't set keep the earlier values. Rows sorted by (Seconds, FrameNum), stably.
    /// </summary>
    void MergedByPosition(TextWriter writer, int[] sorted, Func<int, FrameTime> position)
    {
        var byPosition = new Dictionary<(long, long), int>();
        var rows = new List<(FrameTime At, double?[] Values)>();
        for (int i = 0; i < _length; i++)
        {
            if (_rows[i] == 0)
                continue;
            var at = position(_first + i);
            if (!byPosition.TryGetValue(at.Key, out int r))
            {
                byPosition[at.Key] = r = rows.Count;
                rows.Add((at, new double?[_names.Count])); // the row keeps its first position, as a dict keeps its first key
            }

            foreach (int k in _orders[_rows[i]])
                rows[r].Values[k] = _columns[k][i];
        }

        foreach (var (at, values) in rows.OrderBy(p => p.At.Seconds).ThenBy(p => p.At.FrameNum))
        {
            Position(writer, at);
            foreach (int k in sorted)
                Value(writer, values[k]);
            writer.Write('\n');
        }
    }

    static void Position(TextWriter writer, FrameTime at)
    {
        Span<char> number = stackalloc char[20];
        bool formatted = (at.FrameNum + 1).TryFormat(number, out int length, provider: CultureInfo.InvariantCulture);
        Debug.Assert(formatted, "a long has at most 20 characters");
        writer.Write(number[..length]);
        writer.Write(',');
        writer.Write(at.Timecode());
    }

    static void Value(TextWriter writer, double? value)
    {
        writer.Write(',');
        if (value is { } v)
            WritePyFloat(writer, v);
        else
            writer.Write("None");
    }

    /// <summary><see cref="PyFloat"/>'s text, without a string where it is .NET's own ("R", no exponent) plus ".0".</summary>
    internal static void WritePyFloat(TextWriter writer, double d)
    {
        Span<char> buffer = stackalloc char[32];
        if (Math.Abs(d) < 1e16 && d.TryFormat(buffer, out int length, "R", CultureInfo.InvariantCulture) && buffer[..length].IndexOf('E') < 0)
        {
            writer.Write(buffer[..length]);
            if (buffer[..length].IndexOf('.') < 0)
                writer.Write(".0");
        }
        else
        {
            writer.Write(PyFloat(d));
        }
    }

    /// <summary>The key's column, added on its first use.</summary>
    int KeyIndex(string key)
    {
        for (int k = 0; k < _names.Count; k++)
        {
            if (ReferenceEquals(_names[k], key))
                return k;
        }

        if (_index.TryGetValue(key, out int index))
            return index;
        index = _names.Count;
        _names.Add(key);
        _index[key] = index;
        _columns.Add(new double[_rows.Length]);
        return index;
    }

    /// <summary>The state after adding a key to a frame's: the same keys in the same order share one.</summary>
    int Next(int state, int key)
    {
        if (!_next.TryGetValue((state, key), out int next))
        {
            _next[(state, key)] = next = _orders.Count;
            _orders.Add([.. _orders[state], key]);
        }

        return next;
    }

    /// <summary>The frame's index in the arrays, growing them to take it.</summary>
    int Slot(int frame)
    {
        if (_length == 0)
            _first = frame;
        if (frame < _first)
        {
            int shift = _first - frame; // a frame before the first so far: rare (a range's stats merged out of order)
            Resize(checked(_length + shift), shift);
            _first = frame;
            _length += shift;
        }
        else if ((long)frame - _first >= _length)
        {
            int length = checked(frame - _first + 1);
            if (length > _rows.Length)
                Resize(Math.Max(length, (int)Math.Min(Array.MaxLength, Math.Max(16L, 2L * _rows.Length))), 0);
            _length = length;
        }

        return frame - _first;
    }

    void Resize(int capacity, int shift)
    {
        capacity = Math.Max(capacity, _rows.Length);
        _rows = Moved(_rows, capacity, shift, _length);
        for (int k = 0; k < _columns.Count; k++)
            _columns[k] = Moved(_columns[k], capacity, shift, _length);
    }

    static T[] Moved<T>(T[] array, int capacity, int shift, int length)
    {
        var moved = new T[capacity];
        Array.Copy(array, 0, moved, shift, length);
        return moved;
    }

    /// <summary>Formats like Python's repr(float): shortest round-trip, "1.0" not "1", "1e-05" not "1E-05".</summary>
    internal static string PyFloat(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        if (Math.Abs(d) >= 1e16 && !s.Contains('E')) // .NET writes these out in full below 1e21; Python from 1e16
            s = d.ToString("E" + (s.TrimStart('-').TrimEnd('0').Length - 1), CultureInfo.InvariantCulture);
        int e = s.IndexOf('E');
        if (e >= 0)
        {
            int exponent = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            return $"{s[..e]}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
        }

        return s.Contains('.') ? s : s + ".0";
    }
}
