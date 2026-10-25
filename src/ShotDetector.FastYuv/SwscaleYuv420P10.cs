// SPDX-License-Identifier: LGPL-2.1-or-later
// Port of FFmpeg libswscale's scaled yuv420p10le -> BGR24 path as x86-64 runs it at the same size: the horizontal
// scaler (libswscale/swscale.c, hScale16To15_c), the vertical scaler and output of libswscale/x86/swscale_template.c
// (YSCALEYUV2PACKEDX, YSCALEYUV2RGBX, yuv2bgr24_X) with its filter layout (libswscale/x86/swscale.c,
// ff_updateMMXDitherTables), and for the last two rows the C output (libswscale/output.c, yuv2rgb_X_c_template) with
// its tables (libswscale/yuv2rgb.c, ff_yuv2rgb_c_init_tables). Copyright (C) 2001-2012 Michael Niedermayer,
// (C) 2009 Konstantin Shishkov. Licensed under the GNU Lesser General Public License, version 2.1 or (at your
// option) any later version; see COPYING.LGPL.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ShotDetector.FastYuv;

/// <summary>
/// yuv420p10le → BGR as x86's swscale converts it at the same size with SWS_BICUBIC (what OpenCV and ShotDetector's
/// default path ask for). There is no unscaled converter for 10-bit, so swscale scales: chroma horizontally (a 4-tap
/// bicubic phase shift unless the chroma is centred) to 15 bits, then vertically (4 bicubic taps, 2x) together with
/// the conversion. On x86 that is MMXEXT inline assembly, which accumulates the taps in 16 bits (each product's high
/// half, wrapping) and doesn't clip chroma that overshoots; the last two rows take swscale's C output instead, whose
/// tables clip it. Even widths only (an odd width takes another path, full chroma interpolation, in C). A frame at a
/// time (<see cref="IYuv420P10FrameConverter"/>), each chroma row is filtered horizontally once, as swscale does.
/// </summary>
internal sealed partial class SwscaleYuv420P10 : IYuv420P10FrameConverter, IYuvChromaRows
{
    /// <summary>av_chroma_location_enum_to_pos for ffprobe's chroma_location names (null: unspecified, so center).</summary>
    static readonly Dictionary<string, (int H, int V)> ChromaPositions = new()
    {
        ["left"] = (0, 128),
        ["center"] = (128, 128),
        ["topleft"] = (0, 0),
        ["top"] = (128, 0),
        ["bottomleft"] = (0, 256),
        ["bottom"] = (128, 256),
    };

    readonly int _colorSpace, _hChrPos, _vChrPos;
    readonly bool _fullRange;

    // YSCALEYUV2RGBX's coefficients (ff_yuv2rgb_c_init_tables), as in SwscaleYuv420.
    readonly int _yCoeff, _yOffset, _vrCoeff, _ubCoeff, _ugCoeff, _vgCoeff;

    // The C output's tables: y_table (24-bit case), the per-chroma offsets into it (fill_table, fill_gv_table, indexed
    // by the chroma clipped to 0..255) and the luma offset yoffs.
    readonly byte[] _yTable = new byte[TablePlaneSize];
    readonly int[] _rV = new int[256], _gU = new int[256], _gV = new int[256], _bU = new int[256];
    readonly int _yoffs;

    Plan _plan;

