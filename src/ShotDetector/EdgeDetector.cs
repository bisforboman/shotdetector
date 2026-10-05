namespace ShotDetector;

/// <summary>
/// Edge map behind PySceneDetect's delta_edges (ContentDetector._detect_edges): Canny on the V
/// plane with thresholds at 2/3 and 4/3 of its median, then dilated with a k×k square so edges
/// overlap between frames. Canny is ported from OpenCV (imgproc/canny.cpp) for 8-bit input with
/// aperture 3 and L1 gradient, so the maps match cv2.Canny + cv2.dilate exactly.
/// </summary>
public sealed class EdgeDetector(int width, int height, int? kernelSize = null)
{
    public int KernelSize { get; } = kernelSize ?? EstimatedKernelSize(width, height);

    readonly short[] _dx = new short[width * height], _dy = new short[width * height];
    readonly int[] _mag = new int[width * height];
    readonly byte[] _map = new byte[width * height], _canny = new byte[width * height], _rowMax = new byte[width * height];
    readonly Stack<int> _stack = new();

    /// <summary>_estimated_kernel_size: 4 + round(sqrt(w * h) / 192), made odd.</summary>
    public static int EstimatedKernelSize(int width, int height)
    {
        int size = 4 + (int)Math.Round(Math.Sqrt(width * height) / 192);
        return size % 2 == 0 ? size + 1 : size;
    }

    /// <summary>Writes the edge map of <paramref name="lum"/> (255 = edge, 0 = not) to <paramref name="edges"/>.</summary>
    public void Detect(ReadOnlySpan<byte> lum, Span<byte> edges)
    {
        const double sigma = 1.0 / 3.0;
        double median = Median(lum);
        int low = (int)Math.Max(0, (1.0 - sigma) * median);
        int high = (int)Math.Min(255, (1.0 + sigma) * median);
        Canny(lum, low, high, _canny);
        Dilate(_canny, edges);
    }

    /// <summary>numpy.median of 8-bit values: the middle value, or the mean of the middle two.</summary>
    public static double Median(ReadOnlySpan<byte> values)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (byte v in values)
            histogram[v]++;
        int n = values.Length;
        return n % 2 == 1 ? Nth(histogram, n / 2) : (Nth(histogram, n / 2 - 1) + Nth(histogram, n / 2)) / 2.0;
    }

    static int Nth(ReadOnlySpan<int> histogram, int index)
    {
        for (int v = 0; v < 256; v++)
            if ((index -= histogram[v]) < 0)
                return v;
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>cv2.Canny(src, low, high) (aperture 3, L1 gradient): 255 = edge, 0 = not.</summary>
    public void Canny(ReadOnlySpan<byte> src, int low, int high, Span<byte> output)
    {
        int w = width, h = height;
        // 3x3 Sobel (CV_16S) with BORDER_REPLICATE, and the L1 magnitude.
        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, mid = y * w, down = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, w - 1);
                int dx = (src[up + r] + 2 * src[mid + r] + src[down + r]) - (src[up + l] + 2 * src[mid + l] + src[down + l]);
                int dy = (src[down + l] + 2 * src[down + x] + src[down + r]) - (src[up + l] + 2 * src[up + x] + src[up + r]);
                _dx[mid + x] = (short)dx;
                _dy[mid + x] = (short)dy;
                _mag[mid + x] = Math.Abs(dx) + Math.Abs(dy);
            }
        }

        // Non-maximum suppression along the gradient direction, using OpenCV's fixed-point tangent
        // test. Neighbours outside the image have magnitude 0. Map: 0 = no, 1 = weak, 2 = edge.
        const int shift = 15;
        const int tg22 = 13573; // (int)(tan(22.5°) * (1 << 15) + 0.5)
        int Mag(int x, int y) => x < 0 || x >= w || y < 0 || y >= h ? 0 : _mag[y * w + x];
        _stack.Clear();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, m = _mag[i];
                _map[i] = 0;
                if (m <= low)
                    continue;
                int xs = _dx[i], ys = _dy[i];
                int ax = Math.Abs(xs), ay = Math.Abs(ys) << shift;
                int tg22x = ax * tg22;
                bool isMax;
                if (ay < tg22x)
                    isMax = m > Mag(x - 1, y) && m >= Mag(x + 1, y);
                else if (ay > tg22x + (ax << (shift + 1)))
                    isMax = m > Mag(x, y - 1) && m >= Mag(x, y + 1);
                else
                {
                    int s = (xs ^ ys) < 0 ? -1 : 1;
                    isMax = m > Mag(x - s, y - 1) && m > Mag(x + s, y + 1);
                }
                if (!isMax)
                    continue;
                if (m > high)
                {
                    _map[i] = 2;
                    _stack.Push(i);
                }
                else
                    _map[i] = 1;
            }
        }

        // Hysteresis: weak pixels 8-connected to an edge become edges.
        while (_stack.TryPop(out int i))
        {
            int x = i % w, y = i / w;
            for (int ny = Math.Max(y - 1, 0); ny <= Math.Min(y + 1, h - 1); ny++)
                for (int nx = Math.Max(x - 1, 0); nx <= Math.Min(x + 1, w - 1); nx++)
                {
                    int j = ny * w + nx;
                    if (_map[j] == 1)
                    {
                        _map[j] = 2;
                        _stack.Push(j);
                    }
                }
        }
        for (int i = 0; i < output.Length; i++)
            output[i] = _map[i] == 2 ? (byte)255 : (byte)0;
    }

    /// <summary>cv2.dilate with a k×k square of ones: the maximum over the window, ignoring pixels outside.</summary>
    public void Dilate(ReadOnlySpan<byte> src, Span<byte> edges)
    {
        int w = width, h = height, r = KernelSize / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte max = 0;
                for (int k = Math.Max(x - r, 0); k <= Math.Min(x + r, w - 1) && max < 255; k++)
                    max = Math.Max(max, src[y * w + k]);
                _rowMax[y * w + x] = max;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte max = 0;
                for (int k = Math.Max(y - r, 0); k <= Math.Min(y + r, h - 1) && max < 255; k++)
                    max = Math.Max(max, _rowMax[k * w + x]);
                edges[y * w + x] = max;
            }
    }
}
