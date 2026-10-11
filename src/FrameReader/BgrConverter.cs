using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A <see cref="FrameDecoder"/>'s current frame as packed BGR at its own size, the bytes ffmpeg's
/// <c>-vf scale,format=bgr24</c> gives (bicubic flag, the frame's colour matrix and range), and OpenCV's too. For speed,
/// only some rows can be converted (<see cref="Convert(ReadOnlySpan{int}, out int)"/>): the same converter on those
/// rows' slices, a fraction of the work.
/// Advanced: part of the low-level layer ShotDetector's exact pipelines use; the main API is VideoFrameReader, the audio
/// readers, the writers, Remux and MediaProbe.
/// </summary>
/// <param name="decoder">The decoder whose current frame is converted; not disposed with the converter.</param>
public sealed unsafe class BgrConverter(FrameDecoder decoder) : IDisposable
{
    readonly FrameDecoder _d = decoder;
    AVFrame* _bgr;
    SwsContext* _toBgr, _toRows;

    /// <summary>
    /// Keep the first frame's size: a frame of another size (the stream changed resolution) is scaled to it in the same
    /// conversion (bicubic), as OpenCV does into its buffer; false (the default): each frame at its own size.
    /// </summary>
    public bool KeepFirstSize { get; init; }

    (int W, int H, int Format, AVColorSpace Space, AVColorRange Range, AVChromaLocation Loc) _rowsFor;

    /// <summary>The whole current frame as BGR, its rows <paramref name="stride"/> bytes apart.</summary>
    /// <param name="stride">Bytes from one row to the next (at least 3 x width).</param>
    /// <returns>Height x stride bytes, valid until the next conversion or <see cref="Dispose"/>.</returns>
    public ReadOnlySpan<byte> Convert(out int stride)
    {
        Prepare();
        _d.EnsureRows(0, _d.Frame->height);
        int flags = AsProgressive();
        try
        {
            Check(ffmpeg.sws_scale_frame(_toBgr, _bgr, _d.Frame), "convert");
        }
        finally
        {
            _d.Frame->flags = flags;
        }

        return Result(out stride);
    }

