using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ShotDetector;

/// <summary>
/// Where hand-picked cases may miss edge values: FrameTime and Stats.PyFloat against scenedetect 0.7.1 and Python on
/// 5,000 generated cases (Data/oracle.txt, written by tools/oracle.py), and Stats.Csv's and the cut list's invariants as
/// FsCheck properties (a fixed seed; a failure is shrunk).
/// </summary>
public class PropertyTests
{
    const string Seed = "(20261011,1)";

    [Fact]
    public void SameAsScenedetect()
    {
        var wrong = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "oracle.txt"))
            .Select(line => line.Split('\t'))
            .Select(c => (Question: c[0], Expected: c[1], Actual: Answer(c[0])))
            .Where(c => c.Actual != c.Expected)
            .Select(c => $"{c.Question}\n  scenedetect: {c.Expected}\n  ours:        {c.Actual}")
            .Take(10)
            .ToList();
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    // What tools/oracle.py asks scenedetect or Python, answered by ShotDetector.
    static string Answer(string question)
    {
        string[] q = question.Split(' ');
        FrameTime Time(int i) => new(long.Parse(q[i + 1]), long.Parse(q[i + 2]), new(int.Parse(q[i + 3]), int.Parse(q[i + 4])));
        return q[0] switch
        {
            "r" => Stats.PyFloat(BitConverter.Int64BitsToDouble(long.Parse(q[1]))),
            "t" => $"{Describe(Time(1))} {Time(1).SecondsText()} {Describe(Time(1).PlusFrames(int.Parse(q[6])))}",
            "m" => Describe(Time(1).Minus(Time(6))),
            _ => $"{Math.Sign(ShotDetection.CompareCuts(Time(1), Time(6)))} {(ShotDetection.SameCut(Time(1), Time(6)) ? "True" : "False")}",
        };
    }

    static string Describe(FrameTime t) => $"{t.FrameNum} {BitConverter.DoubleToInt64Bits(t.Seconds)} {t.Timecode()}";

    // Metrics set on frames whose times repeat, in two time bases (frame f at (f % period) / 25 s, half of them as
    // 2(f % period) / 50 s): one row per exact time, a later frame's metric replacing an earlier one's.
    [Property(Replay = Seed, MaxTest = 300)]
    public Property StatsMergeRowsByExactTime() => Prop.ForAll(Arb.From(
        from period in Gen.Choose(1, 20)
        from sets in Gen.ListOf(from frame in Gen.Choose(0, 60) from key in Gen.Elements("a", "b", "c") from v in Gen.Choose(0, 99) select (frame, key, v / 4.0))
        select (period, sets.ToList())),
        c =>
    {
        var (period, sets) = c;
        FrameTime Position(int f) => f % 2 == 0 ? FrameTime.Pts(f % period, 25, new(25, 1)) : FrameTime.Pts(2 * (f % period), 50, new(25, 1));
        var stats = new Stats();
        foreach (var (frame, key, v) in sets)
            stats.Set(frame, key, v);

        var keys = sets.Select(s => s.key).Distinct().Order(StringComparer.Ordinal).ToList();
        var rows = new SortedDictionary<decimal, (int Frame, Dictionary<string, double> Values)>();
        foreach (var group in sets.GroupBy(s => s.frame).OrderBy(g => g.Key))
        {
            decimal time = (group.Key % period) / 25m;
            if (!rows.TryGetValue(time, out var row))
                rows[time] = row = (group.Key, []);
            foreach (var (_, key, v) in group)
                row.Values[key] = v;
        }

        var expected = new StringBuilder("Frame Number,Timecode" + string.Concat(keys.Select(k => "," + k)) + "\n");
        foreach (var (frame, values) in rows.Values)
        {
            expected.Append($"{Position(frame).FrameNum + 1},{Position(frame).Timecode()}")
                .Append(string.Concat(keys.Select(k => "," + (values.TryGetValue(k, out double v) ? Stats.PyFloat(v) : "None"))))
                .Append('\n');
        }

        Assert.Equal(expected.ToString(), stats.Csv(Position));
    });

    // sorted(set(cuts)): ascending, no two equal, every cut represented by the first cut equal to it.
    [Property(Replay = Seed, MaxTest = 300)]
    public Property CutsAreSortedAndUnique() => Prop.ForAll(Arb.From(Gen.ListOf(
        from pts in Gen.Choose(0, 30)
        from tbDen in Gen.Elements(25L, 50L, 1000L)
        select FrameTime.Pts(pts * tbDen / 25, tbDen, new(25, 1)))),
        cuts =>
    {
        var list = cuts.ToList();
        var unique = ShotDetection.Unique(list);
        for (int i = 1; i < unique.Count; i++)
            Assert.True(ShotDetection.CompareCuts(unique[i - 1], unique[i]) < 0);
        Assert.Equal(list.Where((c, i) => !list.Take(i).Any(e => ShotDetection.SameCut(e, c))).Order(Comparer<FrameTime>.Create(ShotDetection.CompareCuts)), unique);
    });
}
