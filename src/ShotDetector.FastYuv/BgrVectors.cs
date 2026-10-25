// SPDX-License-Identifier: LGPL-2.1-or-later
// Part of the ports of FFmpeg libswscale's YUV -> BGR24 conversions (SwscaleYuv420.cs, SwscaleYuv420P10.cs): their
// last step, 16 pixels at a time. Licensed under the GNU Lesser General Public License, version 2.1 or (at your option)
// any later version; see COPYING.LGPL.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ShotDetector.FastYuv;

/// <summary>The converters' Vector128 blocks' last step: 16-bit sums clamped to bytes, and B, G, R interleaved.</summary>
internal static class BgrVectors
{
    /// <summary>Two vectors of sums, each clamped to 0..255 (packuswb, Clip), as 16 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Clamp(Vector128<short> a, Vector128<short> b)
    {
        var max = Vector128.Create((short)255);
        return Vector128.Narrow(
            Vector128.Min(Vector128.Max(a, Vector128<short>.Zero), max).AsUInt16(),
            Vector128.Min(Vector128.Max(b, Vector128<short>.Zero), max).AsUInt16());
    }

    /// <summary>16 pixels' blue, green and red as packed BGR, 48 bytes in three vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Interleave(Vector128<byte> b, Vector128<byte> g, Vector128<byte> r, out Vector128<byte> o0, out Vector128<byte> o1, out Vector128<byte> o2)
    {
        // An index of 0xFF gives 0.
        const byte Z = 0xFF;
        o0 = Vector128.Shuffle(b, Vector128.Create(0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z, Z, 5))
            | Vector128.Shuffle(g, Vector128.Create(Z, 0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z, Z))
            | Vector128.Shuffle(r, Vector128.Create(Z, Z, 0, Z, Z, 1, Z, Z, 2, Z, Z, 3, Z, Z, 4, Z));
        o1 = Vector128.Shuffle(b, Vector128.Create(Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z, 10, Z))
            | Vector128.Shuffle(g, Vector128.Create(5, Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z, 10))
            | Vector128.Shuffle(r, Vector128.Create(Z, 5, Z, Z, 6, Z, Z, 7, Z, Z, 8, Z, Z, 9, Z, Z));
        o2 = Vector128.Shuffle(b, Vector128.Create(Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15, Z, Z))
            | Vector128.Shuffle(g, Vector128.Create(Z, Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15, Z))
            | Vector128.Shuffle(r, Vector128.Create(10, Z, Z, 11, Z, Z, 12, Z, Z, 13, Z, Z, 14, Z, Z, 15));
    }

    /// <summary>8 values, each twice: the first four in the low vector, the last four in the high one (a chroma term per pixel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<short> Lo, Vector128<short> Hi) Double(Vector128<short> v) =>
        (Vector128.Shuffle(v, Vector128.Create((short)0, 0, 1, 1, 2, 2, 3, 3)), Vector128.Shuffle(v, Vector128.Create((short)4, 4, 5, 5, 6, 6, 7, 7)));
}
