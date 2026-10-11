using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ShotDetector;

/// <summary>
/// tools/oracle.py in one Python process for a test class: scenedetect 0.7.1's FrameTimecode and Python's repr(float),
/// a case per line. <see cref="Ask"/> gives null without Python or scenedetect, and the properties then check nothing.
/// </summary>
public sealed class Oracle : IDisposable
{
    readonly Process? _python = Start();

    static Process? Start()
    {
        try
        {
            var python = Process.Start(new ProcessStartInfo("python", [Path.Combine(AppContext.BaseDirectory, "oracle.py")])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            python.ErrorDataReceived += (_, _) => { };
            python.BeginErrorReadLine();
            python.StandardInput.WriteLine("r 0");
            if (python.StandardOutput.ReadLine() == "0.0")
                return python;
            python.Kill();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            return null; // no Python, or it stopped (no scenedetect)
        }
    }

    public string? Ask(string question)
    {
        if (_python is null)
            return null;
        _python.StandardInput.WriteLine(question);
        return _python.StandardOutput.ReadLine();
    }

    public void Dispose()
    {
        if (_python is null)
            return;
        _python.StandardInput.Close();
        _python.WaitForExit();
        _python.Dispose();
    }
}

/// <summary>
/// Random cases where hand-picked ones may miss edge values: FrameTime against scenedetect's FrameTimecode, Stats.PyFloat
/// against repr(float) (both through <see cref="Oracle"/>), and Stats.Csv's and the cut list's invariants. A fixed seed;
/// a failure is shrunk and printed as the oracle's question (feed it to `python tools/oracle.py` to see Python's answer).
/// </summary>
public class PropertyTests(Oracle oracle) : IClassFixture<Oracle>
{
    const string Seed = "(20261011,1)";

    static readonly Gen<Fps> Rates = Gen.OneOf(
        Gen.Elements(new Fps(24, 1),
            new Fps(25, 1),
            new Fps(30, 1),
            new Fps(50, 1),
            new Fps(60, 1),
            new Fps(1, 1),
            new Fps(24000, 1001),
            new Fps(30000, 1001),
            new Fps(60000, 1001),
            new Fps(15, 2)),
        from num in Gen.Choose(1, 240_000)
        from den in Gen.Choose(1, 1001)
        let g = (int)System.Numerics.BigInteger.GreatestCommonDivisor(num, den)
        select new Fps(num / g, den / g)); // reduced, as ffprobe prints rates and Fraction keeps them

    static readonly Gen<long> TimeBases = Gen.OneOf(
        Gen.Elements(1000L, 90000L, 12800L, 15360L, 30000L, 24000L, 1_000_000L, 25L, 2L, 16L, 1024L), // 16, 1024: exact ms ties
        Gen.Choose(1, 1_000_000).Select(d => (long)d));

    // A double near x: 0 to 2 ulps either side.
    static Gen<double> Near(double x) => Gen.Choose(-2, 2).Select(n => n < 0 ? Math.BitDecrement(n == -2 ? Math.BitDecrement(x) : x)
        : n > 0 ? Math.BitIncrement(n == 2 ? Math.BitIncrement(x) : x) : x);

    static readonly Gen<FrameTime> Times =
        from fps in Rates
        from tbDen in TimeBases
        from n in Gen.OneOf(Gen.Choose(0, 3_600_000), Gen.Elements(59_999, 3_599_999, 86_399_999)) // ms rolling over to a minute/hour
        from kind in Gen.Choose(0, 5)
        from wiggle in Gen.Choose(-2, 2)
        from pts in Gen.Choose(0, int.MaxValue)
        from secs in kind == 4 ? Near((n + 0.5) / fps.Value) : Near((n + 0.5) / 1000) // half a frame or half a millisecond
        select kind switch
        {
            0 => FrameTime.Frame(n, fps),
            1 => FrameTime.Pts(pts, tbDen, fps),
            2 => FrameTime.Pts(Math.Max(0, (2L * n + 1) * tbDen * fps.Den / (2L * fps.Num) + wiggle), tbDen, fps), // half a frame
            3 => FrameTime.Pts(Math.Max(0, ((2L * n + 1) * tbDen + 1000) / 2000 + wiggle), tbDen, fps), // half a millisecond
            _ => FrameTime.FromSeconds(Math.Max(0, secs), fps),
        };

    static IEnumerable<FrameTime> Shrink(FrameTime t)
    {
        if (t.TbDen == -2)
        {
            for (int digits = 0; digits < 15; digits++)
            {
                double rounded = Math.Round(t.Seconds, digits);
                if (rounded != t.Seconds)
                    yield return FrameTime.FromSeconds(rounded, t.Fps);
            }
        }
        else if (t.Value > 0)
        {
            yield return t with { Value = t.Value / 2 };
            yield return t with { Value = t.Value - 1 };
        }

        if (t.Fps != new Fps(25, 1))
            yield return new FrameTime(t.Value, t.TbDen, new Fps(25, 1));
    }

