namespace ShotDetector;

/// <summary>
/// Computes PySceneDetect's content_val: the weighted mean absolute difference of the H, S and V
/// planes between the current frame and the previous one. Returns 0 for the first frame.
/// Port of ContentDetector._calculate_frame_score.
/// Differs: no delta_edges component (default weight is 0, so default scores are unaffected).
/// </summary>
public sealed class ContentScorer(double hueWeight = 1, double satWeight = 1, double lumWeight = 1)
{
    public static ContentScorer LumaOnly() => new(0, 0, 1);

    public bool IsLumaOnly => hueWeight == 0 && satWeight == 0 && lumWeight == 1;

    /// <summary>Per-channel deltas behind the last score; null for the first frame.</summary>
    public (double Hue, double Sat, double Lum)? LastDeltas { get; private set; }

    byte[] _h = [], _s = [], _v = [];
    byte[] _prevH = [], _prevS = [], _prevV = [];
    bool _hasPrev;

    public double Score(ReadOnlySpan<byte> bgr)
    {
        int n = bgr.Length / 3;
        if (_h.Length != n)
        {
            _h = new byte[n]; _s = new byte[n]; _v = new byte[n];
            _prevH = new byte[n]; _prevS = new byte[n]; _prevV = new byte[n];
            _hasPrev = false;
        }
        Hsv.Convert(bgr, _h, _s, _v);

        double score = 0;
        LastDeltas = null;
        if (_hasPrev)
        {
            var (dh, ds, dv) = (MeanPixelDistance(_h, _prevH), MeanPixelDistance(_s, _prevS), MeanPixelDistance(_v, _prevV));
            LastDeltas = (dh, ds, dv);
            score = (hueWeight * dh + satWeight * ds + lumWeight * dv)
                  / (Math.Abs(hueWeight) + Math.Abs(satWeight) + Math.Abs(lumWeight));
        }

        (_h, _prevH) = (_prevH, _h);
        (_s, _prevS) = (_prevS, _s);
        (_v, _prevV) = (_prevV, _v);
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
    }

    public static double MeanPixelDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }
}