    /// <summary>
    /// Only the slices holding <paramref name="rows"/> converted; the other rows are left as they were (stale). Each
    /// converted row has exactly the bytes <see cref="Convert(out int)"/> gives it.
    /// </summary>
    /// <param name="rows">The rows wanted, ascending.</param>
    /// <param name="stride">Bytes from one row to the next.</param>
    /// <returns>Height x stride bytes, valid until the next conversion or <see cref="Dispose"/>.</returns>
    public ReadOnlySpan<byte> Convert(ReadOnlySpan<int> rows, out int stride)
    {
        Prepare();

        // Scaled to the first size: the rows wanted aren't the decoded frame's rows, so the whole frame.
        if (_bgr->width != _d.Frame->width || _bgr->height != _d.Frame->height)
            return Convert(out stride);

        // Deinterlacing row by row: only the rows that are read (a slice's other rows come out wrong, and nothing reads them).
        if (_d.RowMode)
        {
            foreach (int r in rows)
                _d.MarkRows(r, r + 1);
            _d.ComputeRows();
        }

        var ctx = RowsContext();
        int flags = AsProgressive();
        try
        {
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
        finally
        {
            _d.Frame->flags = flags;
        }

        return Result(out stride);
    }

    /// <summary>
    /// The frame read as the scale filter reads it, as progressive (its interl=0; swscale refuses to convert an
    /// interlaced-flagged frame otherwise); returns the flags to put back, so the decoder's frame is left as it was.
    /// </summary>
    int AsProgressive()
    {
        int flags = _d.Frame->flags;
        _d.Frame->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        return flags;
    }

    void Prepare()
    {
        if (_toBgr == null)
        {
            _toBgr = ffmpeg.sws_alloc_context();
            ffmpeg.av_opt_set_int(_toBgr, "sws_flags", (long)SwsFlags.SWS_BICUBIC, 0);
            _bgr = ffmpeg.av_frame_alloc();
        }

        if ((_bgr->width != _d.Frame->width || _bgr->height != _d.Frame->height) && !(KeepFirstSize && _bgr->width > 0))
        {
            ffmpeg.av_frame_unref(_bgr);
            _bgr->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
            (_bgr->width, _bgr->height) = (_d.Frame->width, _d.Frame->height);
            Check(ffmpeg.av_frame_get_buffer(_bgr, 32), "frame buffer");
        }
    }

    ReadOnlySpan<byte> Result(out int stride)
    {
        stride = _bgr->linesize[0];
        return new ReadOnlySpan<byte>(_bgr->data[0], stride * _bgr->height);
    }

    /// <summary>
    /// A converter set up as sws_scale_frame sets itself up from the frame (bicubic flag, the colour matrix and
    /// range from its tags, the chroma samples' position from its chroma location, full-range RGB out), which the slice
    /// calls need beforehand; made again if those change.
    /// </summary>
    SwsContext* RowsContext()
    {
        var key = (_d.Frame->width, _d.Frame->height, _d.Frame->format, _d.Frame->colorspace, _d.Frame->color_range, _d.Frame->chroma_location);
        if (_toRows != null && _rowsFor == key)
            return _toRows;
        if (_toRows != null)
            ffmpeg.sws_freeContext(_toRows);
        _toRows = ffmpeg.sws_alloc_context();
        if (_toRows == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "Can't set up the converter.");
        var (hPos, vPos) = ChromaPosition((AVPixelFormat)key.Item3, key.Item6);
        ffmpeg.av_opt_set_int(_toRows, "srcw", key.Item1, 0);
        ffmpeg.av_opt_set_int(_toRows, "srch", key.Item2, 0);
        ffmpeg.av_opt_set_int(_toRows, "dstw", key.Item1, 0);
        ffmpeg.av_opt_set_int(_toRows, "dsth", key.Item2, 0);
        ffmpeg.av_opt_set_pixel_fmt(_toRows, "src_format", (AVPixelFormat)key.Item3, 0);
        ffmpeg.av_opt_set_pixel_fmt(_toRows, "dst_format", AVPixelFormat.AV_PIX_FMT_BGR24, 0);
        ffmpeg.av_opt_set_int(_toRows, "sws_flags", (long)SwsFlags.SWS_BICUBIC, 0);
        ffmpeg.av_opt_set_int(_toRows, "src_h_chr_pos", hPos, 0);
        ffmpeg.av_opt_set_int(_toRows, "src_v_chr_pos", vPos, 0);
        if (ffmpeg.sws_init_context(_toRows, null, null) < 0)
        {
            ffmpeg.sws_freeContext(_toRows);
            _toRows = null;
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "Can't set up the converter.");
        }

        int* coefficients = ffmpeg.sws_getCoefficients((int)key.Item4);
        var table = default(int_array4);
        for (uint i = 0; i < 4; i++)
            table[i] = coefficients[i];
        var rgb = table; // the output's table is unused for RGB
        int fullRange = key.Item5 == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
        Check(ffmpeg.sws_setColorspaceDetails(_toRows, in table, fullRange, in rgb, 1, 0, 1 << 16, 1 << 16), "colour details");
        _rowsFor = key;
        return _toRows;
    }

    /// <summary>
    /// The source chroma position sws_scale_frame derives from a progressive frame's chroma location (libswscale's
    /// get_chroma_pos): center when unspecified, scaled to the subsampling grid, -513 (none) along an axis that isn't
    /// subsampled. h264 and HEVC mark 4:2:0 left-sited, so for 10-bit video (whose conversion interpolates chroma) this
    /// is what makes OpenCV's frames.
    /// </summary>
    static (int H, int V) ChromaPosition(AVPixelFormat format, AVChromaLocation location)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        int subX = desc == null ? 0 : desc->log2_chroma_w, subY = desc == null ? 0 : desc->log2_chroma_h;
        int x, y;
        ffmpeg.av_chroma_location_enum_to_pos(&x, &y, location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED ? AVChromaLocation.AVCHROMA_LOC_CENTER : location);
        return (subX != 0 ? x * ((1 << subX) - 1) : -513, subY != 0 ? y * ((1 << subY) - 1) : -513);
    }

    /// <summary>Frees the converter's buffers.</summary>
    public void Dispose()
    {
        if (_toRows != null)
        {
            ffmpeg.sws_freeContext(_toRows);
            _toRows = null;
        }

        if (_toBgr != null)
        {
            var s = _toBgr;
            ffmpeg.sws_free_context(&s);
            _toBgr = null;
        }

        if (_bgr != null)
        {
            var f = _bgr;
            ffmpeg.av_frame_free(&f);
            _bgr = null;
        }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
