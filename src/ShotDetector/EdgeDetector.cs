using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ShotDetector;

/// <summary>
/// Edge map behind PySceneDetect's delta_edges (ContentDetector._detect_edges): Canny on the V
/// plane with thresholds at 2/3 and 4/3 of its median, then dilated with a k×k square so edges
/// overlap between frames. Canny is ported from OpenCV (imgproc/canny.cpp) for 8-bit input with
/// aperture 3 and L1 gradient, so the maps match cv2.Canny + cv2.dilate exactly.
/// </summary>
internal sealed class EdgeDetector(int width, int height, int? kernelSize = null)
{
    /// <summary>Size of the square the edges are dilated with.</summary>
    public int KernelSize { get; } = kernelSize ?? EstimatedKernelSize(width, height);

    readonly short[] _dx = new short[width * height], _dy = new short[width * height];

    // The magnitudes with a border of zeros (a pixel outside the image has magnitude 0 to the suppression).
    readonly int[] _mag = new int[(width + 2) * (height + 2)];
    readonly byte[] _canny = new byte[width * height], _rowMax = new byte[width * height];

    // The suppression's map (0 = no, 1 = weak, 2 = edge) with a border of zeros, so the hysteresis's neighbours need no
    // clamping; laid out as the magnitudes are.
    readonly byte[] _map = new byte[(width + 2) * (height + 2)];

    // One row with a border of zeros either side, for the dilation's window.
    readonly byte[] _padded = new byte[width + 2 * (kernelSize ?? EstimatedKernelSize(width, height))];
    readonly Stack<int> _stack = new();

    /// <summary>_estimated_kernel_size: 4 + round(sqrt(w * h) / 192), made odd.</summary>
    internal static int EstimatedKernelSize(int width, int height)
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
    internal static double Median(ReadOnlySpan<byte> values)
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
        {
            if ((index -= histogram[v]) < 0)
                return v;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>cv2.Canny(src, low, high) (aperture 3, L1 gradient): 255 = edge, 0 = not.</summary>
    internal void Canny(ReadOnlySpan<byte> src, int low, int high, Span<byte> output)
    {
        int w = width, h = height;

        // 3x3 Sobel (CV_16S) with BORDER_REPLICATE, and the L1 magnitude.
        int pw = w + 2;
        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, mid = y * w, down = Math.Min(y + 1, h - 1) * w;

            // The interior 8 at a time where it can; the rest, and column 0, one at a time.
            bool vector = Vector128.IsHardwareAccelerated && w >= 10;
            int x = vector ? SobelRow(src, up, mid, down, w, y) : 0;
            for (; x < w; x++)
                SobelPixel(src, up, mid, down, w, x, y);
            if (vector)
                SobelPixel(src, up, mid, down, w, 0, y);
        }

        // Non-maximum suppression along the gradient direction, using OpenCV's fixed-point tangent
        // test. Neighbours outside the image have magnitude 0. Map: 0 = no, 1 = weak, 2 = edge.
        const int shift = 15;
        const int tg22 = 13573; // (int)(tan(22.5°) * (1 << 15) + 0.5)
        _stack.Clear();
        Array.Clear(_map);
        ref int mag0 = ref _mag[0];
        for (int y = 0; y < h; y++)
        {
            // This row's magnitudes (and map) start at row y + 1, column 1 of the bordered arrays; a neighbour is at +-1
            // and +-pw.
            ref int row = ref Unsafe.Add(ref mag0, (y + 1) * pw + 1);
            int x = Vector256.IsHardwareAccelerated ? SuppressRow(ref row, y, low, high) : 0;
            for (; x < w; x++)
            {
                int i = y * w + x, m = Unsafe.Add(ref row, x);
                if (m <= low)
                    continue;
                int xs = _dx[i], ys = _dy[i];
                int ax = Math.Abs(xs), ay = Math.Abs(ys) << shift;
                int tg22x = ax * tg22;
                bool isMax;
                ref int at = ref Unsafe.Add(ref row, x);
                if (ay < tg22x)
                {
                    isMax = m > Unsafe.Add(ref at, -1) && m >= Unsafe.Add(ref at, 1);
                }
                else if (ay > tg22x + (ax << (shift + 1)))
                {
                    isMax = m > Unsafe.Add(ref at, -pw) && m >= Unsafe.Add(ref at, pw);
                }
                else
                {
                    int s = (xs ^ ys) < 0 ? -1 : 1;
                    isMax = m > Unsafe.Add(ref at, -pw - s) && m > Unsafe.Add(ref at, pw + s);
                }

                if (!isMax)
                    continue;
                int p = (y + 1) * pw + x + 1;
                if (m > high)
                {
                    _map[p] = 2;
                    _stack.Push(p);
                }
                else
                {
                    _map[p] = 1;
                }
            }
        }

        // Hysteresis: weak pixels 8-connected to an edge become edges (the border is never weak).
        ref byte map = ref _map[0];
        while (_stack.TryPop(out int p))
        {
            foreach (int j in (ReadOnlySpan<int>)[p - pw - 1, p - pw, p - pw + 1, p - 1, p + 1, p + pw - 1, p + pw, p + pw + 1])
            {
                if (Unsafe.Add(ref map, j) == 1)
                {
                    Unsafe.Add(ref map, j) = 2;
                    _stack.Push(j);
                }
            }
        }

        // Edges (2) to 255, the rest to 0, row by row out of the bordered map: 16 at a time, then one by one.
        ref byte dst = ref MemoryMarshal.GetReference(output);
        var two = Vector128.Create((byte)2);
        for (int y = 0; y < h; y++)
        {
            ref byte from = ref Unsafe.Add(ref map, (y + 1) * pw + 1);
            ref byte to = ref Unsafe.Add(ref dst, y * w);
            int o = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; o <= w - 16; o += 16)
                    Vector128.Equals(Vector128.LoadUnsafe(ref from, (nuint)o), two).StoreUnsafe(ref to, (nuint)o);
            }

