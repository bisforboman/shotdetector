// SPDX-License-Identifier: LGPL-2.1-or-later
// Part of the port of FFmpeg libswscale's yuv420p and yuv422p -> BGR24 conversion (SwscaleYuv420.cs): the same
// arithmetic, 16 pixels at a time. Licensed under the GNU Lesser General Public License, version 2.1 or (at your
// option) any later version; see COPYING.LGPL.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ShotDetector.FastYuv;

// The row conversion with Vector128 (SSE on x64, NEON on ARM64, wasm SIMD), giving the same bytes as the tables. Each
// table entry is MulHi(8i - offset, coeff) = (8i - offset) * coeff >> 16 with an arithmetic shift, and every factor is
// within 16 bits (|8i - offset| <= 2040, |coeff| < 2^15), so the 32-bit product (under 2^27) is exact: computed per
// lane as i * 8coeff + (-offset * coeff), the same integer, it shifts to the same term. Each term then fits 16 bits,
// so narrowing keeps it, and the sums of two or three terms the tables add stay within ±1024, where Clip is a clamp
// to 0..255. The columns the resize reads are scattered (pairs, a few pixels apart, from wide frames), so the vectors
// go only where 16 consecutive pixels hold enough of them (Plan); the other pixels the vectors compute are not
// written (IYuv420Converter.RowToBgr leaves them untouched).
public sealed partial class SwscaleYuv420
{
    /// <summary>
    /// The fewest of a block's 16 pixels the columns must ask for before the block is converted with vectors (the rest
    /// go a pixel at a time).
    /// </summary>
    internal const int MinColumnsPerBlock = 10;

    const int BlockPixels = 16;

    readonly VectorTerms _vectorTerms;

    Plan? _plan;

    /// <summary>
    /// Whether RowToBgr uses Vector128 where it pays (the same bytes either way). Tests turn it off on an instance to
    /// compare both on one machine.
    /// </summary>
    internal bool Vectorized { get; set; } = Vector128.IsHardwareAccelerated;

    /// <summary>
    /// Whether the columns are dense enough for blocks, on average over the stretch they span (first to last: the
    /// resize's are in order). Sparser ones (wide frames: pairs a few pixels apart) skip the plan and its check.
    /// </summary>
    internal static bool DenseEnough(ReadOnlySpan<int> cols) =>
        cols.Length >= MinColumnsPerBlock && (long)cols.Length * BlockPixels >= MinColumnsPerBlock * (Math.Abs((long)cols[^1] - cols[0]) + 1);

    /// <summary>
    /// The plan for these columns: the last one when the columns are the same (callers pass one set of columns
    /// for every row of every frame), else a new one.
    /// </summary>
    Plan PlanFor(ReadOnlySpan<int> cols, int width)
    {
        var plan = Volatile.Read(ref _plan);
        if (plan is null || plan.Width != width || !cols.SequenceEqual(plan.Cols))
        {
            plan = new Plan(cols, width);
            Volatile.Write(ref _plan, plan);
        }

        return plan;
    }

    /// <summary>
    /// Where a row's columns go: blocks of 16 pixels (starting at an even column, so a block takes 8 whole chroma
    /// samples) that the columns fill completely, blocks they fill partly (with a byte mask of the pixels asked for),
    /// and the other columns, a pixel at a time. Any order and repeats are fine (each pixel's bytes depend on it alone).
    /// </summary>
    internal sealed class Plan
    {
        public Plan(ReadOnlySpan<int> cols, int width)
        {
            Width = width;
            Cols = cols.ToArray();
            int[] sorted = [.. Cols.Distinct().Order()];
            if (sorted.Length > 0 && (sorted[0] < 0 || sorted[^1] >= width))
                throw new ArgumentOutOfRangeException(nameof(cols), sorted[0] < 0 ? sorted[0] : sorted[^1], "a column outside the row");

            List<int> full = [], partial = [], singles = [];
            List<byte> masks = [];
            for (int i = 0; i < sorted.Length;)
            {
                int x0 = sorted[i] & ~1, end = i;
                while (end < sorted.Length && sorted[end] < x0 + BlockPixels)
                    end++;
                if (x0 + BlockPixels > width || end - i < MinColumnsPerBlock)
                {
                    singles.Add(sorted[i++]);
                    continue;
                }

                if (end - i == BlockPixels)
                {
                    full.Add(x0);
                }
                else
                {
                    partial.Add(x0);
                    var mask = new byte[BlockPixels * 3];
                    for (int k = i; k < end; k++)
                        mask.AsSpan((sorted[k] - x0) * 3, 3).Fill(0xFF);
                    masks.AddRange(mask);
                }

                i = end;
            }

            (Full, Partial, Masks, Singles) = ([.. full], [.. partial], [.. masks], [.. singles]);
        }

