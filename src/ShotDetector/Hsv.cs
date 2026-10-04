namespace ShotDetector;

/// <summary>
/// BGR → HSV for 8-bit pixels, ported from OpenCV's RGB2HSV_b (imgproc/color_hsv.simd.hpp) so the
/// output matches cv2.cvtColor(img, COLOR_BGR2HSV) bit for bit: H in [0,180), S and V in [0,255].
/// OpenCV uses fixed-point division tables instead of floating point.
/// </summary>
public static class Hsv
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

    /// <summary>Converts a packed bgr24 frame into three planar channels.</summary>
    public static void Convert(ReadOnlySpan<byte> bgr, Span<byte> h, Span<byte> s, Span<byte> v)
    {
        for (int i = 0, p = 0; i < h.Length; i++, p += 3)
            (h[i], s[i], v[i]) = FromBgr(bgr[p], bgr[p + 1], bgr[p + 2]);
    }
}
