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

namespace ShotDetector.FastYuv;

/// <summary>
/// yuv420p10le → BGR as x86's swscale converts it at the same size with SWS_BICUBIC (what OpenCV and ShotDetector's
/// default path ask for). There is no unscaled converter for 10-bit, so swscale scales: chroma horizontally (a 4-tap
/// bicubic phase shift unless the chroma is centred) to 15 bits, then vertically (4 bicubic taps, 2x) together with
/// the conversion. On x86 that is MMXEXT inline assembly, which accumulates the taps in 16 bits (each product's high
/// half, wrapping) and doesn't clip chroma that overshoots; the last two rows take swscale's C output instead, whose
/// tables clip it. Even widths only (an odd width takes another path, full chroma interpolation, in C).
/// </summary>
internal sealed class SwscaleYuv420P10 : IYuv420P10Converter
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
    public unsafe void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow)
    {
        var plan = _plan;
        if (plan.Width != width || plan.Height != height)
            _plan = plan = Plan.For(width, height, _hChrPos, _vChrPos) ?? throw new ArgumentException($"FastYuv doesn't convert 10-bit 4:2:0 at {width}x{height}.");
        if (yuv.Length < IYuv420P10Converter.FrameSize(width, height) || (uint)y >= (uint)height)
            throw new ArgumentException("Not a whole yuv420p10le frame of this size, or no such row.");

        int cw = plan.ChromaWidth, chromaSize = cw * plan.ChromaHeight, hSize = plan.H.Size, vSize = plan.V.Size;
        var luma = MemoryMarshal.Cast<byte, ushort>(yuv).Slice(y * width, width);

        // swscale.c switches to the C output for the last two rows ("looks like we can't use MMX here without
        // overwriting this array's tail").
        bool asm = y < height - 2;

        // The vertical filter's chroma rows (offsets into a plane) and coefficients for this row.
        int* rowOffset = stackalloc int[vSize];
        int* vCoeff = stackalloc int[vSize];
        for (int j = 0; j < vSize; j++)
        {
            rowOffset[j] = (plan.V.Pos[y] + j) * cw;
            vCoeff[j] = plan.V.Coeffs[y * vSize + j];
        }

        fixed (byte* frame = yuv)
        {
            fixed (short* hCoeffs = plan.H.Coeffs)
            {
                fixed (int* hPos = plan.H.Pos)
                {
                    ushort* uPlane = (ushort*)frame + width * height, vPlane = uPlane + chromaSize;
                    int last = -1, b = 0, g = 0, r = 0;
                    foreach (int x in cols)
                    {
                        // Each pair of pixels shares its chroma: computed once per pair. The filters keep every read inside the
                        // planes (initFilter's border handling), so pointers.
                        int i = x >> 1;
                        if (i != last)
                        {
                            if ((uint)i >= (uint)cw)
                                throw new ArgumentOutOfRangeException(nameof(cols));
                            last = i;
                            short* hc = hCoeffs + i * hSize;
                            ushort* u = uPlane + hPos[i], v = vPlane + hPos[i];
                            if (asm)
                            {
                                // YSCALEYUV2PACKEDX: from vRounder (4), each tap's pmulhw added with paddw (16 bits, wrapping).
                                int ua = 4, va = 4;
                                for (int j = 0; j < vSize; j++)
                                {
                                    ua = (short)(ua + MulHi(HScale(u + rowOffset[j], hc, hSize), vCoeff[j]));
                                    va = (short)(va + MulHi(HScale(v + rowOffset[j], hc, hSize), vCoeff[j]));
                                }

                                // YSCALEYUV2RGBX's chroma terms.
                                int uu = (short)(ua - 1024), vv = (short)(va - 1024);
                                b = MulHi(uu, _ubCoeff);
                                g = (short)(MulHi(uu, _ugCoeff) + MulHi(vv, _vgCoeff));
                                r = MulHi(vv, _vrCoeff);
                            }
                            else
                            {
                                // yuv2rgb_X_c_template: 32-bit sums from 1 << 18, shifted by 19, then the tables (chroma clipped).
                                int uc = 1 << 18, vc = 1 << 18;
                                for (int j = 0; j < vSize; j++)
                                {
                                    uc += HScale(u + rowOffset[j], hc, hSize) * vCoeff[j];
                                    vc += HScale(v + rowOffset[j], hc, hSize) * vCoeff[j];
                                }

                                int cu = Math.Clamp(uc >> 19, 0, 255), cv = Math.Clamp(vc >> 19, 0, 255);
                                b = _yoffs + _bU[cu];
                                g = _yoffs + _gU[cu] + _gV[cv];
                                r = _yoffs + _rV[cv];
                            }
                        }

                        // hScale16To15 with the one-tap luma filter: Y << 5 (filter 16384, >> 9), at most 15 bits.
                        int y15 = Math.Min(luma[x] << 5, 0x7FFF);
                        int o = x * 3;
                        if (asm)
                        {
                            // One vertical tap of 4096 from vRounder; then YSCALEYUV2RGBX, paddw and packuswb.
                            int yy = MulHi((short)(4 + (y15 >> 4) - _yOffset), _yCoeff);
                            bgrRow[o] = PackUs(yy + b);
                            bgrRow[o + 1] = PackUs(yy + g);
                            bgrRow[o + 2] = PackUs(yy + r);
                        }
                        else
                        {
                            int yy = ((1 << 18) + y15 * 4096) >> 19;
                            bgrRow[o] = _yTable[b + yy];
                            bgrRow[o + 1] = _yTable[g + yy];
                            bgrRow[o + 2] = _yTable[r + yy];
                        }
                    }
                }
            }
        }
    }

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

    static byte PackUs(int v) => (byte)Math.Clamp((int)(short)v, 0, 255); // paddw, then packuswb
}
