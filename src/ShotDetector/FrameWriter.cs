using FFmpeg.AutoGen;
using FrameReader;

namespace ShotDetector;

/// <summary>
/// Writes FrameReader's decoded frames in exactly the layout ffmpeg's command line sends for a
/// <see cref="FramePipeline"/>: the same swscale conversion (FrameReader's <see cref="BgrConverter"/>: bicubic flag,
/// the frame's colour tags), so the same bytes, without a second process or a pipe.
/// </summary>
internal sealed unsafe class FrameWriter(FrameDecoder decoder) : IDisposable
{
    readonly FrameDecoder _d = decoder;
    readonly BgrConverter _bgr = new(decoder);
    SwsContext* _toSize;

    /// <summary>The frame's yuv420p planes, packed (Y, then U, then V), as -pix_fmt yuv420p rawvideo sends them.</summary>
    public void WriteYuv420(byte[] dst)
    {
        int w = _d.Width, h = _d.Height, cw = (w + 1) / 2, ch = (h + 1) / 2, o = 0;
        for (int p = 0; p < 3; p++)
        {
            int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
            var plane = _d.GetPlane(p, out int stride);
            for (int y = 0; y < ph; y++, o += pw)
                plane.Slice(y * stride, pw).CopyTo(dst.AsSpan(o));
        }
    }

    /// <summary>Only the pixels the resize reads, at columns × rows of the converted frame (what the remap filter sends).</summary>
    public void WriteSampledBgr(byte[] dst, int[] columns, int[] rows)
    {
        var bgr = _bgr.Convert(rows, out int stride);
        if (!ReferenceEquals(columns, _runsFor))
            (_runs, _runsFor) = (Runs(columns), columns);
        int o = 0;
        foreach (int r in rows)
        {
            var row = bgr.Slice(r * stride);
            foreach (var (start, length) in _runs!)
            {
                row.Slice(3 * start, 3 * length).CopyTo(dst.AsSpan(o));
                o += 3 * length;
            }
        }
    }

    (int Start, int Length)[]? _runs;
    int[]? _runsFor;

    /// <summary>Ascending columns as runs of consecutive ones, copied a run at a time.</summary>
    static (int Start, int Length)[] Runs(int[] columns)
    {
        var runs = new List<(int, int)>();
        for (int i = 0; i < columns.Length;)
        {
            int j = i + 1;
            while (j < columns.Length && columns[j] == columns[j - 1] + 1)
                j++;
            runs.Add((columns[i], j - i));
            i = j;
        }
        return [.. runs];
    }

    /// <summary>The converted frame's crop region, packed BGR (scale,format=bgr24,crop).</summary>
    public void WriteBgr(byte[] dst, (int X, int Y, int Width, int Height) crop)
    {
        var bgr = _bgr.Convert(out int stride);
        for (int y = 0; y < crop.Height; y++)
            bgr.Slice((crop.Y + y) * stride + crop.X * 3, crop.Width * 3).CopyTo(dst.AsSpan(y * crop.Width * 3));
    }

    /// <summary>The converted frame's crop region scaled to width × height with swscale's bilinear (…,scale=W:H:flags=bilinear).</summary>
    public void WriteScaledBgr(byte[] dst, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
        var converted = _bgr.Convert(out int stride);
        if (_toSize == null)
        {
            _toSize = ffmpeg.sws_getContext(crop.Width, crop.Height, AVPixelFormat.AV_PIX_FMT_BGR24, width, height,
                AVPixelFormat.AV_PIX_FMT_BGR24, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_toSize == null)
                throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Can't set up the scaler.");
        }
        int[] srcStride = [stride, 0, 0, 0];
        fixed (byte* bgr = converted)
        fixed (byte* d = dst)
        {
            byte*[] src = [bgr + crop.Y * stride + crop.X * 3, null, null, null];
            byte*[] dstPlanes = [d, null, null, null];
            int[] dstStride = [width * 3, 0, 0, 0];
            ffmpeg.sws_scale(_toSize, src, srcStride, 0, crop.Height, dstPlanes, dstStride);
        }
    }

    public void Dispose()
    {
        if (_toSize != null) { ffmpeg.sws_freeContext(_toSize); _toSize = null; }
        _bgr.Dispose();
    }
}
