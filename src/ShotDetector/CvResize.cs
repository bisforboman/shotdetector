namespace ShotDetector;

/// <summary>
/// Port of cv2.resize(..., interpolation=INTER_LINEAR) for 8-bit 3-channel images
/// (imgproc/resize.cpp), matching OpenCV's fixed-point arithmetic: 11-bit coefficients, an exact
/// horizontal pass, then the vertical pass as done by its SIMD path (VResizeLinearVec_32s8u).
/// When downscaling it samples only the 2x2 nearest source pixels, so fine detail aliases.
/// </summary>
public static class CvResize
{
    const int CoefScale = 1 << 11;

    public static void Linear(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        var (xofs, xa0, xa1) = Table(dw, sw, clampFraction: true);
        var (yofs, yb0, yb1) = Table(dh, sh, clampFraction: false);
        var row0 = new int[dw * 3];
        var row1 = new int[dw * 3];

        for (int dy = 0; dy < dh; dy++)
        {
            HResize(src.Slice(Math.Clamp(yofs[dy], 0, sh - 1) * sw * 3, sw * 3), sw, xofs, xa0, xa1, row0);
            HResize(src.Slice(Math.Clamp(yofs[dy] + 1, 0, sh - 1) * sw * 3, sw * 3), sw, xofs, xa0, xa1, row1);
            int b0 = yb0[dy], b1 = yb1[dy];
            var d = dst.Slice(dy * dw * 3, dw * 3);
            for (int x = 0; x < d.Length; x++)
            {
                int v = (((b0 * (row0[x] >> 4)) >> 16) + ((b1 * (row1[x] >> 4)) >> 16) + 2) >> 2;
                d[x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    static void HResize(ReadOnlySpan<byte> s, int sw, int[] xofs, short[] a0, short[] a1, int[] d)
    {
        for (int dx = 0; dx < xofs.Length; dx++)
        {
            int sx = xofs[dx];
            for (int c = 0; c < 3; c++)
            {
                int i = sx * 3 + c;
                d[dx * 3 + c] = sx >= sw - 1 ? s[i] * CoefScale : s[i] * a0[dx] + s[i + 3] * a1[dx];
            }
        }
    }

    /// <summary>Source offset and the two fixed-point weights for each destination index.</summary>
    static (int[] Ofs, short[] W0, short[] W1) Table(int dsize, int ssize, bool clampFraction)
    {
        double scale = 1.0 / ((double)dsize / ssize);
        var ofs = new int[dsize];
        var w0 = new short[dsize];
        var w1 = new short[dsize];
        for (int d = 0; d < dsize; d++)
        {
            float f = (float)((d + 0.5) * scale - 0.5);
            int s = (int)MathF.Floor(f);
            f -= s;
            // Horizontally OpenCV pins the borders; vertically it clamps the row index instead.
            if (clampFraction && s < 0) { f = 0; s = 0; }
            if (clampFraction && s >= ssize - 1) { f = 0; s = ssize - 1; }
            ofs[d] = s;
            w0[d] = (short)MathF.Round((1f - f) * CoefScale);
            w1[d] = (short)MathF.Round(f * CoefScale);
        }
        return (ofs, w0, w1);
    }
}
