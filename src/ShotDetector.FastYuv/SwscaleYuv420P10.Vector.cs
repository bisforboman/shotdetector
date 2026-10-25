// SPDX-License-Identifier: LGPL-2.1-or-later
// Part of the port of FFmpeg libswscale's scaled yuv420p10le -> BGR24 path (SwscaleYuv420P10.cs): the MMXEXT output's
// pixels, 16 at a time. Licensed under the GNU Lesser General Public License, version 2.1 or (at your option) any later
// version; see COPYING.LGPL.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace ShotDetector.FastYuv;

// PixelsAsm with Vector128 (SSE on x64, NEON on ARM64, wasm SIMD), giving the same bytes. Per pixel the scalar code
// computes y15 = min(Y << 5, 0x7FFF) in 32 bits; here min(Y, 1024) << 5 (at most 32768, so no 16-bit lane overflows)
// then an unsigned min with 0x7FFF, which is the same number for every 16-bit Y (Y <= 1023: Y << 5 <= 32736; else both
// are 0x7FFF). a = 4 + (y15 >> 4) - yOffset is then within -124..2051, so (short)a is a, and pmulhw's (a * yCoeff) >> 16
// (|yCoeff| < 2^15: a product under 2^27) is computed exactly in 32-bit lanes, shifted arithmetically and narrowed (it
// fits 16 bits). The chroma terms are 16-bit already (MulHi results, or cast to short), so narrowing them keeps them;
// the 16-bit lane add wraps as the scalar's (short) does, and the clamp to 0..255 is packuswb's. A block is 16 pixels
// from an even column (8 whole chroma samples, whose terms must be consecutive in the row's terms), converted at once:
// stored directly where the columns ask for exactly those 16 pixels in order, else into a scratch block from which
// the pixels asked for are copied; the other columns go a pixel at a time.
//
// The horizontal chroma filter (hScale16To15) 8 samples at a time where the pairs' chroma samples run on consecutively
// and so do the filter's positions for them (every sample but a few at the plane's edges), so each tap's 8 source samples
// are one load. On x86 (4 taps), pmaddwd of each tap pair's samples interleaved with their coefficients, the two pairs'
// sums added: the integers of the SSSE3 path it replaces there (pmaddwd per sample's four taps, then phaddd), which
// reads samples as signed 16-bit words like this one, and still takes the samples outside the blocks. Elsewhere, in
// 32-bit lanes: each tap's samples widened unsigned times its coefficients (stored by tap), summed: the scalar code's int
// arithmetic, wrapping alike. Both then >> 9 and min 0x7FFF. For 10-bit samples all of these agree (the signed and
// unsigned readings differ only from 32768 on).
//
// The vertical filter's chroma terms (YSCALEYUV2PACKEDX, YSCALEYUV2RGBX) 8 pairs at a time in 16-bit lanes, as the MMXEXT
// code has them: each filtered sample truncated to 16 bits (Narrow; the scalar code's (short)), pmulhw (x86's own; ARM64's
// smull, smull2 and the products' high halves; else the products in 32-bit lanes, >> 16, narrowed: each fits 16 bits),
// paddw and psubw wrapping as the scalar code's (short) casts, the terms widened back to 32 bits.
internal sealed partial class SwscaleYuv420P10
{
    /// <summary>The samples one horizontal block filters.</summary>
    const int HorizontalBlockSamples = 8;

    /// <summary>How the horizontal filter runs.</summary>
    internal enum Horizontal
    {
        /// <summary>A sample at a time.</summary>
        Scalar,

        /// <summary>x86: blocks with pmaddwd (4 taps), SSSE3 for the other samples.</summary>
        X86,

        /// <summary>Blocks with Vector128 in 32-bit lanes, the other samples one at a time.</summary>
        Portable,
    }

    /// <summary>The horizontal filter for one frame's conversion: its arrays, the pairs' chroma samples, the blocks.</summary>
    readonly unsafe ref struct HorizontalFilter(Horizontal vectors, short* coeffs, int* pos, int size, int* taps, short* tapPairs, int chromaWidth, int* chromaCol, int pairs, int* blocks, int blockCount)
    {
        public readonly Horizontal Vectors = vectors;
        public readonly short* Coeffs = coeffs, TapPairs = tapPairs;
        public readonly int* Pos = pos, Taps = taps, ChromaCol = chromaCol, Blocks = blocks;
        public readonly int Size = size, ChromaWidth = chromaWidth, Pairs = pairs, BlockCount = blockCount;
    }

