namespace ShotDetector;

/// <summary>
/// Port of cv2.resize(..., interpolation=INTER_LINEAR) for 8-bit 3-channel images
/// (imgproc/resize.cpp), matching OpenCV's fixed-point arithmetic: 11-bit coefficients, an exact
/// horizontal pass, then the vertical pass as done by its SIMD path (VResizeLinearVec_32s8u).
/// When downscaling it samples only the 2x2 nearest source pixels, so fine detail aliases, and it
/// reads only a fraction of the source: <see cref="ResizeYuv420"/> converts just those pixels.
/// </summary>
public sealed class CvResize
{
    const int CoefScale = 1 << 11;

    readonly int _sw, _sh, _dw, _dh;
    readonly int[] _xofs, _yofs;
    readonly short[] _xa0, _xa1, _yb0, _yb1;
    readonly int[] _row0, _row1;
    readonly byte[] _bgrRow0, _bgrRow1;

    /// <summary>The source columns the horizontal pass reads, ascending.</summary>
    public int[] SourceCols { get; }

    public CvResize(int sw, int sh, int dw, int dh)
    {
        (_sw, _sh, _dw, _dh) = (sw, sh, dw, dh);
        (_xofs, _xa0, _xa1) = Table(dw, sw, clampFraction: true);
        (_yofs, _yb0, _yb1) = Table(dh, sh, clampFraction: false);
        _row0 = new int[dw * 3];
        _row1 = new int[dw * 3];
        _bgrRow0 = new byte[sw * 3];
        _bgrRow1 = new byte[sw * 3];
        SourceCols = _xofs.SelectMany(x => x >= sw - 1 ? new[] { x } : new[] { x, x + 1 }).Distinct().Order().ToArray();
    }

    /// <summary>One-off resize of a packed BGR image.</summary>
    public static void Linear(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh) =>
        new CvResize(sw, sh, dw, dh).Resize(src, dst);

    /// <summary>Resizes a packed BGR source.</summary>
    public void Resize(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int stride = _sw * 3;
        for (int dy = 0; dy < _dh; dy++)
        {
            HResize(src.Slice(Row0(dy) * stride, stride), _row0);
            HResize(src.Slice(Row1(dy) * stride, stride), _row1);
            VResize(dy, dst);
        }
    }

    /// <summary>
    /// Resizes a packed yuv420p source as if it had first been converted to BGR with
    /// <see cref="Yuv420"/>, converting only the pixels the resize reads.
    /// </summary>
    public void ResizeYuv420(ReadOnlySpan<byte> yuv, Span<byte> dst)
    {
        int lastRow0 = -1, lastRow1 = -1;
        for (int dy = 0; dy < _dh; dy++)
        {
            int r0 = Row0(dy), r1 = Row1(dy);
            // Downscaling rarely reuses rows, but upscaling does; skip converting them twice.
            if (r0 != lastRow0)
            {
                Yuv420.RowToBgr(yuv, _sw, _sh, r0, SourceCols, _bgrRow0);
                HResize(_bgrRow0, _row0);
            }
            if (r1 != lastRow1)
            {
                Yuv420.RowToBgr(yuv, _sw, _sh, r1, SourceCols, _bgrRow1);
                HResize(_bgrRow1, _row1);
            }
            (lastRow0, lastRow1) = (r0, r1);
            VResize(dy, dst);
        }
    }

    int Row0(int dy) => Math.Clamp(_yofs[dy], 0, _sh - 1);
    int Row1(int dy) => Math.Clamp(_yofs[dy] + 1, 0, _sh - 1);

    void HResize(ReadOnlySpan<byte> s, int[] d)
    {
        for (int dx = 0; dx < _xofs.Length; dx++)
        {
            int sx = _xofs[dx];
            for (int c = 0; c < 3; c++)
            {
                int i = sx * 3 + c;
                d[dx * 3 + c] = sx >= _sw - 1 ? s[i] * CoefScale : s[i] * _xa0[dx] + s[i + 3] * _xa1[dx];
            }
        }
    }

    void VResize(int dy, Span<byte> dst)
    {
        int b0 = _yb0[dy], b1 = _yb1[dy];
        var d = dst.Slice(dy * _dw * 3, _dw * 3);
        for (int x = 0; x < d.Length; x++)
        {
            int v = (((b0 * (_row0[x] >> 4)) >> 16) + ((b1 * (_row1[x] >> 4)) >> 16) + 2) >> 2;
            d[x] = (byte)Math.Clamp(v, 0, 255);
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
