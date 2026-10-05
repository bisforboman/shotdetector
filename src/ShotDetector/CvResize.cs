namespace ShotDetector;

/// <summary>
/// Port of cv2.resize(..., interpolation=INTER_LINEAR) for 8-bit 3-channel images
/// (imgproc/resize.cpp), matching OpenCV's fixed-point arithmetic: 11-bit coefficients, an exact
/// horizontal pass, then the vertical pass as done by its SIMD path (VResizeLinearVec_32s8u).
/// When downscaling it samples only the 2x2 nearest source pixels, so fine detail aliases, and it
/// reads only a fraction of the source: ResizeYuv420 converts just those pixels.
/// </summary>
internal sealed class CvResize
{
    const int CoefScale = 1 << 11;

    readonly int _sw, _sh, _dw, _dh;
    readonly int[] _xofs, _yofs;
    readonly short[] _xa0, _xa1, _yb0, _yb1;
    // Output rows are independent, so they're split into chunks resized in parallel, each with its
    // own row buffers. Chunks are fixed so buffers can be allocated once.
    readonly Chunk[] _chunks;

    sealed class Chunk(int start, int end, int dw, int sw)
    {
        public readonly int Start = start, End = end;
        public readonly int[] Row0 = new int[dw * 3], Row1 = new int[dw * 3];
        public byte[] BgrRow0 = new byte[sw * 3], BgrRow1 = new byte[sw * 3];
    }

    /// <summary>The source columns the horizontal pass reads, ascending.</summary>
    public int[] SourceCols { get; }
    int[]? _shiftedCols;

    public CvResize(int sw, int sh, int dw, int dh)
    {
        (_sw, _sh, _dw, _dh) = (sw, sh, dw, dh);
        (_xofs, _xa0, _xa1) = Table(dw, sw, clampFraction: true);
        (_yofs, _yb0, _yb1) = Table(dh, sh, clampFraction: false);
        int chunks = Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Max(1, dh / 8));
        _chunks = Enumerable.Range(0, chunks).Select(c => new Chunk(c * dh / chunks, (c + 1) * dh / chunks, dw, sw)).ToArray();
        SourceCols = _xofs.SelectMany(x => x >= sw - 1 ? new[] { x } : new[] { x, x + 1 }).Distinct().Order().ToArray();
    }

    /// <summary>One-off resize of a packed BGR image.</summary>
    public static void Linear(byte[] src, int sw, int sh, byte[] dst, int dw, int dh) =>
        new CvResize(sw, sh, dw, dh).Resize(src, dst);

    /// <summary>Resizes a packed BGR source.</summary>
    public void Resize(byte[] src, byte[] dst) => Parallel.ForEach(_chunks, c =>
    {
        int stride = _sw * 3;
        for (int dy = c.Start; dy < c.End; dy++)
        {
            HResize(src.AsSpan(Row0(dy) * stride, stride), c.Row0);
            HResize(src.AsSpan(Row1(dy) * stride, stride), c.Row1);
            VResize(dy, c, dst);
        }
    });

    /// <summary>
    /// Resizes a packed yuv420p source as if it had first been converted to BGR with
    /// <paramref name="converter"/>, converting only the pixels the resize reads.
    /// </summary>
    public void ResizeYuv420(byte[] yuv, byte[] dst, IYuv420Converter converter) =>
        ResizeYuv420(yuv, dst, converter, _sw, _sh, 0, 0);

    /// <summary>
    /// As <see cref="ResizeYuv420(byte[], byte[], IYuv420Converter)"/>, resizing the
    /// <c>sw × sh</c> region at (<paramref name="cropX"/>, <paramref name="cropY"/>) of a
    /// <paramref name="fullWidth"/> × <paramref name="fullHeight"/> yuv420p frame. The chroma stays
    /// aligned to the full frame, as cropping after conversion keeps it.
    /// </summary>
    public void ResizeYuv420(byte[] yuv, byte[] dst, IYuv420Converter converter, int fullWidth, int fullHeight, int cropX, int cropY) => Parallel.ForEach(_chunks, c =>
    {
        int[] cols = cropX == 0 ? SourceCols : _shiftedCols ??= SourceCols.Select(x => x + cropX).ToArray();
        int rowBytes = fullWidth * 3;
        if (c.BgrRow0.Length < rowBytes)
            (c.BgrRow0, c.BgrRow1) = (new byte[rowBytes], new byte[rowBytes]);
        int lastRow0 = -1, lastRow1 = -1;
        for (int dy = c.Start; dy < c.End; dy++)
        {
            int r0 = Row0(dy), r1 = Row1(dy);
            // Downscaling rarely reuses rows, but upscaling does; skip converting them twice.
            if (r0 != lastRow0)
            {
                converter.RowToBgr(yuv, fullWidth, fullHeight, r0 + cropY, cols, c.BgrRow0);
                HResize(c.BgrRow0.AsSpan(cropX * 3), c.Row0);
            }
            if (r1 != lastRow1)
            {
                converter.RowToBgr(yuv, fullWidth, fullHeight, r1 + cropY, cols, c.BgrRow1);
                HResize(c.BgrRow1.AsSpan(cropX * 3), c.Row1);
            }
            (lastRow0, lastRow1) = (r0, r1);
            VResize(dy, c, dst);
        }
    });

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

    void VResize(int dy, Chunk c, byte[] dst)
    {
        int b0 = _yb0[dy], b1 = _yb1[dy];
        var d = dst.AsSpan(dy * _dw * 3, _dw * 3);
        for (int x = 0; x < d.Length; x++)
        {
            int v = (((b0 * (c.Row0[x] >> 4)) >> 16) + ((b1 * (c.Row1[x] >> 4)) >> 16) + 2) >> 2;
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
