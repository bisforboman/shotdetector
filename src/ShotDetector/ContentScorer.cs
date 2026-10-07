namespace ShotDetector;

/// <summary>
/// Computes PySceneDetect's content_val: the weighted mean absolute difference of the H, S and V
/// planes (and optionally the edge maps) between the current frame and the previous one.
/// Returns 0 for the first frame. Port of ContentDetector._calculate_frame_score.
/// </summary>
internal sealed class ContentScorer(double hueWeight = 1, double satWeight = 1, double lumWeight = 1, double edgeWeight = 0)
{
    /// <summary>Scores only the V (brightness) channel, like luma_only=True.</summary>
    public static ContentScorer LumaOnly() => new(0, 0, 1, 0) { IsLumaOnly = true };

    /// <summary>Made by <see cref="LumaOnly"/> (luma_only=True), which names the adaptive_ratio stat "_lum".</summary>
    internal bool IsLumaOnly { get; private init; }
    /// <summary>True when edges have a weight, which requires <see cref="Edges"/>.</summary>
    internal bool UsesEdges => edgeWeight > 0;

    /// <summary>
    /// Set to compute edge maps. Required when the edge weight is above 0; PySceneDetect also
    /// computes them whenever a stats file is written.
    /// </summary>
    public EdgeDetector? Edges { get; set; }

    /// <summary>Per-channel deltas behind the last score; null for the first frame. Edges is null when not computed.</summary>
    internal (double Hue, double Sat, double Lum, double? Edges)? LastDeltas { get; private set; }

    byte[] _h = [], _s = [], _v = [], _e = [];
    byte[] _prevH = [], _prevS = [], _prevV = [], _prevE = [];
    long[] _sums = [];
    bool _hasPrev;
    const int ChunkPixels = 8192;
    const int ParallelPixels = 1 << 17; // ~362 x 362

    /// <summary>content_val of this frame (packed BGR) against the previous one; 0 for the first frame.</summary>
    public double Score(byte[] bgr)
    {
        if (UsesEdges && Edges is null)
            throw new InvalidOperationException("An edge weight needs Edges to be set.");
        int n = bgr.Length / 3;
        int chunks = (n + ChunkPixels - 1) / ChunkPixels;
        if (_h.Length != n)
        {
            _h = new byte[n]; _s = new byte[n]; _v = new byte[n]; _e = new byte[n];
            _prevH = new byte[n]; _prevS = new byte[n]; _prevV = new byte[n]; _prevE = new byte[n];
            _sums = new long[chunks * 3];
            _hasPrev = false;
        }

        // HSV conversion fused with the per-channel |difference| sums, in parallel chunks. The sums
        // are integers, so adding the chunks up gives exactly MeanPixelDistance's result.
        var (h, s, v, ph, ps, pv, sums, hasPrev) = (_h, _s, _v, _prevH, _prevS, _prevV, _sums, _hasPrev);
        // Small frames (scenedetect's are at most 256 pixels wide) are cheaper on one thread than split up.
        void Chunk(int c)
        {
            int start = c * ChunkPixels, len = Math.Min(n, start + ChunkPixels) - start;
            Hsv.Convert(bgr.AsSpan(3 * start, 3 * len), h.AsSpan(start, len), s.AsSpan(start, len), v.AsSpan(start, len));
            (sums[3 * c], sums[3 * c + 1], sums[3 * c + 2]) = hasPrev
                ? (AbsDiffSum(h.AsSpan(start, len), ph.AsSpan(start, len)), AbsDiffSum(s.AsSpan(start, len), ps.AsSpan(start, len)),
                   AbsDiffSum(v.AsSpan(start, len), pv.AsSpan(start, len)))
                : (0, 0, 0);
        }
        if (n < ParallelPixels)
            for (int c = 0; c < chunks; c++)
                Chunk(c);
        else
            Parallel.For(0, chunks, Chunk);
        Edges?.Detect(_v, _e);

        double score = 0;
        LastDeltas = null;
        if (_hasPrev)
        {
            long th = 0, ts = 0, tv = 0;
            for (int c = 0; c < chunks; c++)
                (th, ts, tv) = (th + sums[3 * c], ts + sums[3 * c + 1], tv + sums[3 * c + 2]);
            var (dh, ds, dv) = (th / (double)n, ts / (double)n, tv / (double)n);
            double? de = Edges is null ? null : MeanPixelDistance(_e, _prevE);
            LastDeltas = (dh, ds, dv, de);
            score = (hueWeight * dh + satWeight * ds + lumWeight * dv + edgeWeight * (de ?? 0.0))
                  / (Math.Abs(hueWeight) + Math.Abs(satWeight) + Math.Abs(lumWeight) + Math.Abs(edgeWeight));
        }

        (_h, _prevH) = (_prevH, _h);
        (_s, _prevS) = (_prevS, _s);
        (_v, _prevV) = (_prevV, _v);
        (_e, _prevE) = (_prevE, _e);
        _hasPrev = true;
        return score;
    }