            for (; o < w; o++)
                Unsafe.Add(ref to, o) = Unsafe.Add(ref from, o) == 2 ? (byte)255 : (byte)0;
        }
    }

    /// <summary>
    /// The suppression for row <paramref name="y"/>, 8 pixels at a time (while x + 8 &lt;= width): OpenCV's tangent test and
    /// neighbour comparisons for every lane, the scalar loop's integers, then each lane's outcome where its magnitude is
    /// above <paramref name="low"/>. Strong pixels go on the stack in order. Returns where it stopped.
    /// </summary>
    int SuppressRow(ref int row, int y, int low, int high)
    {
        const int shift = 15, tg22 = 13573;
        int w = width, pw = w + 2, x = 0;
        var lowV = Vector256.Create(low);
        var highV = Vector256.Create(high);
        var tg = Vector256.Create(tg22);
        var one = Vector256.Create(1);
        ref short rdx = ref _dx[0];
        ref short rdy = ref _dy[0];
        ref byte map = ref _map[0];
        for (; x + 8 <= w; x += 8)
        {
            ref int at = ref Unsafe.Add(ref row, x);
            var m = Vector256.LoadUnsafe(ref at);
            var candidate = Vector256.GreaterThan(m, lowV);
            if (candidate == Vector256<int>.Zero)
                continue;
            int i = y * w + x;
            var xs = Widen(Vector128.LoadUnsafe(ref rdx, (nuint)i));
            var ys = Widen(Vector128.LoadUnsafe(ref rdy, (nuint)i));
            var ax = Vector256.Abs(xs);
            var ay = Vector256.Abs(ys) << shift;
            var tg22x = ax * tg;
            var horizontal = Vector256.LessThan(ay, tg22x);
            var vertical = Vector256.GreaterThan(ay, tg22x + (ax << (shift + 1)));
            var byRow = Vector256.GreaterThan(m, Load(ref at, -1)) & Vector256.GreaterThanOrEqual(m, Load(ref at, 1));
            var byColumn = Vector256.GreaterThan(m, Load(ref at, -pw)) & Vector256.GreaterThanOrEqual(m, Load(ref at, pw));
            var down = Vector256.GreaterThan(m, Load(ref at, -pw - 1)) & Vector256.GreaterThan(m, Load(ref at, pw + 1));   // s = 1
            var up = Vector256.GreaterThan(m, Load(ref at, -pw + 1)) & Vector256.GreaterThan(m, Load(ref at, pw - 1));     // s = -1
            var diagonal = Vector256.ConditionalSelect(Vector256.LessThan(xs ^ ys, Vector256<int>.Zero), up, down);
            var isMax = candidate & Vector256.ConditionalSelect(horizontal, byRow, Vector256.ConditionalSelect(vertical, byColumn, diagonal));
            if (isMax == Vector256<int>.Zero)
                continue;
            var label = (isMax & one) + (isMax & Vector256.GreaterThan(m, highV) & one);
            int p = (y + 1) * pw + x + 1;
            for (int k = 0; k < 8; k++)
            {
                int l = label.GetElement(k);
                if (l == 0)
                    continue;
                Unsafe.Add(ref map, p + k) = (byte)l;
                if (l == 2)
                    _stack.Push(p + k);
            }
        }

        return x;
    }

    static Vector256<int> Load(ref int at, int offset) => Vector256.LoadUnsafe(ref Unsafe.Add(ref at, offset));

    static Vector256<int> Widen(Vector128<short> v) => Vector256.Create(Vector128.WidenLower(v), Vector128.WidenUpper(v));

    /// <summary>cv2.dilate with a k×k square of ones: the maximum over the window, ignoring pixels outside.</summary>
    internal void Dilate(ReadOnlySpan<byte> src, Span<byte> edges)
    {
        // Pixels outside are left out of the maximum, which is what zeros give (bytes are never below 0).
        int w = width, h = height, r = KernelSize / 2, k = 2 * r + 1;
        var padded = _padded.AsSpan(0, w + 2 * r);
        for (int y = 0; y < h; y++)
        {
            src.Slice(y * w, w).CopyTo(padded[r..]);
            WindowMax(padded, k, _rowMax.AsSpan(y * w, w));
        }

        for (int y = 0; y < h; y++)
        {
            var row = edges.Slice(y * w, w);
            int y0 = Math.Max(y - r, 0), y1 = Math.Min(y + r, h - 1);
            _rowMax.AsSpan(y0 * w, w).CopyTo(row);
            for (int yy = y0 + 1; yy <= y1; yy++)
                Max(row, _rowMax.AsSpan(yy * w, w));
        }
    }

    /// <summary>output[x] = the maximum of padded[x .. x + k - 1].</summary>
    static void WindowMax(ReadOnlySpan<byte> padded, int k, Span<byte> output)
    {
        padded[..output.Length].CopyTo(output);
        for (int j = 1; j < k; j++)
            Max(output, padded.Slice(j, output.Length));
    }

    /// <summary>a[i] = max(a[i], b[i]), 16 at a time.</summary>
    static void Max(Span<byte> a, ReadOnlySpan<byte> b)
    {
        int i = 0;
        ref byte ra = ref MemoryMarshal.GetReference(a);
        ref byte rb = ref MemoryMarshal.GetReference(b);
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= a.Length - 16; i += 16)
                Vector128.Max(Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref ra, (nuint)i);
        }

        for (; i < a.Length; i++)
            Unsafe.Add(ref ra, i) = Math.Max(Unsafe.Add(ref ra, i), Unsafe.Add(ref rb, i));
    }

    /// <summary>One pixel's Sobel derivatives and magnitude, its neighbours replicated at the border.</summary>
    void SobelPixel(ReadOnlySpan<byte> src, int up, int mid, int down, int w, int x, int y)
    {
        int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, w - 1);
        int dx = (src[up + r] + 2 * src[mid + r] + src[down + r]) - (src[up + l] + 2 * src[mid + l] + src[down + l]);
        int dy = (src[down + l] + 2 * src[down + x] + src[down + r]) - (src[up + l] + 2 * src[up + x] + src[up + r]);
        _dx[mid + x] = (short)dx;
        _dy[mid + x] = (short)dy;
        _mag[(y + 1) * (w + 2) + x + 1] = Math.Abs(dx) + Math.Abs(dy);
    }

    /// <summary>
    /// A row's interior (x from 1, while x + 8 &lt;= w - 1) 8 pixels at a time, in 16-bit words: the derivatives are
    /// at most 4 * 255 either way and the magnitude 8 * 255, so nothing overflows. Returns where it stopped.
    /// </summary>
    int SobelRow(ReadOnlySpan<byte> src, int up, int mid, int down, int w, int y)
    {
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref short rdx = ref _dx[0];
        ref short rdy = ref _dy[0];
        ref int rmag = ref _mag[0];
        int magRow = (y + 1) * (w + 2) + 1;
        int x = 1;
        for (; x + 8 <= w - 1; x += 8)
        {
            var ul = Load8(ref s, up + x - 1);
            var uc = Load8(ref s, up + x);
            var ur = Load8(ref s, up + x + 1);
            var ml = Load8(ref s, mid + x - 1);
            var mr = Load8(ref s, mid + x + 1);
            var dl = Load8(ref s, down + x - 1);
            var dc = Load8(ref s, down + x);
            var dr = Load8(ref s, down + x + 1);
            var dx = (ur + mr + mr + dr) - (ul + ml + ml + dl);
            var dy = (dl + dc + dc + dr) - (ul + uc + uc + ur);
            dx.StoreUnsafe(ref rdx, (nuint)(mid + x));
            dy.StoreUnsafe(ref rdy, (nuint)(mid + x));
            var mag = Vector128.Abs(dx) + Vector128.Abs(dy);
            var (lo, hi) = Vector128.Widen(mag);
            lo.StoreUnsafe(ref rmag, (nuint)(magRow + x));
            hi.StoreUnsafe(ref rmag, (nuint)(magRow + x + 4));
        }

        return x;
    }

    /// <summary>The 8 bytes at <paramref name="at"/> as 16-bit words (reads those 8 only).</summary>
    static Vector128<short> Load8(ref byte s, int at) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, at))).AsByte()).AsInt16();
}
