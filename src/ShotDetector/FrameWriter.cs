using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FrameReader;

namespace ShotDetector;

/// <summary>
/// Writes FrameReader's decoded frames in exactly the layout ffmpeg's command line sends for a
/// <see cref="FramePipeline"/>: the same swscale conversion (bicubic flag, the frame's colour tags), so the same bytes,
/// without a second process or a pipe.
/// </summary>
internal sealed unsafe class FrameWriter(FrameDecoder decoder) : IDisposable
{
    readonly FrameDecoder _d = decoder;
    AVFrame* _bgr;
    SwsContext* _toBgr, _toSize, _toRows;
    (int W, int H, int Format, AVColorSpace Space, AVColorRange Range) _rowsFor;

    /// <summary>The frame's yuv420p planes, packed (Y, then U, then V), as -pix_fmt yuv420p rawvideo sends them.</summary>
    public void WriteYuv420(byte[] dst)
    {
        _d.EnsureRows(0, _d.Frame->height);
        int w = _d.Frame->width, h = _d.Frame->height, cw = (w + 1) / 2, ch = (h + 1) / 2, o = 0;
        fixed (byte* d = dst)
        {
            for (int p = 0; p < 3; p++)
            {
                int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
                byte* src = _d.Frame->data[(uint)p];
                int stride = _d.Frame->linesize[(uint)p];
                for (int y = 0; y < ph; y++, o += pw)
                    Buffer.MemoryCopy(src + y * stride, d + o, pw, pw);
            }
        }
    }