    static string Question(FrameTime t) => $"{t.TbDen switch { 0 => 'f', -2 => 's', _ => 'p' }} {t.Value} {t.TbDen} {t.Fps.Num} {t.Fps.Den}";

    static string Describe(FrameTime t) => $"{t.FrameNum} {BitConverter.DoubleToInt64Bits(t.Seconds)} {t.Timecode()}";

    public sealed record Case(FrameTime At, int Frames)
    {
        public override string ToString() => $"t {Question(At)} {Frames}";
    }

    public sealed record Pair(FrameTime A, FrameTime B, char Op)
    {
        public override string ToString() => $"{Op} {Question(A)} {Question(B)}";
    }

    static readonly Arbitrary<Case> Singles = Arb.From(
        from at in Times
        from frames in Gen.Choose(-1000, 1000)
        select new Case(at, frames),
        s => Shrink(s.At).Select(at => s with { At = at }).Append(s with { Frames = s.Frames / 2 }).Where(c => c != s));

    // Pairs at one rate (FrameTimecode subtracts only those; comparisons of timestamps are exact only there), and
    // timestamps at two rates for comparisons.
    static Arbitrary<Pair> Pairs(char op) => Arb.From(
        from a in Times
        from b in Times
        from sameRate in op == 'm' ? Gen.Constant(true) : Gen.Elements(true, true, false)
        from close in Gen.Elements(true, false) // b a few ticks from a, in a's time base: equal or nearly
        from ticks in Gen.Choose(-2, 2)
        let b1 = close && a.TbDen > 0 ? FrameTime.Pts(Math.Max(0, a.Value + ticks) * 2, a.TbDen * 2, b.Fps) : b
        select new Pair(a, sameRate ? new FrameTime(b1.Value, b1.TbDen, a.Fps) : b1, op),
        p => Shrink(p.A).Select(a => p with { A = a }).Concat(Shrink(p.B).Select(b => p with { B = b })));

    void Agrees(string question, string actual)
    {
        string? expected = oracle.Ask(question);
        if (expected is not null)
            Assert.Equal(expected, actual);
    }

    [Property(Replay = Seed, MaxTest = 3000)]
    public Property FrameTimeIsFrameTimecode() => Prop.ForAll(Singles, s =>
        Agrees(s.ToString(), $"{Describe(s.At)} {s.At.SecondsText()} {Describe(s.At.PlusFrames(s.Frames))}"));

    [Property(Replay = Seed, MaxTest = 2000)]
    public Property MinusIsFrameTimecodeMinus() => Prop.ForAll(Pairs('m'), p => Agrees(p.ToString(), Describe(p.A.Minus(p.B))));

    [Property(Replay = Seed, MaxTest = 2000)]
    public Property CutsCompareLikeFrameTimecode() => Prop.ForAll(Pairs('c'), p =>
    {
        int sign = Math.Sign(ShotDetection.CompareCuts(p.A, p.B));
        Agrees(p.ToString(), $"{sign} {(ShotDetection.SameCut(p.A, p.B) ? "True" : "False")}");
    });

    static readonly Gen<double> Doubles = Gen.OneOf(
        from hi in Gen.Choose(int.MinValue, int.MaxValue) // any bit pattern
        from lo in Gen.Choose(int.MinValue, int.MaxValue)
        let d = BitConverter.Int64BitsToDouble(((long)hi << 32) | (uint)lo)
        where double.IsFinite(d)
        select d,
        Gen.Choose(0, int.MaxValue).Select(b => BitConverter.Int64BitsToDouble(b)), // subnormals
        from m in Gen.Choose(-99_999, 99_999) // around the switches to exponent notation (1e-05, 1e+16)
        from e in Gen.Choose(-10, 22)
        from d in Near(double.Parse($"{m}e{e}", CultureInfo.InvariantCulture))
        select d,
        Gen.Choose(0, 1_000_000).Select(n => n / 1000.0), // a stats file's usual values
        Gen.Choose(0, 20).Select(k => Math.Pow(10, k))); // whole numbers

    [Property(Replay = Seed, MaxTest = 3000)]
    public Property PyFloatIsRepr() => Prop.ForAll(Arb.From(Doubles, ArbMap.Default.ArbFor<double>().Shrinker), d =>
        Agrees($"r {BitConverter.DoubleToInt64Bits(d)}", Stats.PyFloat(d)));

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
