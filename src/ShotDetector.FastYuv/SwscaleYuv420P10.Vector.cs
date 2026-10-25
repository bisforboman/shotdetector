// SPDX-License-Identifier: LGPL-2.1-or-later
// Part of the port of FFmpeg libswscale's scaled yuv420p10le -> BGR24 path (SwscaleYuv420P10.cs): the MMXEXT output's
// pixels, 16 at a time. Licensed under the GNU Lesser General Public License, version 2.1 or (at your option) any later
// version; see COPYING.LGPL.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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
internal sealed partial class SwscaleYuv420P10
{
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
}