    /// <summary>Only the pixels the resize reads, at columns × rows of the converted frame (what the remap filter sends).</summary>
    public void WriteSampledBgr(byte[] dst, int[] columns, int[] rows)
    {
        byte* bgr = ToBgr(out int stride, rows);
        if (!ReferenceEquals(columns, _runsFor))
            (_runs, _runsFor) = (Runs(columns), columns);
        fixed (byte* d = dst)
        {
            byte* o = d;
            foreach (int r in rows)
            {
                byte* row = bgr + r * stride;
                foreach (var (start, length) in _runs!)
                {
                    Buffer.MemoryCopy(row + 3 * start, o, 3 * length, 3 * length);
                    o += 3 * length;
                }
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
        _d.EnsureRows(0, _d.Frame->height);
        byte* bgr = ToBgr(out int stride);
        fixed (byte* d = dst)
            for (int y = 0; y < crop.Height; y++)
                Buffer.MemoryCopy(bgr + (crop.Y + y) * stride + crop.X * 3, d + y * crop.Width * 3, crop.Width * 3, crop.Width * 3);
    }

    /// <summary>The converted frame's crop region scaled to width × height with swscale's bilinear (…,scale=W:H:flags=bilinear).</summary>
    public void WriteScaledBgr(byte[] dst, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
        _d.EnsureRows(0, _d.Frame->height);
        byte* bgr = ToBgr(out int stride);
        if (_toSize == null)
        {
            _toSize = ffmpeg.sws_getContext(crop.Width, crop.Height, AVPixelFormat.AV_PIX_FMT_BGR24, width, height,
                AVPixelFormat.AV_PIX_FMT_BGR24, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_toSize == null)
                throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Can't set up the scaler.");
        }
        byte*[] src = [bgr + crop.Y * stride + crop.X * 3, null, null, null];
        int[] srcStride = [stride, 0, 0, 0];
        fixed (byte* d = dst)
        {
            byte*[] dstPlanes = [d, null, null, null];
            int[] dstStride = [width * 3, 0, 0, 0];
            ffmpeg.sws_scale(_toSize, src, srcStride, 0, crop.Height, dstPlanes, dstStride);
        }
    }

    /// <summary>
    /// The frame as BGR, converted like ffmpeg's scale filter (and OpenCV): bicubic flag, colour tags. With
    /// <paramref name="rows"/> (ascending), only the slices holding those rows are converted, the rest left stale:
    /// the same converter on the same rows, a fraction of the work.
    /// </summary>
    byte* ToBgr(out int stride, int[]? rows = null)
    {
        if (_toBgr == null)
        {
            _toBgr = ffmpeg.sws_alloc_context();
            ffmpeg.av_opt_set_int(_toBgr, "sws_flags", (long)SwsFlags.SWS_BICUBIC, 0);
            _bgr = ffmpeg.av_frame_alloc();
        }
        if (_bgr->width != _d.Frame->width || _bgr->height != _d.Frame->height)
        {
            ffmpeg.av_frame_unref(_bgr);
            _bgr->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
            (_bgr->width, _bgr->height) = (_d.Frame->width, _d.Frame->height);
            Check(ffmpeg.av_frame_get_buffer(_bgr, 32), "frame buffer");
        }
        if (rows is null)
        {
            _d.EnsureRows(0, _d.Frame->height);
            Check(ffmpeg.sws_scale_frame(_toBgr, _bgr, _d.Frame), "convert");
        }
        else
        {
            // Deinterlacing: only the rows that are read (a slice's other rows come out wrong, and nothing reads them).
            if (_d.RowMode)
            {
                foreach (int r in rows)
                    _d.MarkRows(r, r + 1);
                _d.ComputeRows();
            }
            var ctx = RowsContext();
            Check(ffmpeg.sws_frame_start(ctx, _bgr, _d.Frame), "convert");
            Check(ffmpeg.sws_send_slice(ctx, 0, (uint)_d.Frame->height), "convert");
            int align = (int)ffmpeg.sws_receive_slice_alignment(ctx), h = _d.Frame->height;
            for (int i = 0; i < rows.Length;)
            {
                // Merge the aligned slices of consecutive wanted rows into one request.
                int start = rows[i] - rows[i] % align, end = Math.Min(start + align, h);
                while (++i < rows.Length && rows[i] < end + align)
                    end = Math.Min(rows[i] - rows[i] % align + align, h);
                Check(ffmpeg.sws_receive_slice(ctx, (uint)start, (uint)(end - start)), "convert");
            }
            ffmpeg.sws_frame_end(ctx);
        }
        stride = _bgr->linesize[0];
        return _bgr->data[0];
    }

    /// <summary>
    /// A converter set up as sws_scale_frame sets itself up from the frame (bicubic flag, the colour matrix and
    /// range from its tags, full-range RGB out), which the slice calls need beforehand; made again if those change.
    /// </summary>
    SwsContext* RowsContext()
    {
        (int W, int H, int Format, AVColorSpace Space, AVColorRange Range) key = (_d.Frame->width, _d.Frame->height, _d.Frame->format, _d.Frame->colorspace, _d.Frame->color_range);
        if (_toRows != null && _rowsFor == key)
            return _toRows;
        if (_toRows != null)
            ffmpeg.sws_freeContext(_toRows);
        _toRows = ffmpeg.sws_getContext(key.W, key.H, (AVPixelFormat)key.Format, key.W, key.H,
            AVPixelFormat.AV_PIX_FMT_BGR24, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (_toRows == null)
            throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Can't set up the converter.");
        int* coefficients = ffmpeg.sws_getCoefficients((int)key.Space);
        var table = new int_array4();
        for (uint i = 0; i < 4; i++)
            table[i] = coefficients[i];
        var rgb = table; // the output's table is unused for RGB
        int fullRange = key.Range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
        Check(ffmpeg.sws_setColorspaceDetails(_toRows, in table, fullRange, in rgb, 1, 0, 1 << 16, 1 << 16), "colour details");
        _rowsFor = key;
        return _toRows;
    }

    public void Dispose()
    {
        if (_toSize != null) { ffmpeg.sws_freeContext(_toSize); _toSize = null; }
        if (_toRows != null) { ffmpeg.sws_freeContext(_toRows); _toRows = null; }
        if (_toBgr != null) { var s = _toBgr; ffmpeg.sws_free_context(&s); _toBgr = null; }
        if (_bgr != null) { var f = _bgr; ffmpeg.av_frame_free(&f); _bgr = null; }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new ShotDetectionException(ShotDetectionError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
