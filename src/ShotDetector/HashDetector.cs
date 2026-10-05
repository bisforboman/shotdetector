namespace ShotDetector;

/// <summary>
/// Port of PySceneDetect 0.7.1's HashDetector (detect-hash): a perceptual hash of each frame
/// (grayscale, shrunk to size×lowpass with INTER_AREA, scaled by its maximum, DCT, the low size×size
/// coefficients compared with their median) and a cut when the hash differs from the previous frame's
/// in at least threshold of its bits.
/// Bit-exact with OpenCV for the grayscale conversion and the INTER_AREA resize. The DCT is computed
/// in double precision rather than cv2.dct's float32, so a hash bit can differ only when a coefficient
/// ties with the median to within ~1e-6 (not seen in 512,000 bits tested).
/// </summary>
public sealed class HashDetector(
    Func<int, FrameTime> position,
    double threshold = 0.395,
    int size = 16,
    int lowpass = 2,
    int minSceneLen = 15) : IDetector
{
    readonly string _key = $"hash_dist [size={size} lowpass={lowpass}]";
    readonly int _n = size * lowpass;
    readonly double[,] _cos = CosineTable(size * lowpass, size);
    bool[]? _lastHash;
    int? _lastCut;
    int _width, _height;
    byte[] _gray = [], _small = [];
    float[] _norm = [];
    double[] _tmp = [];
    float[] _low = [];

    /// <inheritdoc/>
    public Stats? Stats { get; set; }

    /// <inheritdoc/>
    public FrameTime? ProcessFrame(int frame, byte[] bgr)
    {
        _lastCut ??= frame;
        if (_width == 0)
            throw new InvalidOperationException("Call SetFrameSize first.");
        var hash = Hash(bgr);
        FrameTime? cut = null;
        if (_lastHash is not null)
        {
            int distance = 0;
            for (int i = 0; i < hash.Length; i++)
                if (hash[i] != _lastHash[i]) distance++;
            double normalized = distance / (double)(size * size);
            Stats?.Set(frame, _key, normalized);
            if (normalized >= threshold && position(frame).Minus(position(_lastCut.Value)).FrameNum >= minSceneLen)
            {
                cut = position(frame);
                _lastCut = frame;
            }
        }
        _lastHash = hash;
        return cut;
    }

    /// <summary>The frames' size, needed to shrink them.</summary>
    public void SetFrameSize(int width, int height)
    {
        (_width, _height) = (width, height);
        _gray = new byte[width * height];
        _small = new byte[_n * _n];
        _norm = new float[_n * _n];
        _tmp = new double[_n * size];
        _low = new float[size * size];
    }

    /// <summary>hash_frame: size×size bits.</summary>
    public bool[] Hash(ReadOnlySpan<byte> bgr)
    {
        Gray(bgr, _gray);
        AreaResize.Resize(_gray, _width, _height, _small, _n, _n);
        // numpy.float32(img) / max: float32 division (max 0 → 1).
        int max = 0;
        foreach (byte v in _small)
            max = Math.Max(max, v);
        float divisor = Math.Max(max, 1);
        for (int i = 0; i < _norm.Length; i++)
            _norm[i] = _small[i] / divisor;

        // Low size×size block of the 2D DCT-II (orthonormal, like cv2.dct): C_low · x · C_lowᵀ.
        int n = _n;
        for (int y = 0; y < n; y++)
            for (int k = 0; k < size; k++)
            {
                double sum = 0;
                for (int x = 0; x < n; x++)
                    sum += _norm[y * n + x] * _cos[k, x];
                _tmp[y * size + k] = sum;
            }
        for (int j = 0; j < size; j++)
            for (int k = 0; k < size; k++)
            {
                double sum = 0;
                for (int y = 0; y < n; y++)
                    sum += _cos[j, y] * _tmp[y * size + k];
                _low[j * size + k] = (float)sum;
            }

        // numpy.median of float32: mean of the two middle values, in float32.
        var sorted = (float[])_low.Clone();
        Array.Sort(sorted);
        int half = sorted.Length / 2;
        float median = sorted.Length % 2 == 1 ? sorted[half] : (sorted[half - 1] + sorted[half]) / 2f;
        var bits = new bool[_low.Length];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = _low[i] > median;
        return bits;
    }

    /// <summary>cv2.cvtColor(BGR2GRAY) in OpenCV 5: 15-bit fixed point (exact for all 2^24 colours).</summary>
    public static void Gray(ReadOnlySpan<byte> bgr, Span<byte> gray)
    {
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
            gray[i] = (byte)((bgr[p] * 3735 + bgr[p + 1] * 19235 + bgr[p + 2] * 9798 + 16384) >> 15);
    }

    /// <summary>DCT-II basis, rows k &lt; <paramref name="rows"/>: sqrt(2/N)·cos(π(2x+1)k/2N), row 0 scaled by 1/√2.</summary>
    static double[,] CosineTable(int n, int rows)
    {
        var c = new double[rows, n];
        for (int k = 0; k < rows; k++)
            for (int x = 0; x < n; x++)
                c[k, x] = Math.Sqrt(2.0 / n) * (k == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos(Math.PI * (2 * x + 1) * k / (2.0 * n));
        return c;
    }
}

/// <summary>
/// Port of cv2.resize(INTER_AREA) for single-channel 8-bit images (imgproc/resize.cpp, resizeArea_):
/// each destination pixel is the area-weighted mean of the source pixels it covers, accumulated in
/// float32 in OpenCV's order, then rounded half to even.
/// </summary>
internal static class AreaResize
{
    public static void Resize(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        if (sw % dw == 0 && sh % dh == 0)
        {
            ResizeFast(src, sw, dst, dw, dh, sw / dw, sh / dh);
            return;
        }
        var xtab = Table(sw, dw);
        var ytab = Table(sh, dh);
        Span<float> acc = stackalloc float[dw];
        Span<float> buf = stackalloc float[dw];
        int currentDy = ytab[0].Di;
        foreach (var (dy, sy, beta) in ytab)
        {
            if (dy != currentDy)
            {
                Store(acc, dst.Slice(currentDy * dw, dw));
                acc.Clear();
                currentDy = dy;
            }
            buf.Clear();
            var row = src.Slice(sy * sw, sw);
            foreach (var (dx, sx, alpha) in xtab)
                buf[dx] += row[sx] * alpha;
            for (int dx = 0; dx < dw; dx++)
                acc[dx] += buf[dx] * beta;
        }
        Store(acc, dst.Slice(currentDy * dw, dw));
    }

    /// <summary>
    /// resizeAreaFast_, used when both scales are whole numbers: the block's integer sum times
    /// 1/area in float32, rounded; 2×2 blocks use (sum + 2) >> 2 (ResizeAreaFastVec).
    /// </summary>
    static void ResizeFast(ReadOnlySpan<byte> src, int sw, Span<byte> dst, int dw, int dh, int scaleX, int scaleY)
    {
        float scale = 1f / (scaleX * scaleY);
        for (int dy = 0; dy < dh; dy++)
            for (int dx = 0; dx < dw; dx++)
            {
                int sum = 0;
                for (int y = dy * scaleY; y < (dy + 1) * scaleY; y++)
                    for (int x = dx * scaleX; x < (dx + 1) * scaleX; x++)
                        sum += src[y * sw + x];
                dst[dy * dw + dx] = scaleX == 2 && scaleY == 2
                    ? (byte)((sum + 2) >> 2)
                    : (byte)Math.Clamp(Math.Round(sum * scale, MidpointRounding.ToEven), 0, 255);
            }
    }

    static void Store(ReadOnlySpan<float> acc, Span<byte> row)
    {
        for (int i = 0; i < row.Length; i++)
            row[i] = (byte)Math.Clamp(Math.Round(acc[i], MidpointRounding.ToEven), 0, 255);
    }

    /// <summary>computeResizeAreaTab: (destination index, source index, weight) for one axis.</summary>
    static List<(int Di, int Si, float Weight)> Table(int ssize, int dsize)
    {
        double scale = ssize / (double)dsize;
        var tab = new List<(int, int, float)>();
        for (int dx = 0; dx < dsize; dx++)
        {
            double fsx1 = dx * scale, fsx2 = fsx1 + scale;
            double cell = Math.Min(scale, ssize - fsx1);
            int sx1 = (int)Math.Ceiling(fsx1), sx2 = (int)Math.Floor(fsx2);
            sx2 = Math.Min(sx2, ssize - 1);
            sx1 = Math.Min(sx1, sx2);
            if (sx1 - fsx1 > 1e-3)
                tab.Add((dx, sx1 - 1, (float)((sx1 - fsx1) / cell)));
            for (int sx = sx1; sx < sx2; sx++)
                tab.Add((dx, sx, (float)(1.0 / cell)));
            if (fsx2 - sx2 > 1e-3)
                tab.Add((dx, sx2, (float)(Math.Min(Math.Min(fsx2 - sx2, 1.0), cell) / cell)));
        }
        return tab;
    }
}