        public int Width { get; }

        public int[] Cols { get; }

        /// <summary>First pixels of the blocks every pixel of which is asked for.</summary>
        public int[] Full { get; }

        /// <summary>First pixels of the blocks some of whose pixels are asked for, those pixels' bytes in <see cref="Masks"/>.</summary>
        public int[] Partial { get; }

        /// <summary>48 bytes per partial block: 0xFF at the BGR bytes of the pixels asked for, else 0.</summary>
        public byte[] Masks { get; }

        /// <summary>The columns converted a pixel at a time.</summary>
        public int[] Singles { get; }

        /// <summary>Whether any block goes with vectors (else the columns go a pixel at a time, as without a plan).</summary>
        public bool HasBlocks => Full.Length + Partial.Length > 0;
    }

    /// <summary>The tables' terms as multiply-adds of the sample (MulHi(8i - offset, coeff) = (i * Mul + Add) >> 16), broadcast.</summary>
    readonly struct VectorTerms(int yCoeff, int yOffset, int ubCoeff, int ugCoeff, int vrCoeff, int vgCoeff)
    {
        public readonly Vector128<int> YMul = Vector128.Create(8 * yCoeff), YAdd = Vector128.Create(-yOffset * yCoeff);
        public readonly Vector128<int> UbMul = Vector128.Create(8 * ubCoeff), UbAdd = Vector128.Create(-1024 * ubCoeff);
        public readonly Vector128<int> UgMul = Vector128.Create(8 * ugCoeff), UgAdd = Vector128.Create(-1024 * ugCoeff);
        public readonly Vector128<int> VrMul = Vector128.Create(8 * vrCoeff), VrAdd = Vector128.Create(-1024 * vrCoeff);
        public readonly Vector128<int> VgMul = Vector128.Create(8 * vgCoeff), VgAdd = Vector128.Create(-1024 * vgCoeff);
    }

    private readonly ref partial struct Planes
    {
        /// <summary>The columns of <paramref name="plan"/> in <paramref name="row"/> (the plan's columns are in the row; its bgr holds the row).</summary>
        public static void ByPlan(Planes row, Plan plan, in VectorTerms t)
        {
            foreach (int x0 in plan.Full)
            {
                row.Block(x0, t, out var o0, out var o1, out var o2);
                ref byte d = ref Unsafe.Add(ref row._bgr, x0 * 3);
                o0.StoreUnsafe(ref d);
                o1.StoreUnsafe(ref d, 16);
                o2.StoreUnsafe(ref d, 32);
            }

            ref byte masks = ref MemoryMarshal.GetArrayDataReference(plan.Masks);
            for (int i = 0; i < plan.Partial.Length; i++)
            {
                int x0 = plan.Partial[i];
                row.Block(x0, t, out var o0, out var o1, out var o2);
                ref byte d = ref Unsafe.Add(ref row._bgr, x0 * 3);
                ref byte m = ref Unsafe.Add(ref masks, i * 48);
                Vector128.ConditionalSelect(Vector128.LoadUnsafe(ref m), o0, Vector128.LoadUnsafe(ref d)).StoreUnsafe(ref d);
                Vector128.ConditionalSelect(Vector128.LoadUnsafe(ref m, 16), o1, Vector128.LoadUnsafe(ref d, 16)).StoreUnsafe(ref d, 16);
                Vector128.ConditionalSelect(Vector128.LoadUnsafe(ref m, 32), o2, Vector128.LoadUnsafe(ref d, 32)).StoreUnsafe(ref d, 32);
            }

            foreach (int x in plan.Singles)
                row.Pixel(x);
        }

        /// <summary>Pixels x0 .. x0 + 15 (x0 even, x0 + 16 &lt;= the row's width) as packed BGR, 48 bytes in three vectors.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Block(int x0, in VectorTerms t, out Vector128<byte> o0, out Vector128<byte> o1, out Vector128<byte> o2)
        {
            // Luma: 16 samples, a term each. (The shifts are the operator: on wasm, Mono compiles
            // Vector128.ShiftRightArithmetic as a call.)
            var (l0, l1) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.Add(ref _luma, x0)));
            var (y0, y1) = Vector128.Widen(l0.AsInt16());
            var (y2, y3) = Vector128.Widen(l1.AsInt16());
            var yLo = Vector128.Narrow((y0 * t.YMul) + t.YAdd >> 16, (y1 * t.YMul) + t.YAdd >> 16);
            var yHi = Vector128.Narrow((y2 * t.YMul) + t.YAdd >> 16, (y3 * t.YMul) + t.YAdd >> 16);