    /// <summary>
    /// Whether the horizontal filter takes the portable vectors even on x86 (tests compare them with the scalar code
    /// there too).
    /// </summary>
    internal bool PortableHorizontal { get; set; }

    /// <summary>
    /// How the horizontal filter of <paramref name="size"/> taps runs: x86's way on x86 (with 4 taps; else scalar),
    /// portable vectors on ARM64 and wasm, scalar without <see cref="Vectorized"/>.
    /// </summary>
    internal Horizontal HorizontalVectors(int size) =>
        !Vectorized ? Horizontal.Scalar
        : PortableHorizontal ? Horizontal.Portable
        : Sse2.IsSupported ? (size == 4 ? Horizontal.X86 : Horizontal.Scalar)
        : AdvSimd.Arm64.IsSupported || PackedSimd.IsSupported ? Horizontal.Portable
        : Horizontal.Scalar;

    /// <summary>
    /// The horizontal filter's blocks for these chroma samples, into <paramref name="blocks"/> (the first pair of each):
    /// from the left, 8 pairs whose chroma samples are consecutive and whose filter positions are too. Returns how many.
    /// </summary>
    internal static int HorizontalBlocks(ReadOnlySpan<int> chromaCols, int[] hPos, int[] blocks)
    {
        int count = 0;
        for (int k = 0; k + HorizontalBlockSamples <= chromaCols.Length;)
        {
            int i = chromaCols[k], m = 1;
            while (m < HorizontalBlockSamples && chromaCols[k + m] == i + m && hPos[i + m] == hPos[i] + m)
                m++;
            if (m < HorizontalBlockSamples)
            {
                k++;
                continue;
            }

            blocks[count++] = k;
            k += HorizontalBlockSamples;
        }

        return count;
    }

