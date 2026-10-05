namespace ShotDetector;

/// <summary>
/// yuv420p → BGR exactly as OpenCV gets it from swscale: for even-height yuv420p at the same size,
/// swscale uses its unscaled converter, on x86 ff_yuv_420_bgr24_ssse3 (libswscale/x86/yuv_2_rgb.asm)
/// with the BT.601 limited-range coefficients from ff_yuv2rgb_c_init_tables. Chroma is not
/// interpolated: each 2x2 block shares one U and V. Verified against ffmpeg for all 2^24 (Y, U, V).
/// Lets ffmpeg skip colour conversion and lets us convert only the pixels cv2.resize reads.
/// </summary>
internal static class Yuv420
{
    // Each term of the asm is a 16-bit multiply-high of one shifted input, so precompute them per value.
    static readonly int[] YTerm = new int[256], UbTerm = new int[256], UgTerm = new int[256], VrTerm = new int[256], VgTerm = new int[256];

    const int ClipOffset = 1024;
    static readonly byte[] Clip = Enumerable.Range(-ClipOffset, 2 * ClipOffset).Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();

    static Yuv420()
    {
        const int yCoeff = 9539, yOffset = 128, vrCoeff = 13075, ubCoeff = 16525, ugCoeff = -3209, vgCoeff = -6660;
        for (int i = 0; i < 256; i++)
        {
            int y = (short)((i << 3) - yOffset), c = Sat16((i << 3) - 1024);
            YTerm[i] = MulHi(y, yCoeff);
            UbTerm[i] = MulHi(c, ubCoeff);
            UgTerm[i] = MulHi(c, ugCoeff);
            VrTerm[i] = MulHi(c, vrCoeff);
            VgTerm[i] = MulHi(c, vgCoeff);
        }
    }

    static int Sat16(int v) => Math.Clamp(v, short.MinValue, short.MaxValue);
    static int MulHi(int a, int b) => (short)a * (short)b >> 16; // pmulhw

    /// <summary>Frame size in bytes of a tightly packed yuv420p frame (ffmpeg rawvideo).</summary>
    public static int FrameSize(int width, int height) => width * height + 2 * ((width + 1) / 2) * ((height + 1) / 2);

    /// <summary>
    /// Converts the pixels at <paramref name="cols"/> of row <paramref name="y"/> of a packed yuv420p
    /// frame into <paramref name="bgrRow"/> (a packed BGR row); other pixels are left untouched.
    /// </summary>
    public static void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow)
    {
        int chromaWidth = (width + 1) / 2, chromaSize = chromaWidth * ((height + 1) / 2);
        var luma = yuv.Slice(y * width, width);
        var u = yuv.Slice(width * height + (y >> 1) * chromaWidth, chromaWidth);
        var v = yuv.Slice(width * height + chromaSize + (y >> 1) * chromaWidth, chromaWidth);
        // The asm saturates to 16 bits after each add, but every term here is within ±300, so
        // saturation never triggers and plain adds are exact; the final clamp to 0..255 is a lookup.
        var clip = Clip;
        foreach (int x in cols)
        {
            int yy = YTerm[luma[x]] + ClipOffset;
            int U = u[x >> 1], V = v[x >> 1];
            int o = x * 3;
            bgrRow[o] = clip[UbTerm[U] + yy];
            bgrRow[o + 1] = clip[UgTerm[U] + VgTerm[V] + yy];
            bgrRow[o + 2] = clip[VrTerm[V] + yy];
        }
    }
}