    SwscaleYuv420P10(int colorSpace, bool fullRange, int hChrPos, int vChrPos, Plan plan)
    {
        (_colorSpace, _fullRange, _hChrPos, _vChrPos, _plan) = (colorSpace, fullRange, hChrPos, vChrPos, plan);
        var t = SwscaleYuv420.Coeffs[colorSpace];
        long crv = t[0], cbu = t[1], cgu = -t[2], cgv = -t[3], cy = 1 << 16, oy = 0;
        if (!fullRange)
        {
            cy = cy * 255 / 219;
            oy = 16 << 16;
        }
        else
        {
            crv = crv * 224 / 255;
            cbu = cbu * 224 / 255;
            cgu = cgu * 224 / 255;
            cgv = cgv * 224 / 255;
        }

        _yCoeff = SwscaleYuv420.RoundToInt16(cy * (1 << 13));
        _yOffset = SwscaleYuv420.RoundToInt16(oy * (1 << 3));
        _vrCoeff = SwscaleYuv420.RoundToInt16(crv * (1 << 13));
        _ubCoeff = SwscaleYuv420.RoundToInt16(cbu * (1 << 13));
        _ugCoeff = SwscaleYuv420.RoundToInt16(cgu * (1 << 13));
        _vgCoeff = SwscaleYuv420.RoundToInt16(cgv * (1 << 13));

        // The C tables: coefficients scaled by cy, a luma table with headroom, chroma offsets into it.
        crv = ((crv * (1 << 16)) + 0x8000) / Math.Max(cy, 1);
        cbu = ((cbu * (1 << 16)) + 0x8000) / Math.Max(cy, 1);
        cgu = ((cgu * (1 << 16)) + 0x8000) / Math.Max(cy, 1);
        cgv = ((cgv * (1 << 16)) + 0x8000) / Math.Max(cy, 1);
        long yb = -(384L << 16) - LumaHeadroom * cy - oy;
        for (int i = 0; i < TablePlaneSize; i++)
        {
            _yTable[i] = (byte)Math.Clamp((yb + 0x8000) >> 16, 0, 255);
            yb += cy;
        }

        _yoffs = (fullRange ? 384 : 326) + LumaHeadroom;
        for (int c = 0; c < 256; c++)
        {
            _rV[c] = (int)(-(crv >> 9) + ((c * crv) >> 16));
            _gU[c] = (int)(-(cgu >> 9) + ((c * cgu) >> 16));
            _gV[c] = (int)(-(cgv >> 9) + ((c * cgv) >> 16));
            _bU[c] = (int)(-(cbu >> 9) + ((c * cbu) >> 16));
        }
    }

    const int LumaHeadroom = 512, TablePlaneSize = 1024 + 2 * LumaHeadroom;

    /// <summary>A converter for this video, or null for colours, chroma locations or sizes it doesn't reproduce.</summary>
    internal static SwscaleYuv420P10? For(string colorSpace, bool fullRange, string? chromaLocation, int width, int height)
    {
        if (!SwscaleYuv420.ColorSpaces.TryGetValue(colorSpace, out int cs))
            return null;
        if (!ChromaPositions.TryGetValue(chromaLocation ?? "center", out var pos))
            return null;
        return Plan.For(width, height, pos.H, pos.V) is { } plan ? new SwscaleYuv420P10(cs, fullRange, pos.H, pos.V, plan) : null;
    }

    /// <summary>BT.601, limited range, centred chroma: what swscale uses for untagged video.</summary>
    internal static SwscaleYuv420P10 Untagged(int width, int height) =>
        For("unknown", false, null, width, height) ?? throw new ArgumentException($"FastYuv doesn't convert 10-bit 4:2:0 at {width}x{height}.");

    /// <inheritdoc/>
    public IYuv420P10Converter? ForVideo(string colorSpace, bool fullRange, string? chromaLocation, int width, int height) =>
        For(colorSpace, fullRange, chromaLocation, width, height);

    /// <summary>
    /// The filters for one frame size: swscale's horizontal chroma filter (chroma width to the same, 4 taps after
    /// x86's alignment) and vertical chroma filter (chroma height to the frame's). Luma is neither scaled nor shifted:
    /// 15 bits (<c>Y &lt;&lt; 5</c>), one vertical tap of 4096.
    /// </summary>
    sealed class Plan(int width, int height, SwscaleFilter h, SwscaleFilter v)
    {
        public readonly int Width = width, Height = height, ChromaWidth = (width + 1) / 2, ChromaHeight = (height + 1) / 2;
        public readonly SwscaleFilter H = h, V = v;

        public static Plan? For(int width, int height, int hChrPos, int vChrPos)
        {
            // Odd widths: swscale forces full chroma interpolation (C code) for RGB output.
            if (width % 2 != 0 || width < 2 || height < 3)
                return null;
            int cw = (width + 1) / 2, ch = (height + 1) / 2;
            long chrXInc = (((long)cw << 16) + (cw >> 1)) / cw, chrYInc = (((long)ch << 16) + (height >> 1)) / height;
            var h = SwscaleFilter.Bicubic(chrXInc, cw, cw, 4, 1 << 14, SwscaleFilter.LocalPos(1, hChrPos), SwscaleFilter.LocalPos(1, -513));
            var v = SwscaleFilter.Bicubic(chrYInc, ch, height, 2, 1 << 12, SwscaleFilter.LocalPos(1, vChrPos), SwscaleFilter.LocalPos(0, -513));

            // Tiny frames: a filter wider than its plane, or a vertical filter of one or two taps (swscale's
            // yuv2packed1/yuv2packed2 then, not ported).
            return h.Size <= cw && v.Size > 2 ? new Plan(width, height, h, v) : null;
        }
    }

