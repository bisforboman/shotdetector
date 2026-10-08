// Port of FFmpeg's yadif deinterlacer (libavfilter/vf_yadif.c, yadif_common.c), mode 0 (one frame out per frame in):
// the arithmetic of filter_line_c / filter_edges for one row, so ShotDetector can deinterlace only the rows it uses.
//
// Copyright (C) 2006-2011 Michael Niedermayer <michaelni@gmx.at>
//               2010      James Darnley <james.darnley@gmail.com>
//
// This file is part of ShotDetector.FastYuv, a port of parts of FFmpeg, and like FFmpeg is free software; you can
// redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation; either version 2.1 of the License, or (at your option) any later version. It is distributed
// WITHOUT ANY WARRANTY; see COPYING.LGPL.

using System.Runtime.Intrinsics;

namespace ShotDetector.FastYuv;

/// <summary>
/// FFmpeg's yadif (mode 0, send_frame) for single rows: bit-exact with the filter, so ShotDetector's in-process
/// deinterlacing computes only the rows its resize reads. Pass an instance as <see cref="DetectionOptions.Yadif"/>.
/// </summary>
public sealed unsafe class Yadif : IYadif
{
    /// <inheritdoc/>
    public void FilterRow(nint dst, nint prev, nint cur, nint next, int srcStride, int dstStride, int width, int height, int y, int bytesPerSample)
    {
        // As filter_slice: the rows above and below (mirrored at the edges), and the simple mode on the second and
        // second-to-last rows, where yÂ±2 would leave the plane.
        int prefs = y + 1 < height ? srcStride : -srcStride;
        int mrefs = y > 0 ? -srcStride : srcStride;
        int mode = y == 1 || y + 2 == height ? 2 : 0;
        long row = (long)y * srcStride;
        if (bytesPerSample == 1)
            Row((byte*)dst + (long)y * dstStride, (byte*)prev + row, (byte*)cur + row, (byte*)next + row, width, prefs, mrefs, mode);
        else
            Row16((ushort*)((byte*)dst + (long)y * dstStride), (ushort*)((byte*)prev + row), (ushort*)((byte*)cur + row),
                (ushort*)((byte*)next + row), width, prefs / 2, mrefs / 2, mode);
    }

    // filter_line_c (x in [3, w-3), with the spatial check) and filter_edges (the 3 pixels at each end, without it).
    // Mode 0's temporal pair: prev2 = prev, next2 = cur. For 9 to 16 bits this is filter_line_c_16bit, the C code
    // (FFmpeg 8.1's x86 SIMD for those depths gives a different result on every run).
    static void Row(byte* dst, byte* prev, byte* cur, byte* next, int w, int prefs, int mrefs, int mode)
    {
        int x = 0;
        // 16 pixels at a time where the spatial check applies and mode 0's y±2 rows exist; the ends and the rows next
        // to the plane's top and bottom one at a time.
        if (mode == 0 && Vector128.IsHardwareAccelerated && w >= 3 + 16 + 3)
        {
            for (; x < 3; x++)
                dst[x] = (byte)Pixel8(prev + x, cur + x, next + x, prefs, mrefs, mode, false);
            for (; x + 16 <= w - 3; x += 16)
                Pixels16(dst + x, prev + x, cur + x, next + x, prefs, mrefs);
        }
        for (; x < w; x++)
            dst[x] = (byte)Pixel8(prev + x, cur + x, next + x, prefs, mrefs, mode, x >= 3 && x < w - 3);
    }