    /// <summary>
    /// x86's hScale16To15 of 8 consecutive chroma samples whose 4-tap filters start at consecutive samples
    /// (<paramref name="src"/> at the first one's), with the coefficients by tap pair (<paramref name="tapPairs"/> at the
    /// first sample's, the next pair <paramref name="stride"/> on).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void HorizontalBlockX86(ushort* src, short* tapPairs, int stride, int* dst)
    {
        // Taps 0 and 1, then 2 and 3: samples x + t and x + t + 1 side by side, as each sample's coefficients are.
        var a = Vector128.Load((short*)src);
        var b = Vector128.Load((short*)src + 1);
        var lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a, b), Vector128.Load(tapPairs));
        var hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a, b), Vector128.Load(tapPairs + 8));
        a = Vector128.Load((short*)src + 2);
        b = Vector128.Load((short*)src + 3);
        lo += Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a, b), Vector128.Load(tapPairs + stride));
        hi += Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a, b), Vector128.Load(tapPairs + stride + 8));
        var max = Vector128.Create(0x7FFF);
        Vector128.Min(lo >> 9, max).Store(dst);
        Vector128.Min(hi >> 9, max).Store(dst + 4);
    }

    /// <summary>
    /// hScale16To15 of 8 consecutive chroma samples whose filters start at consecutive samples (<paramref name="src"/>
    /// at the first one's), with the taps' coefficients by tap (<paramref name="taps"/> at the first sample's).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void HorizontalBlock(ushort* src, int* taps, int stride, int size, int* dst)
    {
        Vector128<int> lo = Vector128<int>.Zero, hi = Vector128<int>.Zero;
        for (int t = 0; t < size; t++)
        {
            var (s0, s1) = Vector128.Widen(Vector128.Load(src + t));
            int* c = taps + t * stride;
            lo += s0.AsInt32() * Vector128.Load(c);
            hi += s1.AsInt32() * Vector128.Load(c + 4);
        }

        var max = Vector128.Create(0x7FFF);
        Vector128.Min(lo >> 9, max).Store(dst);
        Vector128.Min(hi >> 9, max).Store(dst + 4);
    }

    const int BlockPixels = 16;

    PixelPlan? _pixelPlan;

    /// <summary>
    /// Whether the MMXEXT output's pixels use Vector128 where the columns are dense enough (the same bytes either way).
    /// Tests turn it off on an instance to compare both on one machine.
    /// </summary>
    internal bool Vectorized { get; set; } = Vector128.IsHardwareAccelerated;

    /// <summary>
    /// The pixel plan for these columns (checked: inside the row; their chroma samples <paramref name="chromaCols"/>), or
    /// null when it would have no blocks: the last one when the columns are the same (callers pass one set for every row
    /// of every frame). The plan depends on the columns alone (any width they fit), and its blocks fit the row: a block's
    /// 8th chroma sample is some column's, so x0 + 14 &lt; the width, which is even, so x0 + 16 &lt;= it.
    /// </summary>
    PixelPlan? PixelPlanFor(ReadOnlySpan<int> cols, ReadOnlySpan<int> chromaCols)
    {
        // Sparse columns (wide frames' sampled ones) would make no blocks: no plan, no check.
        if (!Vectorized || !SwscaleYuv420.DenseEnough(cols))
            return null;
        var plan = Volatile.Read(ref _pixelPlan);
        if (plan is null || !cols.SequenceEqual(plan.Cols))
        {
            plan = new PixelPlan(cols, chromaCols);
            Volatile.Write(ref _pixelPlan, plan);
        }

        return plan.HasBlocks ? plan : null;
    }

    /// <summary>
    /// Where a row's columns go: blocks of 16 pixels (from an even column x0, with at least
    /// <see cref="SwscaleYuv420.MinColumnsPerBlock"/> of them asked for, and the 8 chroma samples under them consecutive
    /// in the row's chroma samples, from k0), either direct (the columns ask for exactly x0 .. x0 + 15, once each, in
    /// order, from index j0) or copied (each column asking for one of their pixels takes its 3 bytes); and the other
    /// columns' indices, a pixel at a time.
    /// </summary>
    internal sealed class PixelPlan
    {
        public PixelPlan(ReadOnlySpan<int> cols, ReadOnlySpan<int> chromaCols)
        {
            Cols = cols.ToArray();

            // The pixels up to the last column's pair: a row of an even width has them.
            int n = Cols.Length, width = n == 0 ? 0 : (Cols.Max() | 1) + 1;

            // The first index of each pixel and chroma sample, and how many times each pixel is asked for.
            var firstJ = new int[width];
            var count = new int[width];
            Array.Fill(firstJ, -1);
            for (int j = 0; j < n; j++)
            {
                int x = Cols[j];
                count[x]++;
                if (firstJ[x] < 0)
                    firstJ[x] = j;
            }

            var firstK = new int[(width + 1) / 2];
            Array.Fill(firstK, -1);
            for (int k = chromaCols.Length - 1; k >= 0; k--)
                firstK[chromaCols[k]] = k;

            // Blocks from the left, each at the first column not in one yet; copied ones numbered in blockOf.
            List<int> direct = [], copiedAt = [];
            var blockOf = new int[width];
            Array.Fill(blockOf, -1);
            for (int x = 0; x < width; x++)
            {
                int x0 = x & ~1, asked = 0;
                int k0 = firstK[x0 >> 1];
                if (count[x] == 0 || blockOf[x] != -1 || !Consecutive(chromaCols, k0, x0 >> 1))
                    continue;

                // (Its chroma samples are columns', so x0 + 15 is among the pixels.)
                for (int i = 0; i < BlockPixels; i++)
                    asked += count[x0 + i] > 0 ? 1 : 0;
                if (asked < SwscaleYuv420.MinColumnsPerBlock)
                    continue;

                // Direct: x0 .. x0 + 15 each asked for once, at consecutive indices from j0.
                int j0 = firstJ[x0], block = -2;
                bool isDirect = j0 >= 0 && j0 + BlockPixels <= n;
                for (int i = 0; i < BlockPixels && isDirect; i++)
                    isDirect = count[x0 + i] == 1 && Cols[j0 + i] == x0 + i;
                if (isDirect)
                {
                    direct.AddRange([x0, j0, k0]);
                }
                else
                {
                    block = copiedAt.Count / 2;
                    copiedAt.AddRange([x0, k0]);
                }

                Array.Fill(blockOf, block, x0, BlockPixels);
                x = x0 + BlockPixels - 1;
            }

            // Each copied block's columns, and the columns in no block.
            var perBlock = new List<int>[copiedAt.Count / 2];
            for (int b = 0; b < perBlock.Length; b++)
                perBlock[b] = [];
            List<int> singles = [];
            for (int j = 0; j < n; j++)
            {
                int b = blockOf[Cols[j]];
                if (b == -1)
                    singles.Add(j);
                else if (b >= 0)
                    perBlock[b].AddRange([j, Cols[j] - copiedAt[2 * b]]);
            }

            List<int> copied = [], copies = [];
            for (int b = 0; b < perBlock.Length; b++)
            {
                copied.AddRange([copiedAt[2 * b], copiedAt[(2 * b) + 1], copies.Count / 2, (copies.Count + perBlock[b].Count) / 2]);
                copies.AddRange(perBlock[b]);
            }

            (Direct, Copied, Copies, Singles) = ([.. direct], [.. copied], [.. copies], [.. singles]);
        }

        public int[] Cols { get; }

        /// <summary>The direct blocks: (x0, j0, k0) each.</summary>
        public int[] Direct { get; }

        /// <summary>The copied blocks: (x0, k0, first copy, end of copies) each.</summary>
        public int[] Copied { get; }

        /// <summary>The copied blocks' columns: (j, the pixel's place in its block) each.</summary>
        public int[] Copies { get; }

        /// <summary>The indices of the columns converted a pixel at a time.</summary>
        public int[] Singles { get; }

        public bool HasBlocks => Direct.Length + Copied.Length > 0;

        /// <summary>Whether the chroma samples from <paramref name="k0"/> are c0 .. c0 + 7 (a block's 8).</summary>
        static bool Consecutive(ReadOnlySpan<int> chromaCols, int k0, int c0)
        {
            if (k0 < 0 || k0 + BlockPixels / 2 > chromaCols.Length)
                return false;
            for (int i = 0; i < BlockPixels / 2; i++)
            {
                if (chromaCols[k0 + i] != c0 + i)
                    return false;
            }

            return true;
        }
    }

    /// <summary><see cref="PixelsAsm"/> by <paramref name="plan"/>: its blocks with vectors, its other columns a pixel at a time.</summary>
    unsafe void PixelsByPlan(PixelPlan plan, ushort* luma, int* cols, int* pairOf, int* tb, int* tg, int* tr, byte* output, bool packed)
    {
        var yCoeff = Vector128.Create(_yCoeff);
        var yAdd = Vector128.Create((short)(4 - _yOffset));
        fixed (int* direct = plan.Direct, copied = plan.Copied, copies = plan.Copies, singles = plan.Singles)
        {
            for (int i = 0; i < plan.Direct.Length; i += 3)
            {
                int x0 = direct[i], j0 = direct[i + 1], k0 = direct[i + 2];
                Block(luma + x0, tb + k0, tg + k0, tr + k0, yCoeff, yAdd, out var o0, out var o1, out var o2);
                byte* d = output + Offset(packed, j0, x0);
                o0.Store(d);
                o1.Store(d + 16);
                o2.Store(d + 32);
            }

            byte* scratch = stackalloc byte[BlockPixels * 3];
            for (int i = 0; i < plan.Copied.Length; i += 4)
            {
                int x0 = copied[i], k0 = copied[i + 1];
                Block(luma + x0, tb + k0, tg + k0, tr + k0, yCoeff, yAdd, out var o0, out var o1, out var o2);
                o0.Store(scratch);
                o1.Store(scratch + 16);
                o2.Store(scratch + 32);
                for (int c = copied[i + 2]; c < copied[i + 3]; c++)
                {
                    int j = copies[2 * c];
                    byte* s = scratch + copies[(2 * c) + 1] * 3, d = output + Offset(packed, j, cols[j]);
                    *(ushort*)d = *(ushort*)s;
                    d[2] = s[2];
                }
            }

            int yOffset = _yOffset, yc = _yCoeff;
            for (int i = 0; i < plan.Singles.Length; i++)
            {
                int j = singles[i];
                Pixel(luma, cols[j], tb[pairOf[j]], tg[pairOf[j]], tr[pairOf[j]], yOffset, yc, output + Offset(packed, j, cols[j]));
            }
        }
    }

    /// <summary>
    /// Pixels x0 .. x0 + 15 of the row (<paramref name="luma"/> at x0) with the chroma terms from the block's first pair
    /// (8 pairs) as packed BGR, 48 bytes in three vectors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void Block(ushort* luma, int* tb, int* tg, int* tr, Vector128<int> yCoeff, Vector128<short> yAdd, out Vector128<byte> o0, out Vector128<byte> o1, out Vector128<byte> o2)
    {
        var yLo = Luma(Vector128.Load(luma), yCoeff, yAdd);
        var yHi = Luma(Vector128.Load(luma + 8), yCoeff, yAdd);
        var (bLo, bHi) = BgrVectors.Double(Vector128.Narrow(Vector128.Load(tb), Vector128.Load(tb + 4)));
        var (gLo, gHi) = BgrVectors.Double(Vector128.Narrow(Vector128.Load(tg), Vector128.Load(tg + 4)));
        var (rLo, rHi) = BgrVectors.Double(Vector128.Narrow(Vector128.Load(tr), Vector128.Load(tr + 4)));
        BgrVectors.Interleave(BgrVectors.Clamp(yLo + bLo, yHi + bHi), BgrVectors.Clamp(yLo + gLo, yHi + gHi), BgrVectors.Clamp(yLo + rLo, yHi + rHi), out o0, out o1, out o2);
    }

    /// <summary>8 pixels' luma term: MulHi(4 + (min(Y &lt;&lt; 5, 0x7FFF) &gt;&gt; 4) - yOffset, yCoeff), as <see cref="Pixel"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<short> Luma(Vector128<ushort> y, Vector128<int> yCoeff, Vector128<short> yAdd)
    {
        var y15 = Vector128.Min(Vector128.Min(y, Vector128.Create((ushort)1024)) << 5, Vector128.Create((ushort)0x7FFF));
        var (a0, a1) = Vector128.Widen((y15 >> 4).AsInt16() + yAdd);

        // The shifts are the operator: on wasm, Mono compiles Vector128.ShiftRightArithmetic as a call.
        return Vector128.Narrow((a0 * yCoeff) >> 16, (a1 * yCoeff) >> 16);
    }

    /// <summary><see cref="ChromaTermsAsm"/>'s first pairs - pairs % 8, eight at a time in 16-bit lanes; returns how many it did.</summary>
    unsafe int ChromaTermsVector(int* f, int* rowOffset, int* vCoeff, int vSize, int pairs, int* tb, int* tg, int* tr)
    {
        var ub = Vector128.Create((short)_ubCoeff);
        var ug = Vector128.Create((short)_ugCoeff);
        var vg = Vector128.Create((short)_vgCoeff);
        var vr = Vector128.Create((short)_vrCoeff);
        var offset = Vector128.Create((short)1024);
        int k = 0;
        for (; k <= pairs - 8; k += 8)
        {
            Vector128<short> ua = Vector128.Create((short)VRounder), va = ua;
            for (int t = 0; t < vSize; t++)
            {
                var c = Vector128.Create((short)vCoeff[t]);
                int* fu = f + rowOffset[t] + k;
                ua += MulHi(Vector128.Narrow(Vector128.Load(fu), Vector128.Load(fu + 4)), c);
                va += MulHi(Vector128.Narrow(Vector128.Load(fu + pairs), Vector128.Load(fu + pairs + 4)), c);
            }

            Vector128<short> uu = ua - offset, vv = va - offset;
            StoreWidened(MulHi(uu, ub), tb + k);
            StoreWidened(MulHi(uu, ug) + MulHi(vv, vg), tg + k);
            StoreWidened(MulHi(vv, vr), tr + k);
        }

        return k;
    }

    /// <summary>pmulhw: the high half of each lane's signed 32-bit product.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<short> MulHi(Vector128<short> a, Vector128<short> b)
    {
        if (Sse2.IsSupported)
            return Sse2.MultiplyHigh(a, b);
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.UnzipOdd(
                AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower()).AsInt16(),
                AdvSimd.MultiplyWideningUpper(a, b).AsInt16());
        }

        // The shifts are the operator: on wasm, Mono compiles Vector128.ShiftRightArithmetic as a call.
        var (a0, a1) = Vector128.Widen(a);
        var (b0, b1) = Vector128.Widen(b);
        return Vector128.Narrow((a0 * b0) >> 16, (a1 * b1) >> 16);
    }

    /// <summary>8 16-bit values sign-extended to 32 bits, at <paramref name="dst"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void StoreWidened(Vector128<short> v, int* dst)
    {
        var (lo, hi) = Vector128.Widen(v);
        lo.Store(dst);
        hi.Store(dst + 4);
    }
}
