using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// FFmpeg 8.1's <c>blackdetect</c> on one frame: its threshold for a pixel (<see cref="Threshold"/>) and its count of
/// the luma samples at or under it (<see cref="Count"/>), the frame black when that count over its pixels reaches
/// pic_th. Only the luma plane, at any depth blackdetect takes, so a caller who wants nothing else (a black frame check
/// beside other work) needn't measure the rest of <see cref="PictureMeter"/>'s figures; the meter counts with the same
/// threshold.
/// </summary>
public static class BlackPixels
{
    /// <summary>
    /// blackdetect's pixel_black_th_i: <paramref name="pixelThreshold"/> × the depth's maximum in full range, 16 + it ×
    /// 219 (scaled to the depth) in limited range, truncated (37 and 25 at 8 bits for its default 0.10, 151 and 102 at 10).
    /// </summary>
    /// <param name="bitDepth">The luma's bits per sample, 8 to 16.</param>
    /// <param name="fullRange">Full range (colour range pc, or a yuvj format).</param>
    /// <param name="pixelThreshold">blackdetect's pix_th, 0 to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitDepth"/> isn't 8 to 16, or <paramref name="pixelThreshold"/> isn't 0 to 1.</exception>
    public static uint Threshold(int bitDepth, bool fullRange, double pixelThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitDepth, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitDepth, 16);
        if (!(pixelThreshold is >= 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(pixelThreshold), pixelThreshold, "blackdetect's pix_th is 0 to 1.");
        int max = (1 << bitDepth) - 1, factor = 1 << (bitDepth - 8);
        return (uint)(fullRange ? pixelThreshold * max : (16 * factor) + (pixelThreshold * (235 - 16) * factor));
    }

    /// <summary>The luma samples at or under <paramref name="threshold"/> (blackdetect's count of black pixels).</summary>
    /// <param name="luma">The luma plane: <paramref name="height"/> rows <paramref name="stride"/> bytes apart (the last may end at its width).</param>
    /// <param name="stride">Bytes from one row to the next.</param>
    /// <param name="width">The frame's width in pixels.</param>
    /// <param name="height">The frame's height in pixels.</param>
    /// <param name="bitDepth">Bits per sample: 8 (a byte each), or 9 to 16 (16-bit little-endian).</param>
    /// <param name="threshold">blackdetect's threshold (<see cref="Threshold"/>).</param>
    /// <exception cref="ArgumentException">The plane is smaller than the size says.</exception>
    public static int Count(ReadOnlySpan<byte> luma, int stride, int width, int height, int bitDepth, uint threshold)
    {
        int bytes = bitDepth > 8 ? 2 : 1;
        if (width <= 0 || height <= 0 || stride < width * bytes || luma.Length < ((long)stride * (height - 1)) + (width * bytes))
            throw new ArgumentException($"The plane is smaller than {width}x{height} samples of {bytes} bytes with a stride of {stride}.", nameof(luma));
        long count = 0;
        if (bytes == 1)
        {
            if (threshold >= 255)
                return width * height;
            var limit = Vector128.Create((byte)threshold);
            for (int r = 0; r < height; r++)
            {
                var row = luma.Slice(r * stride, width);
                var lanes = Vector128<byte>.Zero;
                int x = 0, n = 0;
                for (; x + 16 <= width; x += 16)
                {
                    // A compare is all ones where true: subtracting it counts, each byte lane up to 255 times.
                    lanes -= Vector128.LessThanOrEqual(Vector128.Create(row.Slice(x, 16)), limit);
                    if (++n == 255)
                    {
                        count += Sum(lanes);
                        lanes = Vector128<byte>.Zero;
                        n = 0;
                    }
                }

                count += Sum(lanes);
                for (; x < width; x++)
                {
                    if (row[x] <= threshold)
                        count++;
                }
            }
        }
        else
        {
            for (int r = 0; r < height; r++)
            {
                foreach (ushort q in MemoryMarshal.Cast<byte, ushort>(luma.Slice(r * stride, 2 * width)))
                {
                    if (q <= threshold)
                        count++;
                }
            }
        }

        return (int)count;
    }

