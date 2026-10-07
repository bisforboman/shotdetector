using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        int i = Avx2.IsSupported ? ConvertAvx2(bgr, h, s, v) : 0;
        for (; i < n; i++)
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

    /// <summary>
    /// The same arithmetic 8 pixels at a time (AVX2; the division tables read with a gather). Returns how many pixels
    /// it converted; the caller does the rest. Reads 28 bytes from each group's start, so it stops 10 pixels early.
    /// </summary>
    static unsafe int ConvertAvx2(ReadOnlySpan<byte> bgr, Span<byte> h, Span<byte> s, Span<byte> v)
    {
        int n = h.Length, i = 0;
        // Bytes 0-3: B of 4 pixels, 4-7: G, 8-11: R (from 12 bytes of packed BGR).
        var split = Vector128.Create((byte)0, 3, 6, 9, 1, 4, 7, 10, 2, 5, 8, 11, 0x80, 0x80, 0x80, 0x80);
        var half = Vector256.Create(1 << (Shift - 1));
        var c180 = Vector256.Create(180);
        fixed (byte* src = bgr, ph = h, ps = s, pv = v)
        fixed (int* sdiv = SDiv, hdiv = HDiv)
        {
            for (; i + 10 <= n; i += 8)
            {
                var lo = Ssse3.Shuffle(Sse2.LoadVector128(src + 3 * i), split).AsUInt32();      // pixels 0-3
                var hi = Ssse3.Shuffle(Sse2.LoadVector128(src + 3 * i + 12), split).AsUInt32(); // pixels 4-7
                var bg = Sse2.UnpackLow(lo, hi).AsByte();   // B0-7, G0-7
                var rr = Sse2.UnpackHigh(lo, hi).AsByte();  // R0-7, (unused)
                var b = Avx2.ConvertToVector256Int32(bg);
                var g = Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(bg, 8));
                var r = Avx2.ConvertToVector256Int32(rr);
                var vmax = Vector256.Max(b, Vector256.Max(g, r));
                var diff = vmax - Vector256.Min(b, Vector256.Min(g, r));
                var vr = Vector256.Equals(vmax, r);
                var vg = Vector256.Equals(vmax, g);
                var sat = (diff * Avx2.GatherVector256(sdiv, vmax, 4) + half) >> Shift;
                var hue = (vr & (g - b)) + (~vr & ((vg & (b - r + diff + diff)) + (~vg & (r - g + (diff << 2)))));
                hue = (hue * Avx2.GatherVector256(hdiv, diff, 4) + half) >> Shift;
                hue += Vector256.LessThan(hue, Vector256<int>.Zero) & c180;
                *(ulong*)(ph + i) = Bytes(hue);
                *(ulong*)(ps + i) = Bytes(sat);
                *(ulong*)(pv + i) = Bytes(vmax);
            }
        }
        return i;
    }

    /// <summary>8 ints in 0..255 as 8 bytes.</summary>
    static ulong Bytes(Vector256<int> x)
    {
        var w = Vector256.Narrow(x, x);
        return Vector256.Narrow(w, w).AsUInt64().ToScalar();
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
