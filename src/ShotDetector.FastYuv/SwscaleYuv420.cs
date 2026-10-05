// SPDX-License-Identifier: LGPL-2.1-or-later
// Port of FFmpeg libswscale's yuv420p -> BGR24 conversion (libswscale/yuv2rgb.c,
// libswscale/x86/yuv_2_rgb.asm). Copyright (C) 2001-2007 Michael Niedermayer,
// (C) 2009-2010 Konstantin Shishkov. Licensed under the GNU Lesser General Public License,
// version 2.1 or (at your option) any later version; see COPYING.LGPL.

namespace ShotDetector.FastYuv;

/// <summary>
/// yuv420p → BGR exactly as OpenCV gets it from swscale: for even-height yuv420p at the same size,
/// swscale uses its unscaled converter, on x86 ff_yuv_420_bgr24_ssse3 (libswscale/x86/yuv_2_rgb.asm),
/// with coefficients from ff_yuv2rgb_c_init_tables for the video's colour matrix and range. Chroma is
/// not interpolated: each 2x2 block shares one U and V. Verified against ffmpeg for all 2^24
/// (Y, U, V) and every supported matrix and range.
/// Lets ffmpeg skip colour conversion and lets ShotDetector convert only the pixels cv2.resize reads:
/// pass an instance as <see cref="DetectionOptions.Yuv420Converter"/>.
/// </summary>
public sealed class SwscaleYuv420 : IYuv420Converter
{
    /// <summary>ff_yuv2rgb_coeffs (crv, cbu, cgu, cgv), indexed by AVColorSpace.</summary>
    static readonly int[][] Coeffs =
    [
        [104597, 132201, 25675, 53279], // 0 no sequence_display_extension
        [117489, 138438, 13975, 34925], // 1 BT.709
        [104597, 132201, 25675, 53279], // 2 unspecified
        [104597, 132201, 25675, 53279], // 3 reserved
        [104448, 132798, 24759, 53109], // 4 FCC
        [104597, 132201, 25675, 53279], // 5 BT.470BG / BT.601-625
        [104597, 132201, 25675, 53279], // 6 SMPTE 170M / BT.601-525
        [117579, 136230, 16907, 35559], // 7 SMPTE 240M
        [0, 0, 0, 0],                   // 8 YCgCo (not supported)
        [110013, 140363, 12277, 42626], // 9 BT.2020 non-constant
        [110013, 140363, 12277, 42626], // 10 BT.2020 constant
    ];

    /// <summary>ffprobe's color_space names → AVColorSpace.</summary>
    static readonly Dictionary<string, int> ColorSpaces = new()
    {
        ["bt709"] = 1, ["unknown"] = 2, [""] = 2, ["fcc"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6,
        ["smpte240m"] = 7, ["bt2020nc"] = 9, ["bt2020c"] = 10,
    };

    // Each term of the asm is a 16-bit multiply-high of one shifted input, so precompute them per value.
    readonly int[] _yTerm = new int[256], _ubTerm = new int[256], _ugTerm = new int[256], _vrTerm = new int[256], _vgTerm = new int[256];

    const int ClipOffset = 1024;
    static readonly byte[] Clip = Enumerable.Range(-ClipOffset, 2 * ClipOffset).Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();

    /// <summary>BT.601, limited range: what swscale uses for untagged video.</summary>
    public SwscaleYuv420() : this(5, fullRange: false) { }

    SwscaleYuv420(int colorSpace, bool fullRange)
    {
        // ff_yuv2rgb_c_init_tables with brightness 0, contrast and saturation 1 (1 << 16).
        var t = Coeffs[colorSpace];
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
        int yCoeff = RoundToInt16(cy * (1 << 13)), yOffset = RoundToInt16(oy * (1 << 3));
        int vrCoeff = RoundToInt16(crv * (1 << 13)), ubCoeff = RoundToInt16(cbu * (1 << 13));
        int ugCoeff = RoundToInt16(cgu * (1 << 13)), vgCoeff = RoundToInt16(cgv * (1 << 13));
        for (int i = 0; i < 256; i++)
        {
            int y = (short)((i << 3) - yOffset), c = Sat16((i << 3) - 1024);
            _yTerm[i] = MulHi(y, yCoeff);
            _ubTerm[i] = MulHi(c, ubCoeff);
            _ugTerm[i] = MulHi(c, ugCoeff);
            _vrTerm[i] = MulHi(c, vrCoeff);
            _vgTerm[i] = MulHi(c, vgCoeff);
        }
    }

    /// <inheritdoc/>
    public IYuv420Converter? ForColor(string colorSpace, bool fullRange) =>
        ColorSpaces.TryGetValue(colorSpace, out int cs) ? new SwscaleYuv420(cs, fullRange) : null;

    static int RoundToInt16(long f) => (short)Math.Clamp((f + (1 << 15)) >> 16, -0x8000, 0x7FFF);
    static int Sat16(int v) => Math.Clamp(v, short.MinValue, short.MaxValue);
    static int MulHi(int a, int b) => (short)a * (short)b >> 16; // pmulhw

    /// <inheritdoc/>
    public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow)
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
            int yy = _yTerm[luma[x]] + ClipOffset;
            int U = u[x >> 1], V = v[x >> 1];
            int o = x * 3;
            bgrRow[o] = clip[_ubTerm[U] + yy];
            bgrRow[o + 1] = clip[_ugTerm[U] + _vgTerm[V] + yy];
            bgrRow[o + 2] = clip[_vrTerm[V] + yy];
        }
    }
}
