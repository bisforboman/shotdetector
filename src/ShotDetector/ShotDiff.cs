using System.Numerics;

namespace ShotDetector;

/// <summary>A shot's frames in one cut: 0-based frame numbers from the video's first frame as decoded, End exclusive.</summary>
internal readonly record struct FrameSpan(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// What <see cref="ShotDiff"/> keeps of each frame of a cut: its 64-bit perceptual hash, the middle of its grayscale
/// picture (as the hash, without the edges) at <see cref="Side"/>×<see cref="Side"/> pixels (<see cref="Size"/> bytes from
/// frame × Size), and whether that middle is flat (black, or one colour), where the hash is noise.
/// </summary>
internal sealed class CutFrames(ulong[] hashes, byte[] pictures, bool[]? flat = null)
{
    /// <summary>A picture's side in pixels.</summary>
    internal const int Side = 12;

    /// <summary>A picture's bytes.</summary>
    internal const int Size = Side * Side;

    public ulong[] Hashes { get; } = hashes;

    public byte[] Pictures { get; } = pictures;

    public bool[] Flat { get; } = flat ?? new bool[hashes.Length];

    public int Count => Hashes.Length;

    /// <summary>
    /// How many bits frame <paramref name="frame"/>'s hash differs in from <paramref name="other"/>'s frame
    /// <paramref name="otherFrame"/>: none between two flat frames, all between a flat one and another.
    /// </summary>
    public int Distance(int frame, CutFrames other, int otherFrame) =>
        Flat[frame] || other.Flat[otherFrame]
            ? (Flat[frame] && other.Flat[otherFrame] ? 0 : 64)
            : BitOperations.PopCount(Hashes[frame] ^ other.Hashes[otherFrame]);

    public ReadOnlySpan<byte> Picture(int frame) => Pictures.AsSpan(frame * Size, Size);
}

/// <summary>A cut as <see cref="ShotDiff"/> compares it: its detection, its shots' frames, and what it keeps of each frame.</summary>
internal sealed record CutPrint(DetectionResult Result, IReadOnlyList<FrameSpan> Shots, CutFrames Frames);

/// <summary>What happened to a shot between two cuts.</summary>
internal enum ShotChange
{
    /// <summary>In both cuts, in the same order, with the same frames.</summary>
    Unchanged,

    /// <summary>In both cuts, but out of the old cut's order (with its trims, if any).</summary>
    Moved,

    /// <summary>In both cuts, in order, with frames missing (or more of them) at its head or tail.</summary>
    Trimmed,

    /// <summary>Only in the new cut.</summary>
    Added,

    /// <summary>Only in the old cut.</summary>
    Removed,
}

/// <summary>
/// One line of a <see cref="ShotDiff"/> report: the shot's index (0-based) in the old cut <paramref name="A"/> and the new
/// cut <paramref name="B"/>, null where it isn't; <paramref name="Head"/> and <paramref name="Tail"/>, the frames of the
/// old shot missing at the new one's head and tail (negative: the new one has that many more).
/// <paramref name="ALast"/> and <paramref name="BLast"/>: the last shot of a run, where a cut is in one cut only (a run
/// of old shots joined in one new shot, or an old shot split into a run of new ones); null for a single shot. The
/// trims are then the run's.
/// </summary>
internal readonly record struct ShotDiffEntry(ShotChange Change, int? A, int? B, int Head = 0, int Tail = 0, int? ALast = null, int? BLast = null)
{
    /// <summary>The old run's last shot (<see cref="A"/> for a single shot).</summary>
    public int? LastA => ALast ?? A;

    /// <summary>The new run's last shot (<see cref="B"/> for a single shot).</summary>
    public int? LastB => BLast ?? B;
}

/// <summary>
/// <c>shotdetect diff</c>: which shots of an old cut of a programme are unchanged, moved, trimmed, added or removed in a
/// new cut (issue #771, docs/decisions/2026-11-09-shotdetect-diff.md). ShotDetector's own, not scenedetect's: the
/// exactness promise doesn't cover it.
/// <para>
/// Each frame gets a 64-bit perceptual hash (<see cref="Hash"/>: HashDetector's recipe on the middle of its 32×32 shrink),
/// steady under re-encoding, scaling, a new grade or a burnt-in logo near an edge, and keeps the shrink's middle at 12×12. A shot's fingerprint is the hashes of
/// <see cref="Samples"/> frames at fixed places in it. Each new shot is matched to the old shot in which most of its
/// fingerprint frames have a frame within <see cref="MaxDistance"/> bits. The offset between their frames is where the
/// new shot's pictures, laid over the old one's, correlate best (<see cref="Offset"/>): the trims at head and tail follow. The matched shots
/// in the longest run that keeps the old order stay in place; the others moved.
/// </para>
/// </summary>
internal static class ShotDiff
{
    /// <summary>HashDetector's settings for the shrink the hash is taken from: 32×32 pixels, 8×8 bits (a ulong).</summary>
    internal const int HashSize = 8, HashLowpass = 4;

    /// <summary>Most bits two frames' hashes may differ in and still be the same picture.</summary>
    internal const int MaxDistance = 12;

    /// <summary>Frames of a shot's fingerprint.</summary>
    internal const int Samples = 5;

    /// <summary>Most frames of a new shot laid over the old one for each offset tried.</summary>
    const int OffsetFrames = 32;

    /// <summary>
    /// How much worse, in mean 1 − correlation, an offset that keeps a shot's head or tail may be than the best and still
    /// be taken: above what re-encoding does to a still picture, below a few frames of slow motion.
    /// </summary>
    internal const double OffsetTolerance = 0.001;

    /// <summary>
    /// Detects <paramref name="path"/>'s shots with <paramref name="options"/> and takes every frame's hash and picture on
    /// the same pass: a hash detector that never cuts runs beside the options' detectors, so the shots are theirs alone.
    /// </summary>
    internal static CutPrint Read(string path, DetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DetectionOptions();
        var frames = new FrameList();
        List<DetectorSettings> detectors = options.Detectors is { Count: > 0 } given
            ? [.. given]
            : [new DetectorSettings(options.Detector) { Threshold = options.Threshold }];
        detectors.Add(new DetectorSettings(DetectorKind.Hash) { Threshold = 2, HashSize = HashSize, HashLowpass = HashLowpass, OnShrunk = frames.Set });
        var result = ShotDetection.Detect(path, options with { Detectors = detectors }, cancellationToken);
        var starts = result.FramesOf([.. result.Shots.Select(s => s.Start)]);
        var ends = result.FramesOf([.. result.Shots.Select(s => s.End)]);
        var kept = frames.ToFrames(ends.Count > 0 ? ends[^1] : 0);
        var spans = starts.Select((s, i) => new FrameSpan(Math.Min(s, kept.Count), Math.Min(ends[i], kept.Count))).ToList();
        return new CutPrint(result, spans, kept);
    }

    /// <summary>The middle of the shrink the hash is taken from: <see cref="Margin"/> pixels off each side of 32.</summary>
    internal const int Margin = 4, Middle = (HashSize * HashLowpass) - (2 * Margin);

    static readonly double[,] MiddleCosines = HashDetector.CosineFormula(Middle, HashSize);

    /// <summary>
    /// A frame's hash: HashDetector's recipe (scaled by its maximum, the low 8×8 coefficients of the DCT compared with
    /// their median, a bit each) on the middle <see cref="Middle"/>×<see cref="Middle"/> pixels of its 32×32 grayscale
    /// shrink, leaving out the edges where logos, burnt-in timecodes and subtitles sit. Bit k·8 + l is coefficient (k, l).
    /// </summary>
    internal static ulong Hash(ReadOnlySpan<byte> small)
    {
        int n = HashSize * HashLowpass;
        int max = 1;
        for (int y = Margin; y < n - Margin; y++)
        {
            for (int x = Margin; x < n - Margin; x++)
                max = Math.Max(max, small[(y * n) + x]);
        }

        Span<double> rows = stackalloc double[Middle * HashSize];
        for (int y = 0; y < Middle; y++)
        {
            for (int k = 0; k < HashSize; k++)
            {
                double sum = 0;
                for (int x = 0; x < Middle; x++)
                    sum += small[((y + Margin) * n) + x + Margin] / (double)max * MiddleCosines[k, x];
                rows[(y * HashSize) + k] = sum;
            }
        }

        Span<double> low = stackalloc double[HashSize * HashSize];
        for (int j = 0; j < HashSize; j++)
        {
            for (int k = 0; k < HashSize; k++)
            {
                double sum = 0;
                for (int y = 0; y < Middle; y++)
                    sum += MiddleCosines[j, y] * rows[(y * HashSize) + k];
                low[(j * HashSize) + k] = sum;
            }
        }

        Span<double> sorted = stackalloc double[low.Length];
        low.CopyTo(sorted);
        sorted.Sort();
        double median = (sorted[(low.Length / 2) - 1] + sorted[low.Length / 2]) / 2;
        ulong hash = 0;
        for (int i = 0; i < low.Length; i++)
        {
            if (low[i] > median)
                hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>Below this standard deviation of its middle pixels (levels of 255), a frame is flat (<see cref="IsFlat"/>).</summary>
    internal const double FlatDeviation = 2;

    /// <summary>Whether the middle of a 32×32 shrink is flat: black, or one colour, whose hash would be noise.</summary>
    internal static bool IsFlat(ReadOnlySpan<byte> small)
    {
        int n = HashSize * HashLowpass;
        long sum = 0, squares = 0;
        for (int y = Margin; y < n - Margin; y++)
        {
            for (int x = Margin; x < n - Margin; x++)
            {
                int v = small[(y * n) + x];
                sum += v;
                squares += v * v;
            }
        }

        const int Count = Middle * Middle;
        double variance = (squares - ((double)sum * sum / Count)) / Count;
        return variance < FlatDeviation * FlatDeviation;
    }

    /// <summary>
    /// The report, in the new cut's order, with each removed shot placed where it stood: after the shots before it in
    /// the old cut that stayed in order. Shots without frames are left out.
    /// </summary>
    internal static List<ShotDiffEntry> Compare(CutPrint a, CutPrint b) => Compare(a.Shots, a.Frames, b.Shots, b.Frames);

    /// <inheritdoc cref="Compare(CutPrint, CutPrint)"/>
    internal static List<ShotDiffEntry> Compare(IReadOnlyList<FrameSpan> a, CutFrames fa, IReadOnlyList<FrameSpan> b, CutFrames fb)
    {
        // Each new shot's candidates (in parallel: the scan is every old frame for each fingerprint frame), then the
        // choice among them in order, where the previous match breaks ties between repeated pictures.
        var candidates = new List<Candidate>[b.Count];
        Parallel.For(0, b.Count, j => candidates[j] = Candidates(a, fa, b[j], fb));
        var match = new (int A, int Offset)?[b.Count];
        int expected = 0;
        for (int j = 0; j < b.Count; j++)
        {
            if (Choose(candidates[j], expected) is not { } i)
                continue;
            match[j] = (i, 0);
            expected = i + 1;
        }

        Parallel.For(0, b.Count, j =>
        {
            if (match[j] is { } m)
                match[j] = (m.A, Offset(a[m.A], fa, b[j], fb));
        });

        var runs = Runs(a, fa, b, fb, match);
        var inOrder = LongestInOrder([.. runs.Select(r => ((int A, int Offset)?)(r.A, r.Head))]);
        var matchedA = new bool[a.Count];
        foreach (var r in runs)
        {
            for (int i = r.A; i <= r.ALast; i++)
                matchedA[i] = true;
        }

        var report = new List<ShotDiffEntry>();
        int nextRemoved = 0;
        void RemovedBefore(int end)
        {
            for (; nextRemoved < end; nextRemoved++)
            {
                if (!matchedA[nextRemoved] && a[nextRemoved].Length > 0)
                    report.Add(new(ShotChange.Removed, nextRemoved, null));
            }
        }

        int run = 0;
        for (int j = 0; j < b.Count; j++)
        {
            if (b[j].Length == 0)
                continue;
            if (match[j] is null)
            {
                report.Add(new(ShotChange.Added, null, j));
                continue;
            }

            while (runs[run].BLast < j)
                run++;
            var r = runs[run];
            if (r.B != j)
                continue;
            if (inOrder[run])
                RemovedBefore(r.A);
            var change = !inOrder[run] ? ShotChange.Moved : r.Head == 0 && r.Tail == 0 ? ShotChange.Unchanged : ShotChange.Trimmed;
            report.Add(new(change, r.A, j, r.Head, r.Tail, r.ALast != r.A ? r.ALast : null, r.BLast != j ? r.BLast : null));
        }

        RemovedBefore(a.Count);
        return report;
    }

    /// <summary>
    /// A run of old shots <see cref="A"/>..<see cref="ALast"/> and new ones <see cref="B"/>..<see cref="BLast"/> that are
    /// the same frames, though one cut has a cut the other doesn't; mostly one shot each. <see cref="Head"/> and
    /// <see cref="Tail"/>: the old run's frames missing at the new run's head and tail.
    /// </summary>
    readonly record struct Run(int A, int ALast, int B, int BLast, int Head, int Tail);

    /// <summary>
    /// The matched new shots as runs (docs/decisions/2026-11-13-shotdetect-diff-runs.md): consecutive new shots matched to
    /// the same old shot, each starting where the one before ends in it, are one run (the new cut has a cut the old
    /// doesn't); then a run whose frames reach past its old shot's head or tail takes in the old shots there that no new
    /// shot matched, as far as its frames are theirs (the old cut has a cut the new doesn't).
    /// </summary>
    static List<Run> Runs(IReadOnlyList<FrameSpan> a, CutFrames fa, IReadOnlyList<FrameSpan> b, CutFrames fb, (int A, int Offset)?[] match)
    {
        var anchored = new bool[a.Count];
        foreach (var m in match)
        {
            if (m is { } x)
                anchored[x.A] = true;
        }

        var runs = new List<Run>();
        for (int j = 0; j < b.Count; j++)
        {
            if (match[j] is not { } m)
                continue;
            int last = j;
            while (last + 1 < b.Count && match[last + 1] is { } next && next.A == m.A
                && next.Offset == match[last]!.Value.Offset + b[last].Length && b[last].End == b[last + 1].Start)
            {
                last++;
            }

            // The new run's frames from b[j].Start lie over the old cut's from start, shot after shot. Once it takes in
            // other old shots, the offset is found again over all of them: frames the anchor alone doesn't have (a
            // black interstitial) place it, where the anchor alone may be slow enough to fit offsets a few frames apart.
            var span = new FrameSpan(b[j].Start, b[last].End);
            int start = a[m.A].Start + m.Offset;
            var (first, lastA) = Extend(a, fa, fb, anchored, m.A, span, start);
            if (first != m.A || lastA != m.A)
            {
                start = a[first].Start + Offset(new(a[first].Start, a[lastA].End), fa, span, fb);
                (first, lastA) = Extend(a, fa, fb, anchored, m.A, span, start);
            }

            runs.Add(new(first, lastA, j, last, start - a[first].Start, a[lastA].End - start - span.Length));
            j = last;
        }

        return runs;
    }

    /// <summary>
    /// The old shots before and after <paramref name="anchor"/> that no new shot matched and whose frames are
    /// <paramref name="span"/>'s, laid from the old cut's frame <paramref name="start"/>: the run's first and last.
    /// </summary>
    static (int First, int Last) Extend(
        IReadOnlyList<FrameSpan> a, CutFrames fa, CutFrames fb, bool[] anchored, int anchor, FrameSpan span, int start)
    {
        int end = start + span.Length, shift = span.Start - start, first = anchor, last = anchor;
        while (first > 0 && start < a[first].Start && !anchored[first - 1] && a[first - 1].End == a[first].Start
            && Same(fa, Math.Max(start, a[first - 1].Start), a[first - 1].End, fb, shift))
        {
            first--;
        }

        while (last + 1 < a.Count && end > a[last].End && !anchored[last + 1] && a[last + 1].Start == a[last].End
            && Same(fa, a[last + 1].Start, Math.Min(end, a[last + 1].End), fb, shift))
        {
            last++;
        }

        return (first, last);
    }

    /// <summary>
    /// Whether the old cut's frames <paramref name="from"/>..<paramref name="to"/>-1 are the new cut's at
    /// <paramref name="shift"/> frames on: most of <see cref="Samples"/> of them, spread over the span, within
    /// <see cref="MaxDistance"/> bits of theirs.
    /// </summary>
    static bool Same(CutFrames fa, int from, int to, CutFrames fb, int shift)
    {
        var samples = Spread(from, to - from, Samples);
        if (samples.Count == 0)
            return false;
        int found = samples.Count(f => f + shift >= 0 && f + shift < fb.Count && fa.Distance(f, fb, f + shift) <= MaxDistance);
        return found > samples.Count / 2;
    }

    /// <summary>An old shot a new one may be: how many of the new one's fingerprint frames it has, and how near.</summary>
    readonly record struct Candidate(int A, int Found, int Distance);

    /// <summary>Old shots with most of <paramref name="shot"/>'s fingerprint frames within <see cref="MaxDistance"/>.</summary>
    static List<Candidate> Candidates(IReadOnlyList<FrameSpan> a, CutFrames fa, FrameSpan shot, CutFrames fb)
    {
        var list = new List<Candidate>();
        if (shot.Length == 0)
            return list;
        var samples = Fingerprint(shot);
        int need = samples.Count / 2 + 1;
        for (int i = 0; i < a.Count; i++)
        {
            int found = 0, distance = 0;
            foreach (int f in samples)
            {
                int nearest = int.MaxValue;
                for (int k = a[i].Start; k < a[i].End && nearest > 0; k++)
                    nearest = Math.Min(nearest, fa.Distance(k, fb, f));
                if (nearest <= MaxDistance)
                {
                    found++;
                    distance += nearest;
                }
            }

            if (found >= need)
                list.Add(new(i, found, distance));
        }

        return list;
    }

    /// <summary>
    /// The candidate with the most fingerprint frames found; among those within a bit per frame of the nearest, the one
    /// where the old order expects it (the shot after the previous match, or that one again: a split shot), then the
    /// closest to there, then the nearest.
    /// </summary>
    static int? Choose(List<Candidate> candidates, int expected)
    {
        if (candidates.Count == 0)
            return null;
        int found = candidates.Max(c => c.Found);
        var best = candidates.Where(c => c.Found == found).ToList();
        int nearest = best.Min(c => c.Distance);
        return best.Where(c => c.Distance <= nearest + found)
            .OrderBy(c => c.A == expected || c.A == expected - 1 ? 0 : 1)
            .ThenBy(c => Math.Abs(c.A - expected))
            .ThenBy(c => c.Distance)
            .First().A;
    }

    /// <summary>The fingerprint's frames: <see cref="Samples"/> at fixed places (the middles of equal parts), or every frame of a shorter shot.</summary>
    internal static List<int> Fingerprint(FrameSpan shot) => Spread(shot.Start, shot.Length, Samples);

    /// <summary><paramref name="count"/> of the frames from <paramref name="start"/>, each in the middle of an equal part; all of fewer.</summary>
    static List<int> Spread(int start, int length, int count)
    {
        if (length <= count)
            return [.. Enumerable.Range(start, length)];
        return [.. Enumerable.Range(0, count).Select(i => start + (int)(((2L * i + 1) * length) / (2L * count)))];
    }

    /// <summary>
    /// How many frames into <paramref name="a"/> <paramref name="b"/>'s first frame is (negative: before it). Each offset
    /// that lays at least half the shorter shot over the other is scored by the mean of 1 − the correlation of the
    /// pictures laid over each other (up to <see cref="OffsetFrames"/> of them): a grade, a logo and re-encoding change
    /// every offset's score alike. Of the offsets within <see cref="OffsetTolerance"/> of the best, 0 (the head kept),
    /// else the one that keeps the tail, else the best: a still picture, which every offset fits alike, keeps its head,
    /// and slow motion, where the next frames fit nearly as well, takes the trim at one end that edits usually make.
    /// </summary>
    internal static int Offset(FrameSpan a, CutFrames fa, FrameSpan b, CutFrames fb)
    {
        var na = Norms(a, fa);
        var nb = Norms(b, fb);
        int overlap = Math.Max(1, Math.Min(a.Length, b.Length) / 2);
        var scores = new List<(int Offset, double Score)>();
        for (int offset = overlap - b.Length; offset <= a.Length - overlap; offset++)
        {
            // b's frames u from..to-1 lie over a's u + offset.
            int from = Math.Max(0, -offset), to = Math.Min(b.Length, a.Length - offset);
            double sum = 0;
            var us = Spread(from, to - from, OffsetFrames);
            foreach (int u in us)
                sum += 1 - Correlation(fa.Picture(a.Start + u + offset), na[u + offset], fb.Picture(b.Start + u), nb[u]);
            scores.Add((offset, sum / us.Count));
        }

        double best = scores.Min(s => s.Score);
        var close = scores.Where(s => s.Score <= best + OffsetTolerance).ToList();
        int keepsTail = a.Length - b.Length;
        return close.Any(s => s.Offset == 0) ? 0
            : close.Any(s => s.Offset == keepsTail) ? keepsTail
            : close.MinBy(s => s.Score).Offset;
    }

    /// <summary>Each picture's sum and the square root of its sum of squared deviations, for <see cref="Correlation"/>.</summary>
    static (int Sum, double Spread)[] Norms(FrameSpan shot, CutFrames frames)
    {
        var norms = new (int, double)[shot.Length];
        for (int u = 0; u < shot.Length; u++)
        {
            int sum = 0;
            long squares = 0;
            foreach (byte v in frames.Picture(shot.Start + u))
            {
                sum += v;
                squares += v * v;
            }

            norms[u] = (sum, Math.Sqrt(Math.Max(0, squares - ((double)sum * sum / CutFrames.Size))));
        }

        return norms;
    }

    /// <summary>Pearson's correlation of two pictures: 1 alike, 0 unrelated; two flat ones are alike, a flat one and another unrelated.</summary>
    static double Correlation(ReadOnlySpan<byte> x, (int Sum, double Spread) nx, ReadOnlySpan<byte> y, (int Sum, double Spread) ny)
    {
        if (nx.Spread == 0 || ny.Spread == 0)
            return nx.Spread == ny.Spread ? 1 : 0;
        long dot = 0;
        for (int i = 0; i < x.Length; i++)
            dot += x[i] * y[i];
        return (dot - ((double)nx.Sum * ny.Sum / CutFrames.Size)) / (nx.Spread * ny.Spread);
    }

    /// <summary>
    /// Which matches are in a longest run that keeps the old cut's order (by old shot, then by offset into it, so the
    /// pieces of a split shot are in order too).
    /// </summary>
    internal static bool[] LongestInOrder(IReadOnlyList<(int A, int Offset)?> match)
    {
        // Patience sorting: tails[k] is the match ending the best run of length k + 1 found so far.
        var tails = new List<int>();
        var previous = new int[match.Count];
        for (int j = 0; j < match.Count; j++)
        {
            if (match[j] is not { } m)
                continue;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var t = match[tails[mid]]!.Value;
                if (t.A < m.A || (t.A == m.A && t.Offset < m.Offset))
                    lo = mid + 1;
                else
                    hi = mid;
            }

            previous[j] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
                tails.Add(j);
            else
                tails[lo] = j;
        }

        var keep = new bool[match.Count];
        for (int j = tails.Count > 0 ? tails[^1] : -1; j >= 0; j = previous[j])
            keep[j] = true;
        return keep;
    }

    /// <summary>Frames' hashes and pictures as the chunks of a run report them, from any thread.</summary>
    sealed class FrameList
    {
        readonly Lock _lock = new();
        ulong[] _hashes = new ulong[1024];
        bool[] _flat = new bool[1024];
        byte[] _pictures = new byte[1024 * CutFrames.Size];

        /// <summary>A frame's hash from its 32×32 shrink, and the shrink's middle 24×24 kept at 12×12 (2×2 means, rounded).</summary>
        public void Set(int frame, byte[] small)
        {
            ulong hash = Hash(small);
            bool flat = IsFlat(small);
            int n = HashSize * HashLowpass;
            Span<byte> picture = stackalloc byte[CutFrames.Size];
            for (int y = 0; y < CutFrames.Side; y++)
            {
                for (int x = 0; x < CutFrames.Side; x++)
                {
                    int p = (((2 * y) + Margin) * n) + (2 * x) + Margin;
                    picture[y * CutFrames.Side + x] = (byte)((small[p] + small[p + 1] + small[p + n] + small[p + n + 1] + 2) >> 2);
                }
            }

            lock (_lock)
            {
                if (frame >= _hashes.Length)
                {
                    int size = Math.Max(frame + 1, _hashes.Length * 2);
                    Array.Resize(ref _hashes, size);
                    Array.Resize(ref _flat, size);
                    Array.Resize(ref _pictures, size * CutFrames.Size);
                }

                _hashes[frame] = hash;
                _flat[frame] = flat;
                picture.CopyTo(_pictures.AsSpan(frame * CutFrames.Size));
            }
        }

        /// <summary>The first <paramref name="count"/> frames' (unset ones zero).</summary>
        public CutFrames ToFrames(int count)
        {
            lock (_lock)
            {
                var hashes = new ulong[count];
                var pictures = new byte[count * CutFrames.Size];
                var flat = new bool[count];
                Array.Copy(_hashes, hashes, Math.Min(count, _hashes.Length));
                Array.Copy(_flat, flat, Math.Min(count, _flat.Length));
                Array.Copy(_pictures, pictures, Math.Min(pictures.Length, _pictures.Length));
                return new CutFrames(hashes, pictures, flat);
            }
        }
    }
}