    /// <summary>Records the last score's metrics like ContentDetector does (not for the first frame).</summary>
    internal void Record(Stats? stats, int frame, double score)
    {
        if (stats is null || LastDeltas is not { } d)
            return;
        stats.Set(frame, "content_val", score);
        stats.Set(frame, "delta_hue", d.Hue);
        stats.Set(frame, "delta_sat", d.Sat);
        stats.Set(frame, "delta_lum", d.Lum);
        if (d.Edges is { } e)
            stats.Set(frame, "delta_edges", e);
    }

    internal static double MeanPixelDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => AbsDiffSum(a, b) / (double)a.Length;

    /// <summary>Σ|a[i] - b[i]| 16 bytes at a time with 128-bit vectors (ARM): the sum so far and how many bytes it took.</summary>
    internal static (long Sum, int Done) AbsDiffSum128(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var total = System.Runtime.Intrinsics.Vector128<uint>.Zero;
        int i = 0;
        while (i + 16 <= a.Length)
        {
            // Up to 128 rounds of 2 x 255 fit a 16-bit lane; then move them to 32-bit lanes.
            var acc = System.Runtime.Intrinsics.Vector128<ushort>.Zero;
            for (int round = 0; round < 128 && i + 16 <= a.Length; round++, i += 16)
            {
                var x = System.Runtime.Intrinsics.Vector128.Create(a.Slice(i, 16));
                var y = System.Runtime.Intrinsics.Vector128.Create(b.Slice(i, 16));
                var d = System.Runtime.Intrinsics.Vector128.Max(x, y) - System.Runtime.Intrinsics.Vector128.Min(x, y);
                var (lo, hi) = System.Runtime.Intrinsics.Vector128.Widen(d);
                acc += lo + hi;
            }
            var (l32, h32) = System.Runtime.Intrinsics.Vector128.Widen(acc);
            total += l32 + h32;
        }
        return ((long)(System.Runtime.Intrinsics.Vector128.Sum(System.Runtime.Intrinsics.Vector128.WidenLower(total))
            + System.Runtime.Intrinsics.Vector128.Sum(System.Runtime.Intrinsics.Vector128.WidenUpper(total))), i);
    }

    /// <summary>Σ|a[i] - b[i]|, 32 bytes at a time where the hardware has 256-bit vectors.</summary>
    internal static long AbsDiffSum(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        int i = 0;
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            var total = System.Runtime.Intrinsics.Vector256<uint>.Zero;
            while (i + 32 <= a.Length)
            {
                // Up to 128 rounds of 2 x 255 fit a 16-bit lane; then move them to 32-bit lanes.
                var acc = System.Runtime.Intrinsics.Vector256<ushort>.Zero;
                for (int round = 0; round < 128 && i + 32 <= a.Length; round++, i += 32)
                {
                    var x = System.Runtime.Intrinsics.Vector256.Create(a.Slice(i, 32));
                    var y = System.Runtime.Intrinsics.Vector256.Create(b.Slice(i, 32));
                    var d = System.Runtime.Intrinsics.Vector256.Max(x, y) - System.Runtime.Intrinsics.Vector256.Min(x, y);
                    var (lo, hi) = System.Runtime.Intrinsics.Vector256.Widen(d);
                    acc += lo + hi;
                }
                var (l32, h32) = System.Runtime.Intrinsics.Vector256.Widen(acc);
                total += l32 + h32;
            }
            sum = (long)(System.Runtime.Intrinsics.Vector256.Sum(System.Runtime.Intrinsics.Vector256.WidenLower(total))
                + System.Runtime.Intrinsics.Vector256.Sum(System.Runtime.Intrinsics.Vector256.WidenUpper(total)));
        }
        else if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
            (sum, i) = AbsDiffSum128(a, b);
        for (; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum;
    }
}
