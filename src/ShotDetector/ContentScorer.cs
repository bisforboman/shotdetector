namespace ShotDetector;

/// <summary>
/// Computes PySceneDetect's content_val: the weighted mean absolute difference of the H, S and V
/// planes (and optionally the edge maps) between the current frame and the previous one.
/// Returns 0 for the first frame. Port of ContentDetector._calculate_frame_score.
/// </summary>
public sealed class ContentScorer(double hueWeight = 1, double satWeight = 1, double lumWeight = 1, double edgeWeight = 0)
{
    /// <summary>Scores only the V (brightness) channel, like luma_only=True.</summary>
    public static ContentScorer LumaOnly() => new(0, 0, 1, 0) { IsLumaOnly = true };

    /// <summary>Made by <see cref="LumaOnly"/> (luma_only=True), which names the adaptive_ratio stat "_lum".</summary>
    public bool IsLumaOnly { get; private init; }
    /// <summary>True when edges have a weight, which requires <see cref="Edges"/>.</summary>
    public bool UsesEdges => edgeWeight > 0;

    /// <summary>
    /// Set to compute edge maps. Required when the edge weight is above 0; PySceneDetect also
    /// computes them whenever a stats file is written.
    /// </summary>
    public EdgeDetector? Edges { get; set; }

    /// <summary>Per-channel deltas behind the last score; null for the first frame. Edges is null when not computed.</summary>
    public (double Hue, double Sat, double Lum, double? Edges)? LastDeltas { get; private set; }

    byte[] _h = [], _s = [], _v = [], _e = [];
    byte[] _prevH = [], _prevS = [], _prevV = [], _prevE = [];
    long[] _sums = [];
    bool _hasPrev;
    const int ChunkPixels = 8192;

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
        Parallel.For(0, chunks, c =>
        {
            long sh = 0, ss = 0, sv = 0;
            for (int i = c * ChunkPixels, end = Math.Min(n, i + ChunkPixels); i < end; i++)
            {
                var (hh, s1, v1) = Hsv.FromBgr(bgr[3 * i], bgr[3 * i + 1], bgr[3 * i + 2]);
                (h[i], s[i], v[i]) = (hh, s1, v1);
                if (hasPrev)
                {
                    sh += Math.Abs(hh - ph[i]);
                    ss += Math.Abs(s1 - ps[i]);
                    sv += Math.Abs(v1 - pv[i]);
                }
            }
            (sums[3 * c], sums[3 * c + 1], sums[3 * c + 2]) = (sh, ss, sv);
        });
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
    public void Record(Stats? stats, int frame, double score)
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

    internal static double MeanPixelDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }
}
