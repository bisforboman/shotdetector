using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    const int ParallelPixels = 1 << 17; // ~362 x 362

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

    /// <summary>The source rows the vertical pass reads, ascending.</summary>
    public int[] SourceRows { get; }
    int[]? _rowIndex; // source row -> its index in SourceRows
    // For a sampled row (SourceCols only): byte offsets of each output column's two source pixels, and their
    // weights; at the right border the second weight is 0, which gives HResize's s * CoefScale exactly.
    int[]? _sampledX0, _sampledX1;
    short[]? _sampledW0, _sampledW1;
    int[]? _shiftedCols;
    // The same for a whole row (Resize, ResizeYuv420).
    readonly int[] _fullX0, _fullX1;
    readonly short[] _fullW0, _fullW1;

    public CvResize(int sw, int sh, int dw, int dh)
    {
        (_sw, _sh, _dw, _dh) = (sw, sh, dw, dh);
        (_xofs, _xa0, _xa1) = Table(dw, sw, clampFraction: true);
        (_yofs, _yb0, _yb1) = Table(dh, sh, clampFraction: false);
        // Small outputs (scenedetect's sizes are at most 256 pixels wide) take well under a millisecond;
        // splitting them across threads costs more CPU than it saves, so only large ones go parallel.
        int chunks = dw * dh < ParallelPixels ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Max(1, dh / 8));
        _chunks = Enumerable.Range(0, chunks).Select(c => new Chunk(c * dh / chunks, (c + 1) * dh / chunks, dw, sw)).ToArray();
        SourceCols = _xofs.SelectMany(x => x >= sw - 1 ? new[] { x } : new[] { x, x + 1 }).Distinct().Order().ToArray();
        SourceRows = Enumerable.Range(0, dh).SelectMany(dy => new[] { Row0(dy), Row1(dy) }).Distinct().Order().ToArray();
        (_fullX0, _fullX1, _fullW0, _fullW1) = (new int[dw], new int[dw], new short[dw], new short[dw]);
        for (int dx = 0; dx < dw; dx++)
        {
            int sx = _xofs[dx];
            bool edge = sx >= sw - 1;
            (_fullX0[dx], _fullX1[dx]) = (sx * 3, edge ? sx * 3 : sx * 3 + 3);
            (_fullW0[dx], _fullW1[dx]) = edge ? ((short)CoefScale, (short)0) : (_xa0[dx], _xa1[dx]);
        }
    }

    /// <summary>
    /// Resizes from only the pixels the resize reads: <see cref="SourceRows"/> × <see cref="SourceCols"/>, packed BGR
    /// (what ffmpeg's remap filter sends). Same arithmetic as <see cref="Resize"/>, so the same result.
    /// </summary>
    public void ResizeSampled(byte[] sampled, byte[] dst)
    {
        if (_rowIndex is null)
            PrepareSampled();
        ForChunks(c =>
        {
            int rowBytes = SourceCols.Length * 3, lastRow0 = -1, lastRow1 = -1;
            for (int dy = c.Start; dy < c.End; dy++)
            {
                int r0 = Row0(dy), r1 = Row1(dy);
                if (r0 != lastRow0)
                    HResizeSampled(sampled.AsSpan(_rowIndex![r0] * rowBytes, rowBytes), c.Row0);
                if (r1 != lastRow1)
                    HResizeSampled(sampled.AsSpan(_rowIndex![r1] * rowBytes, rowBytes), c.Row1);
                (lastRow0, lastRow1) = (r0, r1);
                VResize(dy, c, dst);
            }
        });
    }

    void PrepareSampled()
    {
        var col = new Dictionary<int, int>();
        for (int k = 0; k < SourceCols.Length; k++)
            col[SourceCols[k]] = k;
        (_sampledX0, _sampledX1) = (new int[_dw], new int[_dw]);
        (_sampledW0, _sampledW1) = (new short[_dw], new short[_dw]);
        for (int dx = 0; dx < _dw; dx++)
        {
            int sx = _xofs[dx];
            bool edge = sx >= _sw - 1;
            _sampledX0[dx] = col[sx] * 3;
            _sampledX1[dx] = edge ? col[sx] * 3 : col[sx + 1] * 3;
            (_sampledW0[dx], _sampledW1[dx]) = edge ? ((short)CoefScale, (short)0) : (_xa0[dx], _xa1[dx]);
        }
        var index = new int[_sh];
        for (int i = 0; i < SourceRows.Length; i++)
            index[SourceRows[i]] = i;
        _rowIndex = index;
    }

    /// <summary>HResize from a sampled row: the same products and sums, read from the packed columns.</summary>
    void HResizeSampled(ReadOnlySpan<byte> s, int[] d) => HResize(s, d, _sampledX0!, _sampledX1!, _sampledW0!, _sampledW1!);

    /// <summary>
    /// OpenCV's horizontal pass: per output pixel, the two source pixels at byte offsets x0/x1 times 11-bit weights
    /// (at the right border the second weight is 0: s * CoefScale). The offsets are in range by construction, so no
    /// bounds checks: this runs for every row of every frame.
    /// </summary>
    static unsafe void HResize(ReadOnlySpan<byte> s, int[] d, int[] x0, int[] x1, short[] w0, short[] w1)
    {
        if (x0.Length > 0 && Math.Max(x0[^1], x1[^1]) + 2 >= s.Length)
            throw new ArgumentException("The row is shorter than the resize reads.");
        fixed (byte* sp = s)
        fixed (int* dp = d, a0 = x0, a1 = x1)
        fixed (short* v0 = w0, v1 = w1)
        {
            int* o = dp;
            for (int dx = 0; dx < x0.Length; dx++, o += 3)
            {
                byte* a = sp + a0[dx], b = sp + a1[dx];
                int wa = v0[dx], wb = v1[dx];
                o[0] = a[0] * wa + b[0] * wb;
                o[1] = a[1] * wa + b[1] * wb;
                o[2] = a[2] * wa + b[2] * wb;
            }
        }
    }

    /// <summary>Runs the body on every chunk: inline when there is one, else in parallel.</summary>
    void ForChunks(Action<Chunk> body)
    {
        if (_chunks.Length == 1)
            body(_chunks[0]);
        else
            Parallel.ForEach(_chunks, body);
    }

    /// <summary>One-off resize of a packed BGR image.</summary>
    public static void Linear(byte[] src, int sw, int sh, byte[] dst, int dw, int dh) =>
        new CvResize(sw, sh, dw, dh).Resize(src, dst);

    /// <summary>Resizes a packed BGR source.</summary>
    public void Resize(byte[] src, byte[] dst) => ForChunks(c =>
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
    public void ResizeYuv420(byte[] yuv, byte[] dst, IYuv420Converter converter, int fullWidth, int fullHeight, int cropX, int cropY) => ForChunks(c =>
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

    void HResize(ReadOnlySpan<byte> s, int[] d) => HResize(s, d, _fullX0, _fullX1, _fullW0, _fullW1);

    /// <summary>OpenCV's vertical pass (VResizeLinearVec_32s8u's arithmetic), 32 values at a time where possible.</summary>
    void VResize(int dy, Chunk c, byte[] dst)
    {
        int b0 = _yb0[dy], b1 = _yb1[dy], n = _dw * 3, x = 0;
        var d = dst.AsSpan(dy * n, n);
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(c.Row0);
        ref int r1 = ref MemoryMarshal.GetArrayDataReference(c.Row1);
        if (Vector256.IsHardwareAccelerated)
        {
            var vb0 = Vector256.Create(b0);
            var vb1 = Vector256.Create(b1);
            ref byte o = ref MemoryMarshal.GetReference(d);
            for (; x <= n - 32; x += 32)
            {
                var p = Pass(ref r0, ref r1, (nuint)x, vb0, vb1);
                var q = Pass(ref r0, ref r1, (nuint)x + 8, vb0, vb1);
                var r = Pass(ref r0, ref r1, (nuint)x + 16, vb0, vb1);
                var t = Pass(ref r0, ref r1, (nuint)x + 24, vb0, vb1);
                // Clamped to 0..255, so narrowing keeps every value.
                Vector256.Narrow(Vector256.Narrow(p, q), Vector256.Narrow(r, t)).AsByte().StoreUnsafe(ref o, (nuint)x);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
            x = VResize128(ref r0, ref r1, ref MemoryMarshal.GetReference(d), n, b0, b1);
        for (; x < n; x++)
        {
            int v = (((b0 * (Unsafe.Add(ref r0, x) >> 4)) >> 16) + ((b1 * (Unsafe.Add(ref r1, x) >> 4)) >> 16) + 2) >> 2;
            d[x] = (byte)Math.Clamp(v, 0, 255);
        }
    }

    /// <summary>The vertical pass 16 values at a time with 128-bit vectors (ARM); returns how many it did.</summary>
    internal static int VResize128(ref int r0, ref int r1, ref byte o, int n, int b0, int b1)
    {
        var vb0 = Vector128.Create(b0);
        var vb1 = Vector128.Create(b1);
        int x = 0;
        for (; x <= n - 16; x += 16)
        {
            var p = Pass(ref r0, ref r1, (nuint)x, vb0, vb1);
            var q = Pass(ref r0, ref r1, (nuint)x + 4, vb0, vb1);
            var r = Pass(ref r0, ref r1, (nuint)x + 8, vb0, vb1);
            var t = Pass(ref r0, ref r1, (nuint)x + 12, vb0, vb1);
            Vector128.Narrow(Vector128.Narrow(p, q), Vector128.Narrow(r, t)).AsByte().StoreUnsafe(ref o, (nuint)x);
        }
        return x;
    }

    static Vector128<int> Pass(ref int r0, ref int r1, nuint x, Vector128<int> b0, Vector128<int> b1)
    {
        var v = (((b0 * (Vector128.LoadUnsafe(ref r0, x) >> 4)) >> 16) + ((b1 * (Vector128.LoadUnsafe(ref r1, x) >> 4)) >> 16)
            + Vector128.Create(2)) >> 2;
        return Vector128.Min(Vector128.Max(v, Vector128<int>.Zero), Vector128.Create(255));
    }

    static Vector256<int> Pass(ref int r0, ref int r1, nuint x, Vector256<int> b0, Vector256<int> b1)
    {
        var v = (((b0 * (Vector256.LoadUnsafe(ref r0, x) >> 4)) >> 16) + ((b1 * (Vector256.LoadUnsafe(ref r1, x) >> 4)) >> 16)
            + Vector256.Create(2)) >> 2;
        return Vector256.Min(Vector256.Max(v, Vector256<int>.Zero), Vector256.Create(255));
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