    /// <summary>yadif's FILTER (mode 0, with the spatial check) for 16 pixels: the same integer arithmetic in lanes.</summary>
    static void Pixels16(byte* dst, byte* prev, byte* cur, byte* next, int prefs, int mrefs)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var r = Half256(prev, cur, next, prefs, mrefs, 0).AsUInt16();
            Vector256.Narrow(r, r).GetLower().Store(dst);
            return;
        }
        // 0..255 each, so narrowing keeps every value.
        Vector128.Narrow(Half(prev, cur, next, prefs, mrefs, 0).AsUInt16(), Half(prev, cur, next, prefs, mrefs, 8).AsUInt16()).Store(dst);
    }

    /// <summary>8 pixels starting at <paramref name="o"/> (0 or 8), as shorts.</summary>
    static Vector128<short> Half(byte* prev, byte* cur, byte* next, int prefs, int mrefs, int o)
    {
        // Exactly 8 bytes, widened: nothing read past the pixels used (the plane's last rows end at its buffer's end).
        static Vector128<short> L(byte* p) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)p).AsByte()).AsInt16();
        static Vector128<short> Abs(Vector128<short> v) => Vector128.Abs(v);
        prev += o; cur += o; next += o;
        var c = L(cur + mrefs);
        var e = L(cur + prefs);
        var p0 = L(prev);
        var c0 = L(cur); // next2 = cur
        var d = (p0 + c0) >> 1;
        var diff = Vector128.Max(Vector128.Max(Abs(p0 - c0) >> 1, (Abs(L(prev + mrefs) - c) + Abs(L(prev + prefs) - e)) >> 1),
            (Abs(L(next + mrefs) - c) + Abs(L(next + prefs) - e)) >> 1);
        var spatialPred = (c + e) >> 1;
        var score = Abs(L(cur + mrefs - 1) - L(cur + prefs - 1)) + Abs(c - e) + Abs(L(cur + mrefs + 1) - L(cur + prefs + 1)) - Vector128<short>.One;

        // CHECK(-1), and CHECK(-2) only where -1 won.
        var s1 = Abs(L(cur + mrefs - 2) - L(cur + prefs)) + Abs(L(cur + mrefs - 1) - L(cur + prefs + 1)) + Abs(c - L(cur + prefs + 2));
        var m1 = Vector128.LessThan(s1, score);
        score = Vector128.ConditionalSelect(m1, s1, score);
        spatialPred = Vector128.ConditionalSelect(m1, (L(cur + mrefs - 1) + L(cur + prefs + 1)) >> 1, spatialPred);
        var s2 = Abs(L(cur + mrefs - 3) - L(cur + prefs + 1)) + Abs(L(cur + mrefs - 2) - L(cur + prefs + 2)) + Abs(L(cur + mrefs - 1) - L(cur + prefs + 3));
        var m2 = m1 & Vector128.LessThan(s2, score);
        score = Vector128.ConditionalSelect(m2, s2, score);
        spatialPred = Vector128.ConditionalSelect(m2, (L(cur + mrefs - 2) + L(cur + prefs + 2)) >> 1, spatialPred);

        // CHECK(1), and CHECK(2) only where 1 won.
        var s3 = Abs(c - L(cur + prefs - 2)) + Abs(L(cur + mrefs + 1) - L(cur + prefs - 1)) + Abs(L(cur + mrefs + 2) - e);
        var m3 = Vector128.LessThan(s3, score);
        score = Vector128.ConditionalSelect(m3, s3, score);
        spatialPred = Vector128.ConditionalSelect(m3, (L(cur + mrefs + 1) + L(cur + prefs - 1)) >> 1, spatialPred);
        var s4 = Abs(L(cur + mrefs + 1) - L(cur + prefs - 3)) + Abs(L(cur + mrefs + 2) - L(cur + prefs - 2)) + Abs(L(cur + mrefs + 3) - L(cur + prefs - 1));
        var m4 = m3 & Vector128.LessThan(s4, score);
        spatialPred = Vector128.ConditionalSelect(m4, (L(cur + mrefs + 2) + L(cur + prefs - 2)) >> 1, spatialPred);

        // Mode 0: the y±2 rows of prev and cur.
        var b = (L(prev + 2 * mrefs) + L(cur + 2 * mrefs)) >> 1;
        var f = (L(prev + 2 * prefs) + L(cur + 2 * prefs)) >> 1;
        var max = Vector128.Max(Vector128.Max(d - e, d - c), Vector128.Min(b - c, f - e));
        var min = Vector128.Min(Vector128.Min(d - e, d - c), Vector128.Max(b - c, f - e));
        diff = Vector128.Max(Vector128.Max(diff, min), -max);

        // diff >= 0, so yadif's two-sided clamp is a min/max.
        return Vector128.Min(Vector128.Max(spatialPred, d - diff), d + diff);
    }

    /// <summary>16 pixels as shorts (AVX2).</summary>
    static Vector256<short> Half256(byte* prev, byte* cur, byte* next, int prefs, int mrefs, int o)
    {
        // Exactly 16 bytes, widened.
        static Vector256<short> L(byte* p)
        {
            var v = Vector128.Load(p);
            return Vector256.Create(Vector128.WidenLower(v), Vector128.WidenUpper(v)).AsInt16();
        }
        static Vector256<short> Abs(Vector256<short> v) => Vector256.Abs(v);
        prev += o; cur += o; next += o;
        var c = L(cur + mrefs);
        var e = L(cur + prefs);
        var p0 = L(prev);
        var c0 = L(cur); // next2 = cur
        var d = (p0 + c0) >> 1;
        var diff = Vector256.Max(Vector256.Max(Abs(p0 - c0) >> 1, (Abs(L(prev + mrefs) - c) + Abs(L(prev + prefs) - e)) >> 1),
            (Abs(L(next + mrefs) - c) + Abs(L(next + prefs) - e)) >> 1);
        var spatialPred = (c + e) >> 1;
        var score = Abs(L(cur + mrefs - 1) - L(cur + prefs - 1)) + Abs(c - e) + Abs(L(cur + mrefs + 1) - L(cur + prefs + 1)) - Vector256<short>.One;

        // CHECK(-1), and CHECK(-2) only where -1 won.
        var s1 = Abs(L(cur + mrefs - 2) - L(cur + prefs)) + Abs(L(cur + mrefs - 1) - L(cur + prefs + 1)) + Abs(c - L(cur + prefs + 2));
        var m1 = Vector256.LessThan(s1, score);
        score = Vector256.ConditionalSelect(m1, s1, score);
        spatialPred = Vector256.ConditionalSelect(m1, (L(cur + mrefs - 1) + L(cur + prefs + 1)) >> 1, spatialPred);
        var s2 = Abs(L(cur + mrefs - 3) - L(cur + prefs + 1)) + Abs(L(cur + mrefs - 2) - L(cur + prefs + 2)) + Abs(L(cur + mrefs - 1) - L(cur + prefs + 3));
        var m2 = m1 & Vector256.LessThan(s2, score);
        score = Vector256.ConditionalSelect(m2, s2, score);
        spatialPred = Vector256.ConditionalSelect(m2, (L(cur + mrefs - 2) + L(cur + prefs + 2)) >> 1, spatialPred);

        // CHECK(1), and CHECK(2) only where 1 won.
        var s3 = Abs(c - L(cur + prefs - 2)) + Abs(L(cur + mrefs + 1) - L(cur + prefs - 1)) + Abs(L(cur + mrefs + 2) - e);
        var m3 = Vector256.LessThan(s3, score);
        score = Vector256.ConditionalSelect(m3, s3, score);
        spatialPred = Vector256.ConditionalSelect(m3, (L(cur + mrefs + 1) + L(cur + prefs - 1)) >> 1, spatialPred);
        var s4 = Abs(L(cur + mrefs + 1) - L(cur + prefs - 3)) + Abs(L(cur + mrefs + 2) - L(cur + prefs - 2)) + Abs(L(cur + mrefs + 3) - L(cur + prefs - 1));
        var m4 = m3 & Vector256.LessThan(s4, score);
        spatialPred = Vector256.ConditionalSelect(m4, (L(cur + mrefs + 2) + L(cur + prefs - 2)) >> 1, spatialPred);

        // Mode 0: the y±2 rows of prev and cur.
        var b = (L(prev + 2 * mrefs) + L(cur + 2 * mrefs)) >> 1;
        var f = (L(prev + 2 * prefs) + L(cur + 2 * prefs)) >> 1;
        var max = Vector256.Max(Vector256.Max(d - e, d - c), Vector256.Min(b - c, f - e));
        var min = Vector256.Min(Vector256.Min(d - e, d - c), Vector256.Max(b - c, f - e));
        diff = Vector256.Max(Vector256.Max(diff, min), -max);

        // diff >= 0, so yadif's two-sided clamp is a min/max.
        return Vector256.Min(Vector256.Max(spatialPred, d - diff), d + diff);
    }

    static void Row16(ushort* dst, ushort* prev, ushort* cur, ushort* next, int w, int prefs, int mrefs, int mode)
    {
        for (int x = 0; x < w; x++)
            dst[x] = (ushort)Pixel16(prev + x, cur + x, next + x, prefs, mrefs, mode, x >= 3 && x < w - 3);
    }

    /// <summary>yadif's FILTER macro for one 8-bit pixel.</summary>
    static int Pixel8(byte* prev, byte* cur, byte* next, int prefs, int mrefs, int mode, bool notEdge)
    {
        byte* prev2 = prev, next2 = cur;
        int c = cur[mrefs], d = (prev2[0] + next2[0]) >> 1, e = cur[prefs];
        int temporalDiff0 = Math.Abs(prev2[0] - next2[0]);
        int temporalDiff1 = (Math.Abs(prev[mrefs] - c) + Math.Abs(prev[prefs] - e)) >> 1;
        int temporalDiff2 = (Math.Abs(next[mrefs] - c) + Math.Abs(next[prefs] - e)) >> 1;
        int diff = Math.Max(Math.Max(temporalDiff0 >> 1, temporalDiff1), temporalDiff2);
        int spatialPred = (c + e) >> 1;
        if (notEdge)
        {
            int spatialScore = Math.Abs(cur[mrefs - 1] - cur[prefs - 1]) + Math.Abs(c - e) + Math.Abs(cur[mrefs + 1] - cur[prefs + 1]) - 1;
            // CHECK(-1) then CHECK(-2) only if -1 won; CHECK(1) then CHECK(2) only if 1 won.
            if (Check(cur, mrefs, prefs, -1, ref spatialScore, ref spatialPred))
                Check(cur, mrefs, prefs, -2, ref spatialScore, ref spatialPred);
            if (Check(cur, mrefs, prefs, 1, ref spatialScore, ref spatialPred))
                Check(cur, mrefs, prefs, 2, ref spatialScore, ref spatialPred);
        }
        if ((mode & 2) == 0)
        {
            int b = (prev2[2 * mrefs] + next2[2 * mrefs]) >> 1;
            int f = (prev2[2 * prefs] + next2[2 * prefs]) >> 1;
            int max = Math.Max(Math.Max(d - e, d - c), Math.Min(b - c, f - e));
            int min = Math.Min(Math.Min(d - e, d - c), Math.Max(b - c, f - e));
            diff = Math.Max(Math.Max(diff, min), -max);
        }
        if (spatialPred > d + diff)
            spatialPred = d + diff;
        else if (spatialPred < d - diff)
            spatialPred = d - diff;
        return spatialPred;
    }

    static bool Check(byte* cur, int mrefs, int prefs, int j, ref int spatialScore, ref int spatialPred)
    {
        int score = Math.Abs(cur[mrefs - 1 + j] - cur[prefs - 1 - j]) + Math.Abs(cur[mrefs + j] - cur[prefs - j])
            + Math.Abs(cur[mrefs + 1 + j] - cur[prefs + 1 - j]);
        if (score >= spatialScore)
            return false;
        spatialScore = score;
        spatialPred = (cur[mrefs + j] + cur[prefs - j]) >> 1;
        return true;
    }

    /// <summary>yadif's FILTER macro for one 9- to 16-bit pixel.</summary>
    static int Pixel16(ushort* prev, ushort* cur, ushort* next, int prefs, int mrefs, int mode, bool notEdge)
    {
        ushort* prev2 = prev, next2 = cur;
        int c = cur[mrefs], d = (prev2[0] + next2[0]) >> 1, e = cur[prefs];
        int temporalDiff0 = Math.Abs(prev2[0] - next2[0]);
        int temporalDiff1 = (Math.Abs(prev[mrefs] - c) + Math.Abs(prev[prefs] - e)) >> 1;
        int temporalDiff2 = (Math.Abs(next[mrefs] - c) + Math.Abs(next[prefs] - e)) >> 1;
        int diff = Math.Max(Math.Max(temporalDiff0 >> 1, temporalDiff1), temporalDiff2);
        int spatialPred = (c + e) >> 1;
        if (notEdge)
        {
            int spatialScore = Math.Abs(cur[mrefs - 1] - cur[prefs - 1]) + Math.Abs(c - e) + Math.Abs(cur[mrefs + 1] - cur[prefs + 1]) - 1;
            if (Check16(cur, mrefs, prefs, -1, ref spatialScore, ref spatialPred))
                Check16(cur, mrefs, prefs, -2, ref spatialScore, ref spatialPred);
            if (Check16(cur, mrefs, prefs, 1, ref spatialScore, ref spatialPred))
                Check16(cur, mrefs, prefs, 2, ref spatialScore, ref spatialPred);
        }
        if ((mode & 2) == 0)
        {
            int b = (prev2[2 * mrefs] + next2[2 * mrefs]) >> 1;
            int f = (prev2[2 * prefs] + next2[2 * prefs]) >> 1;
            int max = Math.Max(Math.Max(d - e, d - c), Math.Min(b - c, f - e));
            int min = Math.Min(Math.Min(d - e, d - c), Math.Max(b - c, f - e));
            diff = Math.Max(Math.Max(diff, min), -max);
        }
        if (spatialPred > d + diff)
            spatialPred = d + diff;
        else if (spatialPred < d - diff)
            spatialPred = d - diff;
        return spatialPred;
    }

    static bool Check16(ushort* cur, int mrefs, int prefs, int j, ref int spatialScore, ref int spatialPred)
    {
        int score = Math.Abs(cur[mrefs - 1 + j] - cur[prefs - 1 - j]) + Math.Abs(cur[mrefs + j] - cur[prefs - j])
            + Math.Abs(cur[mrefs + 1 + j] - cur[prefs + 1 - j]);
        if (score >= spatialScore)
            return false;
        spatialScore = score;
        spatialPred = (cur[mrefs + j] + cur[prefs - j]) >> 1;
        return true;
    }
}
