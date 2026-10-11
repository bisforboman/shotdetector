using FFmpeg.AutoGen;
using FrameReader;

namespace ShotDetector;

/// <summary>
/// Writes FrameReader's decoded frames in exactly the layout ffmpeg's command line sends for a
/// <see cref="FramePipeline"/>: the same swscale conversion (FrameReader's <see cref="BgrConverter"/>: bicubic flag,
/// the frame's colour tags), so the same bytes, without a second process or a pipe. With <paramref name="turn"/>, the
/// converted frame is rotated clockwise by it (90, 180 or 270), as the executable's transpose/hflip,vflip on bgr24 and
/// OpenCV's cv::rotate do.
/// </summary>
internal sealed unsafe class FrameWriter(FrameDecoder decoder, int turn = 0) : IDisposable
{
    readonly FrameDecoder _d = decoder;
    readonly BgrConverter _bgr = new(decoder);
    readonly int _turn = turn;
    byte[] _turned = [];
    SwsContext* _toSize;

    /// <summary>The whole converted frame, turned: its bytes and row stride.</summary>
    ReadOnlySpan<byte> Converted(out int stride)
    {
        var bgr = _bgr.Convert(out int s);
        if (_turn == 0)
        {
            stride = s;
            return bgr;
        }
        int w = _d.Width, h = _d.Height, ow = _turn == 180 ? w : h, oh = _turn == 180 ? h : w;
        if (_turned.Length != ow * oh * 3)
            _turned = new byte[ow * oh * 3];
        fixed (byte* src = bgr)
        fixed (byte* dst = _turned)
        {
            for (int y = 0; y < oh; y++)
            {
                byte* d = dst + (long)y * ow * 3;
                for (int x = 0; x < ow; x++, d += 3)
                {
                    var (sx, sy) = Source(x, y, w, h);
                    byte* p = src + (long)sy * s + sx * 3;
                    (d[0], d[1], d[2]) = (p[0], p[1], p[2]);
                }
            }
        }
        stride = ow * 3;
        return _turned;
    }

    /// <summary>
    /// The frame's yuv420p planes, packed (Y, then U, then V), as -pix_fmt yuv420p rawvideo sends them; turned, each
    /// plane as ffmpeg's transpose/hflip,vflip turns it.
    /// </summary>
    public void WriteYuv420(byte[] dst)
    {
        int w = _d.Width, h = _d.Height, cw = (w + 1) / 2, ch = (h + 1) / 2, o = 0;
        for (int p = 0; p < 3; p++)
        {
            int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
            var plane = _d.GetPlane(p, out int stride);
            if (_turn == 0)
            {
                for (int y = 0; y < ph; y++, o += pw)
                    plane.Slice(y * stride, pw).CopyTo(dst.AsSpan(o));
                continue;
            }
            int ow = _turn == 180 ? pw : ph, oh = _turn == 180 ? ph : pw;
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    var (sx, sy) = Source(x, y, pw, ph);
                    dst[o++] = plane[sy * stride + sx];
                }
        }
    }

    /// <summary>
    /// The source pixel of output (x, y) turned by _turn, as ffmpeg's filters do: transpose=clock (output row y is source
    /// column y, read bottom up), transpose=cclock (source column w-1-y, top down), hflip,vflip (the opposite pixel).
    /// </summary>
    (int X, int Y) Source(int x, int y, int w, int h) => _turn switch
    {
        90 => (y, h - 1 - x),
        270 => (w - 1 - y, x),
        _ => (w - 1 - x, h - 1 - y),
    };

    /// <summary>Only the pixels the resize reads, at columns × rows of the converted frame (what the remap filter sends).</summary>
    public void WriteSampledBgr(byte[] dst, int[] columns, int[] rows)
    {
        // Turned, the sampled rows are columns of the decoded frame: the whole frame then.
        var bgr = _turn == 0 ? _bgr.Convert(rows, out int stride) : Converted(out stride);
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
        var bgr = Converted(out int stride);
        for (int y = 0; y < crop.Height; y++)
            bgr.Slice((crop.Y + y) * stride + crop.X * 3, crop.Width * 3).CopyTo(dst.AsSpan(y * crop.Width * 3));
    }

    /// <summary>The converted frame's crop region scaled to width × height with swscale's bilinear (…,scale=W:H:flags=bilinear).</summary>
    public void WriteScaledBgr(byte[] dst, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
        var converted = Converted(out int stride);
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