    /// <inheritdoc/>
    public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow) =>
        Convert(yuv, width, height, [y], cols, bgrRow, packed: false);

    /// <inheritdoc/>
    // The vertical filter's taps for the row: as many chroma rows as the filter is long, from its position (SwscaleFilter
    // keeps them inside the plane).
    public (int First, int Count) ChromaRows(int width, int height, int chromaHeight, int y)
    {
        var plan = _plan;
        if (plan.Width != width || plan.Height != height)
            _plan = plan = Plan.For(width, height, _hChrPos, _vChrPos) ?? throw new ArgumentException($"FastYuv doesn't convert 10-bit 4:2:0 at {width}x{height}.");
        if ((uint)y >= (uint)height)
            throw new ArgumentOutOfRangeException(nameof(y));
        return (plan.V.Pos[y], plan.V.Size);
    }

    /// <inheritdoc/>
    public void FrameToBgr(ReadOnlySpan<byte> yuv, int width, int height, ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, Span<byte> bgr) =>
        Convert(yuv, width, height, rows, cols, bgr, packed: true);

    /// <summary>
    /// Converts <paramref name="rows"/> × <paramref name="cols"/>: into one row at the columns' own offsets (x * 3), or
    /// <paramref name="packed"/> (row i's column j at (i * cols.Length + j) * 3). As swscale, each chroma row the
    /// vertical filter reads is filtered horizontally once (at the columns' chroma samples), then each row's vertical
    /// filter and output read those.
    /// </summary>
    void Convert(ReadOnlySpan<byte> yuv, int width, int height, ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, Span<byte> bgr, bool packed)
    {
        var plan = _plan;
        if (plan.Width != width || plan.Height != height)
            _plan = plan = Plan.For(width, height, _hChrPos, _vChrPos) ?? throw new ArgumentException($"FastYuv doesn't convert 10-bit 4:2:0 at {width}x{height}.");
        if (yuv.Length < IYuv420P10Converter.FrameSize(width, height))
            throw new ArgumentException("Not a whole yuv420p10le frame of this size, or no such row.");

        int cw = plan.ChromaWidth, ch = plan.ChromaHeight, vSize = plan.V.Size, n = cols.Length;
        var pool = System.Buffers.ArrayPool<int>.Shared;
        int[] pairOf = pool.Rent(n), chromaCols = pool.Rent(n), slot = pool.Rent(ch), terms = pool.Rent(3 * Math.Max(n, 1));
        int[]? filtered = null;
        try
        {
            // Each pair of pixels shares its chroma: the chroma sample of each run of columns in one pair, and which
            // run each column is in.
            int pairs = 0, last = -1, end = 0;
            for (int j = 0; j < n; j++)
            {
                int i = cols[j] >> 1;
                if ((uint)i >= (uint)cw)
                    throw new ArgumentOutOfRangeException(nameof(cols));
                if (i != last)
                    chromaCols[pairs++] = last = i;
                pairOf[j] = pairs - 1;
                end = Math.Max(end, cols[j] + 1);
            }

            if (bgr.Length < (packed ? rows.Length * n : end) * 3)
                throw new ArgumentException("The output is shorter than the pixels converted.", nameof(bgr));

            // The chroma rows the rows' vertical filters read, each given a slot in the filtered rows.
            Array.Fill(slot, -1, 0, ch);
            int chromaRows = 0;
            foreach (int y in rows)
            {
                if ((uint)y >= (uint)height)
                    throw new ArgumentException("Not a whole yuv420p10le frame of this size, or no such row.");
                for (int j = 0; j < vSize; j++)
                {
                    if (slot[plan.V.Pos[y] + j] < 0)
                        slot[plan.V.Pos[y] + j] = chromaRows++;
                }
            }

            // Slot s: U's filtered samples at 2 * s * pairs, V's right after.
            filtered = pool.Rent(Math.Max(2 * chromaRows * pairs, 1));
            var pixels = PixelPlanFor(cols, chromaCols.AsSpan(0, pairs));
            ConvertRows(plan, pixels, yuv, width, height, rows, cols, bgr, packed, pairOf, chromaCols, pairs, slot, filtered, terms);
        }
        finally
        {
            pool.Return(pairOf);
            pool.Return(chromaCols);
            pool.Return(slot);
            pool.Return(terms);
            if (filtered is not null)
                pool.Return(filtered);
        }
    }

    /// <summary>
    /// <see cref="Convert"/>'s loops, given its buffers: the columns' chroma samples (<paramref name="chromaCols"/>,
    /// <paramref name="pairs"/> of them, and which one each column takes), the chroma rows' slots, the filtered rows and
    /// a row's chroma terms (<paramref name="terms"/>, 3 × pairs), and the pixels' plan (null: a pixel at a time). Apart
    /// from Convert's try/finally, so the JIT keeps the loops' locals in registers.
    /// </summary>
    unsafe void ConvertRows(Plan plan,
        PixelPlan? pixels,
        ReadOnlySpan<byte> yuv,
        int width,
        int height,
        ReadOnlySpan<int> rows,
        ReadOnlySpan<int> cols,
        Span<byte> bgr,
        bool packed,
        int[] pairOf,
        int[] chromaCols,
        int pairs,
        int[] slot,
        int[] filtered,
        int[] terms)
    {
        int cw = plan.ChromaWidth, ch = plan.ChromaHeight, hSize = plan.H.Size, vSize = plan.V.Size, n = cols.Length;
        int* rowOffset = stackalloc int[vSize];
        int* vCoeff = stackalloc int[vSize];
        fixed (byte* frame = yuv, output = bgr)
        {
            fixed (short* hCoeffs = plan.H.Coeffs)
            {
                fixed (int* hPos = plan.H.Pos, f = filtered, tb = terms, pairOfColumn = pairOf, chromaCol = chromaCols, col = cols)
                {
                    // The filters keep every read inside the planes (initFilter's border handling), so pointers.
                    ushort* lumaPlane = (ushort*)frame, uPlane = lumaPlane + width * height, vPlane = uPlane + cw * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        if (slot[c] < 0)
                            continue;
                        int* fu = f + 2 * slot[c] * pairs;
                        HorizontalRow(uPlane + c * cw, hCoeffs, hPos, hSize, chromaCol, pairs, fu);
                        HorizontalRow(vPlane + c * cw, hCoeffs, hPos, hSize, chromaCol, pairs, fu + pairs);
                    }

                    int* tg = tb + pairs, tr = tg + pairs;
                    for (int r = 0; r < rows.Length; r++)
                    {
                        int y = rows[r];

                        // swscale.c switches to the C output for the last two rows ("looks like we can't use MMX here
                        // without overwriting this array's tail").
                        bool asm = y < height - 2;

                        // The vertical filter's chroma rows (offsets into the filtered rows) and coefficients for this row.
                        for (int t = 0; t < vSize; t++)
                        {
                            rowOffset[t] = 2 * slot[plan.V.Pos[y] + t] * pairs;
                            vCoeff[t] = plan.V.Coeffs[y * vSize + t];
                        }

                        // Each pair's chroma terms once (b, g and r: tb, tg, tr), then each pixel's output.
                        ushort* luma = lumaPlane + y * width;
                        byte* rowOutput = packed ? output + r * n * 3 : output;
                        if (asm)
                        {
                            ChromaTermsAsm(f, rowOffset, vCoeff, vSize, pairs, tb, tg, tr);
                            if (pixels is not null)
                                PixelsByPlan(pixels, luma, col, pairOfColumn, tb, tg, tr, rowOutput, packed);
                            else
                                PixelsAsm(luma, col, pairOfColumn, n, tb, tg, tr, rowOutput, packed);
                        }
                        else
                        {
                            ChromaTermsC(f, rowOffset, vCoeff, vSize, pairs, tb, tg, tr);
                            PixelsC(luma, col, pairOfColumn, n, tb, tg, tr, rowOutput, packed);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// hScale16To15 of one chroma row at the pairs' chroma samples (<paramref name="chromaCol"/>) into
    /// <paramref name="dst"/>.
    /// </summary>
    static unsafe void HorizontalRow(ushort* src, short* hCoeffs, int* hPos, int hSize, int* chromaCol, int pairs, int* dst)
    {
        int k = 0;
        if (hSize == 4 && Ssse3.IsSupported)
        {
            // Four samples at a time: each one's four taps (10-bit samples and coefficients as words) multiplied and
            // summed pairwise (pmaddwd), then the pairs (phaddd): the scalar loop's integers.
            for (; k <= pairs - 4; k += 4)
            {
                int i0 = chromaCol[k], i1 = chromaCol[k + 1], i2 = chromaCol[k + 2], i3 = chromaCol[k + 3];
                var a = Sse2.MultiplyAddAdjacent(
                    Vector128.Create(Unsafe.ReadUnaligned<long>(src + hPos[i0]), Unsafe.ReadUnaligned<long>(src + hPos[i1])).AsInt16(),
                    Vector128.Create(Unsafe.ReadUnaligned<long>(hCoeffs + i0 * 4), Unsafe.ReadUnaligned<long>(hCoeffs + i1 * 4)).AsInt16());
                var b = Sse2.MultiplyAddAdjacent(
                    Vector128.Create(Unsafe.ReadUnaligned<long>(src + hPos[i2]), Unsafe.ReadUnaligned<long>(src + hPos[i3])).AsInt16(),
                    Vector128.Create(Unsafe.ReadUnaligned<long>(hCoeffs + i2 * 4), Unsafe.ReadUnaligned<long>(hCoeffs + i3 * 4)).AsInt16());
                Vector128.Min(Ssse3.HorizontalAdd(a, b) >> 9, Vector128.Create(0x7FFF)).Store(dst + k);
            }
        }

        for (; k < pairs; k++)
        {
            int i = chromaCol[k];
            dst[k] = HScale(src + hPos[i], hCoeffs + i * hSize, hSize);
        }
    }

    /// <summary>
    /// The MMXEXT output for a row's pixels: hScale16To15 with the one-tap luma filter (Y &lt;&lt; 5: filter 16384,
    /// &gt;&gt; 9, at most 15 bits), one vertical tap of 4096 from vRounder, then YSCALEYUV2RGBX with the pair's chroma
    /// terms (<paramref name="tb"/>, <paramref name="tg"/>, <paramref name="tr"/>), paddw and packuswb. Column j's pixel
    /// at j * 3 when <paramref name="packed"/>, else at its x * 3.
    /// </summary>
    unsafe void PixelsAsm(ushort* luma, int* cols, int* pairOf, int n, int* tb, int* tg, int* tr, byte* output, bool packed)
    {
        int yOffset = _yOffset, yCoeff = _yCoeff;
        for (int j = 0; j < n; j++)
        {
            int x = cols[j], k = pairOf[j];
            Pixel(luma, x, tb[k], tg[k], tr[k], yOffset, yCoeff, output + Offset(packed, j, x));
        }
    }

    /// <summary>One pixel of <see cref="PixelsAsm"/>: luma x's term and the pair's chroma terms, to <paramref name="o"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void Pixel(ushort* luma, int x, int tb, int tg, int tr, int yOffset, int yCoeff, byte* o)
    {
        int y15 = Math.Min(luma[x] << 5, 0x7FFF);
        int yy = MulHi((short)(4 + (y15 >> 4) - yOffset), yCoeff);
        o[0] = PackUs(yy + tb);
        o[1] = PackUs(yy + tg);
        o[2] = PackUs(yy + tr);
    }

    /// <summary>
    /// yuv2rgb_X_c_template's output for a row's pixels (the last two rows): the luma's one tap of 4096 from 1 &lt;&lt; 18,
    /// shifted by 19, then the tables at the pair's offsets (<paramref name="tb"/>, <paramref name="tg"/>,
    /// <paramref name="tr"/>). Placed as <see cref="PixelsAsm"/> places them.
    /// </summary>
    unsafe void PixelsC(ushort* luma, int* cols, int* pairOf, int n, int* tb, int* tg, int* tr, byte* output, bool packed)
    {
        byte[] table = _yTable;
        for (int j = 0; j < n; j++)
        {
            int x = cols[j], k = pairOf[j];
            int y15 = Math.Min(luma[x] << 5, 0x7FFF);
            int yy = ((1 << 18) + y15 * 4096) >> 19;
            byte* o = output + Offset(packed, j, x);
            o[0] = table[tb[k] + yy];
            o[1] = table[tg[k] + yy];
            o[2] = table[tr[k] + yy];
        }
    }

    /// <summary>Where column j (at x) goes: packed, its index's place; else its own column's.</summary>
    static int Offset(bool packed, int j, int x) => packed ? j * 3 : x * 3;

    // x86's vRounder: what the MMXEXT vertical scaler's sums start from.
    const int VRounder = 4;

    /// <summary>
    /// YSCALEYUV2PACKEDX and YSCALEYUV2RGBX's chroma terms for each pair: from vRounder, each tap's pmulhw added with paddw
    /// (16 bits, wrapping), then the coefficients. Four pairs at a time where vectors are fast (the same integers).
    /// </summary>
    unsafe void ChromaTermsAsm(int* f, int* rowOffset, int* vCoeff, int vSize, int pairs, int* tb, int* tg, int* tr)
    {
        int k = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> ub = Vector128.Create(_ubCoeff), ug = Vector128.Create(_ugCoeff), vg = Vector128.Create(_vgCoeff), vr = Vector128.Create(_vrCoeff);
            for (; k <= pairs - 4; k += 4)
            {
                Vector128<int> ua = Vector128.Create(VRounder), va = ua;
                for (int t = 0; t < vSize; t++)
                {
                    var c = Vector128.Create(vCoeff[t]);
                    int* fu = f + rowOffset[t] + k;
                    ua = Short(ua + MulHi(Vector128.Load(fu), c));
                    va = Short(va + MulHi(Vector128.Load(fu + pairs), c));
                }

                Vector128<int> uu = Short(ua - Vector128.Create(1024)), vv = Short(va - Vector128.Create(1024));
                MulHi(uu, ub).Store(tb + k);
                Short(MulHi(uu, ug) + MulHi(vv, vg)).Store(tg + k);
                MulHi(vv, vr).Store(tr + k);
            }
        }

        for (; k < pairs; k++)
        {
            int ua = VRounder, va = VRounder;
            for (int t = 0; t < vSize; t++)
            {
                int* fu = f + rowOffset[t];
                ua = (short)(ua + MulHi(fu[k], vCoeff[t]));
                va = (short)(va + MulHi(fu[pairs + k], vCoeff[t]));
            }

            int uu = (short)(ua - 1024), vv = (short)(va - 1024);
            tb[k] = MulHi(uu, _ubCoeff);
            tg[k] = (short)(MulHi(uu, _ugCoeff) + MulHi(vv, _vgCoeff));
            tr[k] = MulHi(vv, _vrCoeff);
        }
    }

    /// <summary>
    /// yuv2rgb_X_c_template's chroma terms for each pair: 32-bit sums from 1 &lt;&lt; 18, shifted by 19, then offsets into
    /// the tables (chroma clipped). The last two rows only.
    /// </summary>
    unsafe void ChromaTermsC(int* f, int* rowOffset, int* vCoeff, int vSize, int pairs, int* tb, int* tg, int* tr)
    {
        for (int k = 0; k < pairs; k++)
        {
            int uc = 1 << 18, vc = 1 << 18;
            for (int t = 0; t < vSize; t++)
            {
                int* fu = f + rowOffset[t];
                uc += fu[k] * vCoeff[t];
                vc += fu[pairs + k] * vCoeff[t];
            }

            int cu = Math.Clamp(uc >> 19, 0, 255), cv = Math.Clamp(vc >> 19, 0, 255);
            tb[k] = _yoffs + _bU[cu];
            tg[k] = _yoffs + _gU[cu] + _gV[cv];
            tr[k] = _yoffs + _rV[cv];
        }
    }

    /// <summary>pmulhw on each lane's low 16 bits: the high half of the signed product.</summary>
    static Vector128<int> MulHi(Vector128<int> a, Vector128<int> b) => (Short(a) * Short(b)) >> 16;

    /// <summary>Each lane's low 16 bits, sign-extended: (short) per lane.</summary>
    static Vector128<int> Short(Vector128<int> v) => (v << 16) >> 16;

    /// <summary>hScale16To15_c for 10-bit input: the filter's sum shifted by depth - 1 (9), at most 15 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe int HScale(ushort* src, short* coeffs, int size)
    {
        int val = size == 4 ? src[0] * coeffs[0] + src[1] * coeffs[1] + src[2] * coeffs[2] + src[3] * coeffs[3] : 0;
        for (int j = size == 4 ? 4 : 0; j < size; j++)
            val += src[j] * coeffs[j];
        return Math.Min(val >> 9, 0x7FFF);
    }

    static int MulHi(int a, int b) => (short)a * (short)b >> 16; // pmulhw

    /// <summary>paddw, then packuswb: the low 16 bits, signed, saturated to 0..255 (without branches: video saturates often).</summary>
    static byte PackUs(int v)
    {
        v = (short)v;
        v &= ~(v >> 31);
        return (byte)(v | ((255 - v) >> 31));
    }
}
