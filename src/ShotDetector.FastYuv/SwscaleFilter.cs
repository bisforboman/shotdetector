// SPDX-License-Identifier: LGPL-2.1-or-later
// Port of FFmpeg libswscale's filter set-up (libswscale/utils.c, initFilter and get_local_pos), its bicubic case.
// Copyright (C) 2001-2003 Michael Niedermayer, (C) 2024 Niklas Haas. Licensed under the GNU Lesser General Public
// License, version 2.1 or (at your option) any later version; see COPYING.LGPL.

namespace ShotDetector.FastYuv;

/// <summary>
/// A swscale scaling filter: for each output sample, the first input sample it reads (<see cref="Pos"/>) and
/// <see cref="Size"/> coefficients from there (<see cref="Coeffs"/>, <c>one</c> being 1.0).
/// </summary>
internal sealed record SwscaleFilter(short[] Coeffs, int[] Pos, int Size)
{
    /// <summary>
    /// initFilter for SWS_BICUBIC (default parameters: B 0, C 0.6), no source or destination filter, as x86 sets it up:
    /// <paramref name="filterAlign"/> 4 horizontally and 2 vertically, a one-tap filter left unaligned (MMX's "special
    /// case for unscaled vertical filtering"). NEON aligns the same. Positions are in 1/256 of a sample (get_local_pos).
    /// </summary>
    public static SwscaleFilter Bicubic(long xInc, int srcW, int dstW, int filterAlign, int one, int srcPos, int dstPos)
    {
        long fone = 1L << (54 - Math.Min(Log2(srcW / dstW), 8));
        int filterSize;
        long[] filter;
        int[] filterPos = new int[dstW];
        if (Math.Abs(xInc - 0x10000) < 10 && srcPos == dstPos)
        {
            // Unscaled.
            filterSize = 1;
            filter = new long[dstW];
            for (int i = 0; i < dstW; i++)
            {
                filter[i] = fone;
                filterPos[i] = i;
            }
        }
        else
        {
            const int sizeFactor = 4; // bicubic
            filterSize = xInc <= 1 << 16 ? 1 + sizeFactor : 1 + (sizeFactor * srcW + dstW - 1) / dstW;
            filterSize = Math.Max(Math.Min(filterSize, srcW - 2), 1);
            filter = new long[dstW * filterSize];
            long xDstInSrc = ((dstPos * xInc) >> 7) - ((srcPos * 0x10000L) >> 7);
            const long b = 0, c = (long)(0.6 * (1 << 24));
            for (int i = 0; i < dstW; i++)
            {
                int xx = (int)((xDstInSrc - (filterSize - 2) * (1L << 16)) / (1 << 17));
                filterPos[i] = xx;
                for (int j = 0; j < filterSize; j++)
                {
                    long d = Math.Abs((long)xx * (1 << 17) - xDstInSrc) << 13;
                    if (xInc > 1 << 16)
                        d = d * dstW / srcW;
                    long coeff;
                    if (d >= 1L << 31)
                    {
                        coeff = 0;
                    }
                    else
                    {
                        long dd = (d * d) >> 30, ddd = (dd * d) >> 30;
                        coeff = d < 1L << 30
                            ? (12 * (1L << 24) - 9 * b - 6 * c) * ddd + (-18 * (1L << 24) + 12 * b + 6 * c) * dd + (6 * (1L << 24) - 2 * b) * (1L << 30)
                            : (-b - 6 * c) * ddd + (6 * b + 30 * c) * dd + (-12 * b - 48 * c) * d + (8 * b + 24 * c) * (1L << 30);
                    }

                    filter[i * filterSize + j] = coeff / ((1L << 54) / fone);
                    xx++;
                }

                xDstInSrc += 2 * xInc;
            }
        }

        // Reduce: drop near-zero coefficients on the left (shifting the position) and count those on the right.
        double cutOffMax = SwsMaxReduceCutoff * fone;
        int minFilterSize = 0;
        for (int i = dstW - 1; i >= 0; i--)
        {
            int min = filterSize;
            long cutOff = 0;
            for (int j = 0; j < filterSize; j++)
            {
                cutOff += Math.Abs(filter[i * filterSize]);
                if (cutOff > cutOffMax || (i < dstW - 1 && filterPos[i] >= filterPos[i + 1]))
                    break;
                for (int k = 1; k < filterSize; k++)
                    filter[i * filterSize + k - 1] = filter[i * filterSize + k];
                filter[i * filterSize + filterSize - 1] = 0;
                filterPos[i]++;
            }

            cutOff = 0;
            for (int j = filterSize - 1; j > 0; j--)
            {
                cutOff += Math.Abs(filter[i * filterSize + j]);
                if (cutOff > cutOffMax)
                    break;
                min--;
            }

            minFilterSize = Math.Max(minFilterSize, min);
        }

        // x86 (MMX) and NEON: a one-tap vertical filter stays one tap.
        if (minFilterSize == 1 && filterAlign == 2)
            filterAlign = 1;
        int size = (minFilterSize + filterAlign - 1) & ~(filterAlign - 1);
        var reduced = new long[dstW * size];
        for (int i = 0; i < dstW; i++)
        {
            for (int j = 0; j < size && j < filterSize; j++)
                reduced[i * size + j] = filter[i * filterSize + j];
        }

        // Borders: coefficients before the first sample move onto it, those after the last onto the last.
        for (int i = 0; i < dstW; i++)
        {
            var f = reduced.AsSpan(i * size, size);
            if (filterPos[i] < 0)
            {
                for (int j = 1; j < size; j++)
                {
                    int left = Math.Max(j + filterPos[i], 0);
                    f[left] += f[j];
                    f[j] = 0;
                }

                filterPos[i] = 0;
            }

            if (filterPos[i] + size > srcW)
            {
                int shift = filterPos[i] + Math.Min(size - srcW, 0);
                long acc = 0;
                for (int j = size - 1; j >= 0; j--)
                {
                    if (filterPos[i] + j >= srcW)
                    {
                        acc += f[j];
                        f[j] = 0;
                    }
                }

                for (int j = size - 1; j >= 0; j--)
                    f[j] = j < shift ? 0 : f[j - shift];
                filterPos[i] -= shift;
                f[srcW - 1 - filterPos[i]] += acc;
            }
        }

        // Normalize to one, carrying each coefficient's rounding error into the next.
        var coeffs = new short[dstW * size];
        for (int i = 0; i < dstW; i++)
        {
            var f = reduced.AsSpan(i * size, size);
            long sum = 0, error = 0;
            foreach (long v in f)
                sum += v;
            sum = (sum + one / 2) / one;
            if (sum == 0)
                sum = 1; // "zero vector in scaling"
            for (int j = 0; j < size; j++)
            {
                long v = f[j] + error;
                long intV = v >= 0 ? (v + (sum >> 1)) / sum : (v - (sum >> 1)) / sum; // ROUNDED_DIV
                coeffs[i * size + j] = (short)intV;
                error = v - intV * sum;
            }
        }

        return new SwscaleFilter(coeffs, filterPos, size);
    }

    /// <summary>
    /// get_local_pos: a chroma position (1/256 of a luma sample from the left or top, -513 for none) in the
    /// subsampled plane's own grid, relative to its ideal left edge.
    /// </summary>
    public static int LocalPos(int chromaSubsample, int pos)
    {
        if (pos == -1 || pos <= -513)
            pos = (128 << chromaSubsample) - 128;
        return (pos + 128) >> chromaSubsample;
    }

    const double SwsMaxReduceCutoff = 0.002;

    static int Log2(int v) => v <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);
}
