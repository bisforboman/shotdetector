namespace ShotDetector;

/// <summary>
/// Computes PySceneDetect's content_val: the weighted mean absolute difference of the H, S and V
/// planes (and optionally the edge maps) between the current frame and the previous one.
/// Returns 0 for the first frame. Port of ContentDetector._calculate_frame_score.
/// </summary>
public sealed class ContentScorer(double hueWeight = 1, double satWeight = 1, double lumWeight = 1, double edgeWeight = 0)
{
    public static ContentScorer LumaOnly() => new(0, 0, 1, 0) { IsLumaOnly = true };

    /// <summary>Made by <see cref="LumaOnly"/> (luma_only=True), which names the adaptive_ratio stat "_lum".</summary>
    public bool IsLumaOnly { get; private init; }
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
    bool _hasPrev;

    public double Score(ReadOnlySpan<byte> bgr)
    {
        if (UsesEdges && Edges is null)
            throw new InvalidOperationException("An edge weight needs Edges to be set.");
        int n = bgr.Length / 3;
        if (_h.Length != n)
        {
            _h = new byte[n]; _s = new byte[n]; _v = new byte[n]; _e = new byte[n];
            _prevH = new byte[n]; _prevS = new byte[n]; _prevV = new byte[n]; _prevE = new byte[n];
            _hasPrev = false;
        }
        Hsv.Convert(bgr, _h, _s, _v);
        Edges?.Detect(_v, _e);

        double score = 0;
        LastDeltas = null;
        if (_hasPrev)
        {
            var (dh, ds, dv) = (MeanPixelDistance(_h, _prevH), MeanPixelDistance(_s, _prevS), MeanPixelDistance(_v, _prevV));
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

    public static double MeanPixelDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }
}
