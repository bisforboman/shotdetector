namespace ShotDetector;

/// <summary>
/// BGR → HSV for 8-bit pixels, ported from OpenCV's RGB2HSV_b (imgproc/color_hsv.simd.hpp) so the
/// output matches cv2.cvtColor(img, COLOR_BGR2HSV) bit for bit: H in [0,180), S and V in [0,255].
/// OpenCV uses fixed-point division tables instead of floating point.
/// </summary>
internal static class Hsv
{
    const int Shift = 12;
    static readonly int[] SDiv = new int[256];
    static readonly int[] HDiv = new int[256];

    static Hsv()
    {
        for (int i = 1; i < 256; i++)
        {
            // saturate_cast<int>(double) rounds half to even, same as Math.Round's default.
            SDiv[i] = (int)Math.Round((255 << Shift) / (double)i);
            HDiv[i] = (int)Math.Round((180 << Shift) / (6.0 * i));
        }
    }

    /// <summary><see cref="FromBgr"/> for a run of packed BGR pixels, into separate H, S and V planes.</summary>
    public static void Convert(ReadOnlySpan<byte> bgr, Span<byte> h, Span<byte> s, Span<byte> v)
    {
        int n = h.Length;
        if (bgr.Length < 3 * n || s.Length < n || v.Length < n)
            throw new ArgumentException("Planes and pixels don't match.");
        ref byte src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(bgr);
        ref byte ph = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(h);
        ref byte ps = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s);
        ref byte pv = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(v);
        ref int sdiv = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(SDiv);
        ref int hdiv = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(HDiv);
        for (int i = 0; i < n; i++)
        {
            int b = System.Runtime.CompilerServices.Unsafe.Add(ref src, 3 * i);
            int g = System.Runtime.CompilerServices.Unsafe.Add(ref src, 3 * i + 1);
            int r = System.Runtime.CompilerServices.Unsafe.Add(ref src, 3 * i + 2);
            int vmax = Math.Max(b, Math.Max(g, r));
            int diff = vmax - Math.Min(b, Math.Min(g, r));
            int vr = vmax == r ? -1 : 0;
            int vg = vmax == g ? -1 : 0;
            int sat = (diff * System.Runtime.CompilerServices.Unsafe.Add(ref sdiv, vmax) + (1 << (Shift - 1))) >> Shift;
            int hue = (vr & (g - b)) + (~vr & ((vg & (b - r + 2 * diff)) + (~vg & (r - g + 4 * diff))));
            hue = (hue * System.Runtime.CompilerServices.Unsafe.Add(ref hdiv, diff) + (1 << (Shift - 1))) >> Shift;
            if (hue < 0) hue += 180;
            System.Runtime.CompilerServices.Unsafe.Add(ref ph, i) = (byte)hue;
            System.Runtime.CompilerServices.Unsafe.Add(ref ps, i) = (byte)sat;
            System.Runtime.CompilerServices.Unsafe.Add(ref pv, i) = (byte)vmax;
        }
    }

    public static (byte H, byte S, byte V) FromBgr(byte b, byte g, byte r)
    {
        int v = Math.Max(b, Math.Max(g, r));
        int vmin = Math.Min(b, Math.Min(g, r));
        int diff = v - vmin;
        int vr = v == r ? -1 : 0;
        int vg = v == g ? -1 : 0;

        int s = (diff * SDiv[v] + (1 << (Shift - 1))) >> Shift;
        int h = (vr & (g - b)) + (~vr & ((vg & (b - r + 2 * diff)) + (~vg & (r - g + 4 * diff))));
        h = (h * HDiv[diff] + (1 << (Shift - 1))) >> Shift;
        if (h < 0) h += 180;
        return ((byte)h, (byte)s, (byte)v);
    }
}