            // Chroma: the 8 samples under the block (x0 / 2 + 8 <= the chroma width), their terms, each then for two pixels.
            var cu = Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref _u, x0 >> 1))).AsByte());
            var cv = Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref _v, x0 >> 1))).AsByte());
            var (u0, u1) = Vector128.Widen(cu.AsInt16());
            var (v0, v1) = Vector128.Widen(cv.AsInt16());
            var ub = Vector128.Narrow((u0 * t.UbMul) + t.UbAdd >> 16, (u1 * t.UbMul) + t.UbAdd >> 16);
            var g = Vector128.Narrow(
                ((u0 * t.UgMul) + t.UgAdd >> 16) + ((v0 * t.VgMul) + t.VgAdd >> 16),
                ((u1 * t.UgMul) + t.UgAdd >> 16) + ((v1 * t.VgMul) + t.VgAdd >> 16));
            var vr = Vector128.Narrow((v0 * t.VrMul) + t.VrAdd >> 16, (v1 * t.VrMul) + t.VrAdd >> 16);

            var lo = Vector128.Create((short)0, 0, 1, 1, 2, 2, 3, 3);
            var hi = Vector128.Create((short)4, 4, 5, 5, 6, 6, 7, 7);
            var b = Clamp(yLo + Vector128.Shuffle(ub, lo), yHi + Vector128.Shuffle(ub, hi));
            var gr = Clamp(yLo + Vector128.Shuffle(g, lo), yHi + Vector128.Shuffle(g, hi));
            var r = Clamp(yLo + Vector128.Shuffle(vr, lo), yHi + Vector128.Shuffle(vr, hi));

            // Interleaved: B G R per pixel (an index of 0xFF gives 0).
            const byte Z = 0xFF;
            o0 = Vector128.Shuffle(b, Vector128.Create(0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z, Z, 5))
                | Vector128.Shuffle(gr, Vector128.Create(Z, 0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z, Z))
                | Vector128.Shuffle(r, Vector128.Create(Z, Z, 0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z));
            o1 = Vector128.Shuffle(b, Vector128.Create(Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z, 10, Z))
                | Vector128.Shuffle(gr, Vector128.Create(5, Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z, 10))
                | Vector128.Shuffle(r, Vector128.Create(Z, 5, Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z));
            o2 = Vector128.Shuffle(b, Vector128.Create(Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15, Z, Z))
                | Vector128.Shuffle(gr, Vector128.Create(Z, Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15, Z))
                | Vector128.Shuffle(r, Vector128.Create(10, Z, Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15));
        }

        /// <summary>Two vectors of sums, each clamped to 0..255 (Clip), as 16 bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Clamp(Vector128<short> a, Vector128<short> b)
        {
            var max = Vector128.Create((short)255);
            return Vector128.Narrow(
                Vector128.Min(Vector128.Max(a, Vector128<short>.Zero), max).AsUInt16(),
                Vector128.Min(Vector128.Max(b, Vector128<short>.Zero), max).AsUInt16());
        }
    }
}
