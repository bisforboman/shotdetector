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
    readonly BgrConverter _bgr = new(decoder) { KeepFirstSize = true }; // OpenCV's buffer: the stream's size
    readonly int _turn = turn;
    byte[] _turned = [];
    SwsContext* _toSize;
    int _firstWidth = -1, _firstHeight;

    /// <summary>Whether the current frame has the first frame's size (the stream's size, to OpenCV).</summary>
    public bool HasFirstSize
    {
        get
        {
            if (_firstWidth < 0)
            {
                (_firstWidth, _firstHeight) = (_d.Width, _d.Height);

                // The converter takes its size from the first frame it converts; on the YUV path that may be a later
                // frame of another size, so it sees this one now (no rows converted).
                _bgr.Convert([], out _);
            }

            return _d.Width == _firstWidth && _d.Height == _firstHeight;
        }
    }

    /// <summary>The whole converted frame, turned: its bytes and row stride.</summary>
    ReadOnlySpan<byte> Converted(out int stride)
    {
        var bgr = _bgr.Convert(out int s);
        if (_turn == 0)
        {
            stride = s;
            return bgr;
        }

        // The converter scales every frame to the first one's size (KeepFirstSize): read it at that size, not at the
        // current frame's, which a stream changing size mid-way would make larger than the buffer.
        _ = HasFirstSize;
        int w = _firstWidth, h = _firstHeight, ow = _turn == 180 ? w : h, oh = _turn == 180 ? h : w;
        if (_turned.Length != ow * oh * 3)
            _turned = new byte[ow * oh * 3];
        fixed (byte* src = bgr)
        {
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
            {
                for (int x = 0; x < ow; x++)
                {
                    var (sx, sy) = Source(x, y, pw, ph);
                    dst[o++] = plane[sy * stride + sx];
                }
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

    /// <summary>
    /// The converted frame resized by <paramref name="resizer"/> (its source the crop at <paramref name="crop"/>), read
    /// straight from the converter's rows: only <paramref name="rows"/> (the crop's, offset) are converted.
    /// </summary>
    public void WriteResized(byte[] dst, CvResize resizer, int[] rows, (int X, int Y) crop)
    {
        // Turned, the rows read are columns of the decoded frame: the whole frame then.
        var bgr = _turn == 0 ? _bgr.Convert(rows, out int stride) : Converted(out stride);
        resizer.ResizeRows(bgr, stride, crop.X, crop.Y, dst);
    }

    /// <summary>
    /// The converted frame's <paramref name="rows"/> (the resize's source rows, offset by the crop), each the crop's
    /// width, one after another: for <see cref="CvResize.ResizePackedRows"/> on another thread. Only those rows are
    /// converted.
    /// </summary>
    public void WriteRows(byte[] dst, int[] rows, (int X, int Width) crop)
    {
        // Turned, the rows read are columns of the decoded frame: the whole frame then.
        var bgr = _turn == 0 ? _bgr.Convert(rows, out int stride) : Converted(out stride);
        int n = crop.Width * 3;
        for (int i = 0; i < rows.Length; i++)
            bgr.Slice(rows[i] * stride + crop.X * 3, n).CopyTo(dst.AsSpan(i * n, n));
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
            _toSize = ffmpeg.sws_getContext(crop.Width,
                crop.Height,
                AVPixelFormat.AV_PIX_FMT_BGR24,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGR24,
                (int)SwsFlags.SWS_BILINEAR,
                null,
                null,
                null);
            if (_toSize == null)
                throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Can't set up the scaler.");
        }

        int[] srcStride = [stride, 0, 0, 0];
        fixed (byte* bgr = converted)
        {
            fixed (byte* d = dst)
            {
                byte*[] src = [bgr + crop.Y * stride + crop.X * 3, null, null, null];
                byte*[] dstPlanes = [d, null, null, null];
                int[] dstStride = [width * 3, 0, 0, 0];
                ffmpeg.sws_scale(_toSize, src, srcStride, 0, crop.Height, dstPlanes, dstStride);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_toSize != null)
        {
            ffmpeg.sws_freeContext(_toSize);
            _toSize = null;
        }

        _bgr.Dispose();
    }
}
