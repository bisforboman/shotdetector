using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
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
        int i = Avx2.IsSupported ? ConvertAvx2(bgr, h, s, v) : AdvSimd.Arm64.IsSupported ? ConvertNeon(bgr, h, s, v) : 0;
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

    /// <summary>
    /// The same arithmetic 16 pixels at a time on ARM: one de-interleaving load gives B, G and R, max/min/difference
    /// run on bytes, and the rest on four 4-lane int vectors. NEON can't gather, so the two table reads are scalar.
    /// Returns how many pixels it converted.
    /// </summary>
    static unsafe int ConvertNeon(ReadOnlySpan<byte> bgr, Span<byte> h, Span<byte> s, Span<byte> v)
    {
        int n = h.Length, i = 0;
        int* sd = stackalloc int[16], hd = stackalloc int[16];
        byte* vm = stackalloc byte[16], df = stackalloc byte[16];
        var half = Vector128.Create(1 << (Shift - 1));
        var c180 = Vector128.Create(180);
        fixed (byte* src = bgr, ph = h, ps = s, pv = v)
        fixed (int* sdiv = SDiv, hdiv = HDiv)
        {
            for (; i + 16 <= n; i += 16)
            {
                var (b8, g8, r8) = AdvSimd.Arm64.Load3xVector128AndUnzip(src + 3 * i);
                var vmax8 = Vector128.Max(b8, Vector128.Max(g8, r8));
                var diff8 = vmax8 - Vector128.Min(b8, Vector128.Min(g8, r8));
                vmax8.Store(pv + i);
                vmax8.Store(vm);
                diff8.Store(df);
                for (int k = 0; k < 16; k++)
                {
                    sd[k] = sdiv[vm[k]];
                    hd[k] = hdiv[df[k]];
                }
                var (bl, bh) = Vector128.Widen(b8);
                var (gl, gh) = Vector128.Widen(g8);
                var (rl, rh) = Vector128.Widen(r8);
                var (ml, mh) = Vector128.Widen(vmax8);
                var (dl, dh) = Vector128.Widen(diff8);
                Vector128<int> S0, S1, S2, S3, H0, H1, H2, H3;
                Quarter(Vector128.WidenLower(bl), Vector128.WidenLower(gl), Vector128.WidenLower(rl), Vector128.WidenLower(ml), Vector128.WidenLower(dl), sd, hd, out S0, out H0);
                Quarter(Vector128.WidenUpper(bl), Vector128.WidenUpper(gl), Vector128.WidenUpper(rl), Vector128.WidenUpper(ml), Vector128.WidenUpper(dl), sd + 4, hd + 4, out S1, out H1);
                Quarter(Vector128.WidenLower(bh), Vector128.WidenLower(gh), Vector128.WidenLower(rh), Vector128.WidenLower(mh), Vector128.WidenLower(dh), sd + 8, hd + 8, out S2, out H2);
                Quarter(Vector128.WidenUpper(bh), Vector128.WidenUpper(gh), Vector128.WidenUpper(rh), Vector128.WidenUpper(mh), Vector128.WidenUpper(dh), sd + 12, hd + 12, out S3, out H3);
                // All in 0..255 (hue 0..179), so narrowing keeps every value.
                Vector128.Narrow(Vector128.Narrow(S0, S1), Vector128.Narrow(S2, S3)).AsByte().Store(ps + i);
                Vector128.Narrow(Vector128.Narrow(H0, H1), Vector128.Narrow(H2, H3)).AsByte().Store(ph + i);
            }
        }
        return i;

        void Quarter(Vector128<uint> bu, Vector128<uint> gu, Vector128<uint> ru, Vector128<uint> mu, Vector128<uint> du, int* sdq, int* hdq,
            out Vector128<int> sat, out Vector128<int> hue)
        {
            Vector128<int> b = bu.AsInt32(), g = gu.AsInt32(), r = ru.AsInt32(), vmax = mu.AsInt32(), diff = du.AsInt32();
            var vr = Vector128.Equals(vmax, r);
            var vg = Vector128.Equals(vmax, g);
            sat = (diff * Vector128.Load(sdq) + half) >> Shift;
            var x = (vr & (g - b)) + (~vr & ((vg & (b - r + diff + diff)) + (~vg & (r - g + (diff << 2)))));
            x = (x * Vector128.Load(hdq) + half) >> Shift;
            hue = x + (Vector128.LessThan(x, Vector128<int>.Zero) & c180);
        }
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