    /// <summary>
    /// The current frame of <paramref name="decoder"/> as blackdetect judges it, if its pixel format is one blackdetect
    /// takes as it is (planar YUV, gray, NV12 or NV21 of 8 to 16 bits: the luma in the first plane): its black pixels and
    /// pixels. Null for any other format (RGB, packed YUV: ffmpeg converts those before blackdetect).
    /// </summary>
    /// <param name="decoder">A decoder on a frame (<see cref="FrameDecoder.Next"/>).</param>
    /// <param name="pixelThreshold">blackdetect's pix_th, 0 to 1.</param>
    public static unsafe (int Black, int Pixels)? Of(FrameDecoder decoder, double pixelThreshold)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var luma = decoder.GetPlane(0, out int stride);
        return Of(decoder.CurrentFrame, luma, stride, pixelThreshold);
    }

    /// <summary><see cref="Of(FrameDecoder, double)"/> for a frame taken from a decoder.</summary>
    /// <param name="frame">A decoded frame.</param>
    /// <param name="pixelThreshold">blackdetect's pix_th, 0 to 1.</param>
    public static unsafe (int Black, int Pixels)? Of(DecodedFrame frame, double pixelThreshold)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var luma = frame.GetPlane(0, out int stride);
        return Of(frame.Frame, luma, stride, pixelThreshold);
    }

    static unsafe (int Black, int Pixels)? Of(AVFrame* f, ReadOnlySpan<byte> luma, int stride, double pixelThreshold)
    {
        var format = (AVPixelFormat)f->format;
        if (!Takes(format))
            return null;
        var d = ffmpeg.av_pix_fmt_desc_get(format);
        int depth = d->comp[0].depth;
        string name = ffmpeg.av_get_pix_fmt_name(format) ?? "";
        bool full = f->color_range == AVColorRange.AVCOL_RANGE_JPEG || name.StartsWith("yuvj", StringComparison.Ordinal);
        return (Count(luma, stride, f->width, f->height, depth, Threshold(depth, full, pixelThreshold)), f->width * f->height);
    }

    /// <summary>Whether blackdetect takes the format as it is (its yuv_formats; FFmpeg 8.1).</summary>
    static unsafe bool Takes(AVPixelFormat format)
    {
        var d = ffmpeg.av_pix_fmt_desc_get(format);
        if (d == null || (d->flags & ffmpeg.AV_PIX_FMT_FLAG_BE) != 0 || (d->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0)
            return false;
        string name = ffmpeg.av_get_pix_fmt_name(format) ?? "";
        return name is "gray" or "gray10le" or "gray12le" or "gray14le" or "gray16le" or "nv12" or "nv21"
            || (name.StartsWith("yuv", StringComparison.Ordinal) && (d->flags & ffmpeg.AV_PIX_FMT_FLAG_PLANAR) != 0 && Listed(name));
    }

    // blackdetect's yuv_formats and yuva_formats (little-endian on a little-endian build), by name.
    static bool Listed(string name) => name is "yuv410p" or "yuv411p" or "yuv420p" or "yuv422p" or "yuv440p" or "yuv444p"
        or "yuvj411p" or "yuvj420p" or "yuvj422p" or "yuvj444p" or "yuvj440p"
        or "yuv420p9le" or "yuv422p9le" or "yuv444p9le" or "yuv420p10le" or "yuv422p10le" or "yuv444p10le" or "yuv440p10le"
        or "yuv444p12le" or "yuv422p12le" or "yuv420p12le" or "yuv440p12le" or "yuv444p14le" or "yuv422p14le" or "yuv420p14le"
        or "yuv420p16le" or "yuv422p16le" or "yuv444p16le"
        or "yuva420p" or "yuva422p" or "yuva444p" or "yuva444p9le" or "yuva444p10le" or "yuva444p12le" or "yuva444p16le"
        or "yuva422p9le" or "yuva422p10le" or "yuva422p12le" or "yuva422p16le" or "yuva420p9le" or "yuva420p10le" or "yuva420p16le";

    static int Sum(Vector128<byte> v)
    {
        var (a, b) = Vector128.Widen(v);
        return Vector128.Sum(a + b);
    }
}
