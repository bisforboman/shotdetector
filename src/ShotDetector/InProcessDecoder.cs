using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ShotDetector;

/// <summary>
/// Decodes a video's first video stream with FFmpeg's libraries in this process (FFmpeg.AutoGen bindings, FFmpeg 8.1
/// ABI) and writes each frame in exactly the layout ffmpeg's command line would send for a <see cref="FramePipeline"/>:
/// the same decoder, the same swscale conversion (bicubic flag, the frame's colour tags), so the same bytes, without a
/// second process or a pipe.
/// </summary>
internal sealed unsafe class InProcessDecoder : IDisposable
{
    static readonly object InitLock = new();
    static bool _loaded;
    static readonly Dictionary<string, bool> Loadable = [];

    AVFormatContext* _fmt;
    AVCodecContext* _dec;
    AVPacket* _pkt;
    AVFrame* _frame, _bgr;
    SwsContext* _toBgr, _toSize, _toRows;
    (int W, int H, int Format, AVColorSpace Space, AVColorRange Range) _rowsFor;
    readonly int _stream;
    readonly AVRational _timeBase;
    bool _draining, _done;
    // Deinterlacing: decoded frames go through buffer -> yadif -> buffersink (as ffmpeg's -vf yadif), and _frame is
    // the sink's output. The graph is built from the first decoded frame.
    readonly bool _deinterlace;
    AVFrame* _decoded;
    AVFilterGraph* _graph;
    AVFilterContext* _source, _sink;
    bool _flushed;
    // Deinterlacing with an IYadif instead (DetectionOptions.Yadif): the previous, current and next decoded frames, and
    // _frame as the output, whose rows are computed when a writer asks for them (EnsureRows).
    readonly IYadif? _yadif;
    bool? _rowMode;
    AVFrame* _rPrev, _rCur, _rNext;
    bool _rShift, _rDone;
    long _rPts;
    bool _rTff;
    int _rPlanes, _rChromaW, _rChromaH, _rBytes;
    bool[][]? _rRows; // per plane: rows computed for the current output frame
    // The display matrix applied as ffmpeg's autorotate does: transpose (its dir, -1 for none), then hflip, vflip. The
    // oriented frame is swapped in as _frame until the next frame.
    readonly int _transpose = -1;
    readonly bool _hflip, _vflip;
    AVFrame* _oriented;
    bool _swapped;

    /// <summary>The stream's time base in seconds per pts unit.</summary>
    public double TimeBase => _timeBase.num / (double)_timeBase.den;

    /// <summary>The current frame's best-effort timestamp (what ffmpeg's command line gives each frame).</summary>
    public long Pts => _rowMode == true ? _rPts : _sink == null ? _frame->best_effort_timestamp
        : ffmpeg.av_rescale_q(_frame->pts, _sink->inputs[0]->time_base, _timeBase); // yadif doubles the time base: exact

    /// <summary>The current frame's own pts, if set (ffprobe's frame pts).</summary>
    public long? FramePts => _frame->pts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pts;

    /// <summary>The dts of the packet the current frame came from, if set (ffprobe's frame pkt_dts).</summary>
    public long? FramePacketDts => _frame->pkt_dts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pkt_dts;

    /// <param name="path">The file.</param>
    /// <param name="libraryDirectory">Where FFmpeg's libraries are (null: next to the app, then the system).</param>
    /// <param name="threads">Decoder threads (0: FFmpeg's choice).</param>
    /// <param name="deinterlace">Deinterlace with yadif (its defaults, as ffmpeg's -vf yadif); needs libavfilter.</param>
    /// <param name="yadif">Row-wise yadif for deinterlacing (FastYuv's), else FFmpeg's on whole frames.</param>
    public InProcessDecoder(string path, string? libraryDirectory, int threads, bool deinterlace = false, IYadif? yadif = null)
    {
        Load(libraryDirectory);
        _deinterlace = deinterlace;
        _yadif = deinterlace ? yadif : null;
        _fmt = InProcessProbe.Open(path);
        _stream = InProcessProbe.FirstVideoStream(_fmt);
        if (_stream < 0)
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, "The input has no video stream.");
        var st = _fmt->streams[_stream];
        _timeBase = st->time_base;
        var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"No decoder for {st->codecpar->codec_id} in these FFmpeg libraries.");
        _dec = ffmpeg.avcodec_alloc_context3(codec);
        Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "decoder parameters");
        _dec->pkt_timebase = st->time_base; // as ffmpeg's command line sets it (best-effort timestamps use it)
        _dec->thread_count = threads;
        Check(ffmpeg.avcodec_open2(_dec, codec, null), "open decoder");
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (deinterlace)
            _decoded = ffmpeg.av_frame_alloc();
        (_transpose, _hflip, _vflip) = Orientation(st->codecpar);
        if (_transpose >= 0 || _hflip || _vflip)
        {
            // ffmpeg's command line orients before the -vf chain, so before yadif; here it would come after.
            if (deinterlace)
                throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Deinterlacing rotated or flipped video isn't supported in-process.");
            _oriented = ffmpeg.av_frame_alloc();
        }
    }

    /// <summary>
    /// What ffmpeg's command line inserts for the stream's display matrix (fftools' autorotate): transpose=clock (dir 1),
    /// cclock (2), cclock_flip (0) or clock_flip (3) for 90 and 270 degrees, hflip and vflip for 180, vflip for 0.
    /// </summary>
    static (int Transpose, bool HFlip, bool VFlip) Orientation(AVCodecParameters* par)
    {
        var sd = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sd == null || sd->size < 36)
            return (-1, false, false);
        var m = new int_array9();
        for (uint i = 0; i < 9; i++)
            m[i] = ((int*)sd->data)[i];
        double theta = -Math.Round(ffmpeg.av_display_rotation_get(in m)); // get_rotation
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        if (Math.Abs(theta - 90) < 1)
            return (m[3] > 0 ? 0 : 1, false, false);
        if (Math.Abs(theta - 180) < 1)
            return (-1, m[0] < 0, m[4] < 0);
        if (Math.Abs(theta - 270) < 1)
            return (m[3] < 0 ? 3 : 2, false, false);
        if (Math.Abs(theta) > 1)
            throw new ShotDetectionException(ShotDetectionError.DecodeFailed, $"Rotation by {theta} degrees isn't supported in-process.");
        return (-1, false, m[4] < 0);
    }

    /// <summary>Whether a rotation (ffprobe's, degrees) of this pixel format is one in-process decoding applies.</summary>
    internal static bool CanRotate(int rotation, string? pixelFormat)
    {
        if (rotation % 90 != 0)
            return false;
        if (rotation % 180 == 0)
            return true;
        var desc = ffmpeg.av_pix_fmt_desc_get(ffmpeg.av_get_pix_fmt(pixelFormat ?? ""));
        return desc != null && desc->log2_chroma_w == desc->log2_chroma_h; // as the transpose filter's formats
    }

    /// <summary>Seeks to the keyframe at or before <paramref name="pts"/> (stream time base), as ffmpeg's -ss does.</summary>
    public void Seek(long pts)
    {
        long us = ffmpeg.av_rescale_q(pts, _timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });
        Check(ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, us, us, 0), "seek");
        ffmpeg.avcodec_flush_buffers(_dec);
    }

    /// <summary>Decodes the next frame; false at the end. Packets that don't decode are skipped, as ffmpeg's command line does.</summary>
    public bool Next()
    {
        if (_swapped)
            SwapOriented();
        if (!NextFrame())
            return false;
        if (_oriented != null)
        {
            Orient();
            SwapOriented();
        }
        return true;
    }

    void SwapOriented()
    {
        var f = _frame;
        _frame = _oriented;
        _oriented = f;
        _swapped = !_swapped;
    }

    /// <summary>
    /// _frame transposed and/or flipped into _oriented, as ffmpeg's transpose, hflip and vflip filters do: a pure
    /// rearrangement of each plane's samples (planar formats, up to 16 bits).
    /// </summary>
    void Orient()
    {
        EnsureRows(0, _frame->height);
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)_frame->format);
        int bytes = desc == null ? 0 : desc->comp[0].depth > 8 ? 2 : 1, planes = 0;
        for (uint i = 0; desc != null && i < desc->nb_components; i++)
        {
            planes = Math.Max(planes, desc->comp[i].plane + 1);
            if (desc->comp[i].step != bytes)
                bytes = 0;
        }
        if (bytes == 0 || (desc->flags & (ffmpeg.AV_PIX_FMT_FLAG_PAL | ffmpeg.AV_PIX_FMT_FLAG_BITSTREAM | ffmpeg.AV_PIX_FMT_FLAG_HWACCEL)) != 0
            || ((desc->flags & ffmpeg.AV_PIX_FMT_FLAG_PLANAR) == 0 && desc->nb_components > 1))
            throw new ShotDetectionException(ShotDetectionError.DecodeFailed, $"Rotating {(AVPixelFormat)_frame->format} isn't supported in-process.");
        bool t = _transpose >= 0;
        int w = _frame->width, h = _frame->height, ow = t ? h : w, oh = t ? w : h;
        if (_oriented->width != ow || _oriented->height != oh || _oriented->format != _frame->format)
        {
            ffmpeg.av_frame_unref(_oriented);
            (_oriented->format, _oriented->width, _oriented->height) = (_frame->format, ow, oh);
            Check(ffmpeg.av_frame_get_buffer(_oriented, 64), "rotate");
        }
        ffmpeg.av_frame_side_data_free(&_oriented->side_data, &_oriented->nb_side_data);
        ffmpeg.av_dict_free(&_oriented->metadata);
        Check(ffmpeg.av_frame_copy_props(_oriented, _frame), "rotate");
        if (t && _frame->sample_aspect_ratio.num != 0)
            _oriented->sample_aspect_ratio = new AVRational { num = _frame->sample_aspect_ratio.den, den = _frame->sample_aspect_ratio.num };
        for (int p = 0; p < planes; p++)
        {
            int sx = p is 1 or 2 ? desc->log2_chroma_w : 0, sy = p is 1 or 2 ? desc->log2_chroma_h : 0;
            int pw = -((-w) >> sx), ph = -((-h) >> sy); // the source plane
            int opw = t ? ph : pw, oph = t ? pw : ph;
            byte* src = _frame->data[(uint)p], dst = _oriented->data[(uint)p];
            int ss = _frame->linesize[(uint)p], ds = _oriented->linesize[(uint)p];
            for (int y = 0; y < oph; y++)
            {
                byte* d = dst + (long)y * ds;
                if (!t)
                {
                    byte* s = src + (long)(_vflip ? ph - 1 - y : y) * ss;
                    if (!_hflip)
                        Buffer.MemoryCopy(s, d, opw * bytes, opw * bytes);
                    else if (bytes == 1)
                        for (int x = 0; x < opw; x++)
                            d[x] = s[opw - 1 - x];
                    else
                        for (int x = 0; x < opw; x++)
                            ((ushort*)d)[x] = ((ushort*)s)[opw - 1 - x];
                    continue;
                }
                // transpose: output row y is source column y (dir & 2: counted from the right), read top down (dir & 1: bottom up)
                int col = (_transpose & 2) != 0 ? pw - 1 - y : y;
                byte* s0 = src + (long)col * bytes;
                long step = ss;
                if ((_transpose & 1) != 0)
                {
                    s0 += (long)(ph - 1) * ss;
                    step = -step;
                }
                if (bytes == 1)
                    for (int x = 0; x < opw; x++)
                        d[x] = s0[x * step];
                else
                    for (int x = 0; x < opw; x++)
                        ((ushort*)d)[x] = *(ushort*)(s0 + x * step);
            }
        }
    }

    bool NextFrame()
    {
        if (!_deinterlace)
            return Decode(_frame);
        if (_yadif != null && _rowMode != false)
        {
            bool? rows = NextRows();
            if (rows is { } more)
                return more;
            // The first frame's format isn't one row-wise yadif handles: FFmpeg's yadif from here, with that frame.
        }
        while (true)
        {
            if (_sink != null)
            {
                ffmpeg.av_frame_unref(_frame);
                int got = ffmpeg.av_buffersink_get_frame(_sink, _frame);
                if (got == 0)
                    return true;
                if (got == ffmpeg.AVERROR_EOF)
                    return false;
                if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(got, "deinterlace");
            }
            if (_flushed)
                return false;
            if (_rCur != null)
            {
                // Handing over from NextRows: its first decoded frame goes into the graph first.
                ffmpeg.av_frame_ref(_decoded, _rCur);
                var f = _rCur;
                ffmpeg.av_frame_free(&f);
                _rCur = null;
            }
            else if (!Decode(_decoded))
            {
                // The decoder is done: flush yadif's last frames out.
                if (_source == null)
                    return false;
                Check(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0), "deinterlace");
                _flushed = true;
                continue;
            }
            if (_graph == null)
                BuildGraph(_decoded);
            // As ffmpeg's command line hands frames to its filters: the best-effort timestamp as the pts.
            _decoded->pts = _decoded->best_effort_timestamp;
            Check(ffmpeg.av_buffersrc_add_frame_flags(_source, _decoded, 0), "deinterlace");
        }
    }

    /// <summary>
    /// Row-wise deinterlacing (<see cref="IYadif"/>): yadif's frame order (output i from frames i-1, i and i+1, the
    /// first and last standing in for the missing ones), with _frame's rows computed on demand. True: a frame;
    /// false: the end; null: the format isn't one it handles (only decided on the first frame).
    /// </summary>
    bool? NextRows()
    {
        if (_rShift)
        {
            // Move on: the current frame becomes the previous one, the next one current.
            if (_rPrev != _rCur) { var p = _rPrev; ffmpeg.av_frame_free(&p); }
            _rPrev = _rCur;
            _rCur = _rNext;
            _rNext = null;
            _rShift = false;
        }
        if (_rDone)
            return false;
        while (true)
        {
            var f = ffmpeg.av_frame_alloc();
            if (!Decode(f))
            {
                ffmpeg.av_frame_free(&f);
                if (_rCur == null)
                    return false;
                // The end: the last frame is its own next (yadif's flush).
                _rDone = true;
                Output(_rPrev, _rCur, _rCur);
                return true;
            }
            if (_rCur == null)
            {
                if (_rowMode == null && !SetUpRows(f))
                {
                    _rowMode = false;
                    _rCur = f; // Next() feeds it to FFmpeg's yadif
                    return null;
                }
                _rowMode = true;
                _rPrev = _rCur = f; // the first frame is its own previous
                continue;
            }
            _rNext = f;
            Output(_rPrev, _rCur, _rNext);
            _rShift = true;
            return true;
        }
    }

    /// <summary>Whether row-wise yadif handles this frame's format (planar YUV or gray, 8 to 16 bits); sets the geometry.</summary>
    bool SetUpRows(AVFrame* first)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)first->format);
        if (desc == null)
            return false;
        ulong unsupported = ffmpeg.AV_PIX_FMT_FLAG_RGB | ffmpeg.AV_PIX_FMT_FLAG_PAL | ffmpeg.AV_PIX_FMT_FLAG_BITSTREAM | ffmpeg.AV_PIX_FMT_FLAG_HWACCEL;
        if ((desc->flags & unsupported) != 0 || ((desc->flags & ffmpeg.AV_PIX_FMT_FLAG_PLANAR) == 0 && desc->nb_components > 1))
            return false;
        int planes = 0, depth = desc->comp[0].depth;
        for (uint i = 0; i < desc->nb_components; i++)
        {
            planes = Math.Max(planes, desc->comp[i].plane + 1);
            if (desc->comp[i].depth != depth || desc->comp[i].step > (depth > 8 ? 2 : 1))
                return false;
        }
        if (depth > 16)
            return false;
        (_rPlanes, _rChromaW, _rChromaH, _rBytes) = (planes, desc->log2_chroma_w, desc->log2_chroma_h, depth > 8 ? 2 : 1);
        _frame->format = first->format;
        (_frame->width, _frame->height) = (first->width, first->height);
        Check(ffmpeg.av_frame_get_buffer(_frame, 64), "deinterlace");
        _rRows = new bool[planes][];
        return true;
    }

    /// <summary>Starts output frame i (prev, cur, next): its properties now, its rows when asked for.</summary>
    void Output(AVFrame* prev, AVFrame* cur, AVFrame* next)
    {
        for (int p = 0; p < _rPlanes; p++)
            if (prev->linesize[(uint)p] != cur->linesize[(uint)p] || next->linesize[(uint)p] != cur->linesize[(uint)p])
                throw new ShotDetectionException(ShotDetectionError.DecodeFailed, "Deinterlacing: frames with differing strides.");
        // copy_props adds to the side data and metadata already there: without this, every frame's would pile up
        // (and sws_frame_start refs them all).
        ffmpeg.av_frame_side_data_free(&_frame->side_data, &_frame->nb_side_data);
        ffmpeg.av_dict_free(&_frame->metadata);
        Check(ffmpeg.av_frame_copy_props(_frame, cur), "deinterlace");
        _frame->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        _rPts = cur->best_effort_timestamp;
        // As yadif's auto parity: the frame's field order, top first when it isn't flagged interlaced.
        _rTff = (cur->flags & ffmpeg.AV_FRAME_FLAG_INTERLACED) == 0 || (cur->flags & ffmpeg.AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0;
        for (int p = 0; p < _rPlanes; p++)
        {
            int h = PlaneHeight(p);
            if (_rRows![p]?.Length == h)
                Array.Clear(_rRows[p]);
            else
                _rRows[p] = new bool[h];
        }
        _cPrev = prev;
        _cCur = cur;
        _cNext = next;
    }

    AVFrame* _cPrev, _cCur, _cNext; // the frames the current output's rows come from

    int PlaneHeight(int p) => p is 1 or 2 ? -((-_frame->height) >> _rChromaH) : _frame->height;
    int PlaneWidth(int p) => p is 1 or 2 ? -((-_frame->width) >> _rChromaW) : _frame->width;

    /// <summary>
    /// Computes the output's rows that luma rows [<paramref name="from"/>, <paramref name="to"/>) need (with a margin,
    /// and the chroma rows under them): interpolated rows with yadif, the others copied from the current frame.
    /// </summary>
    void EnsureRows(int from, int to)
    {
        if (_rowMode != true || _swapped)
            return;
        MarkRows(from, to);
        ComputeRows();
    }

    readonly List<(int Plane, int Y)> _rPending = [];

    /// <summary>
    /// Computes the rows <see cref="MarkRows"/> collected, in parallel batches: yadif's rows are independent, and FFmpeg's
    /// own yadif uses every core too.
    /// </summary>
    void ComputeRows()
    {
        int n = _rPending.Count;
        if (n == 0)
            return;
        if (n < 64 || Environment.ProcessorCount == 1)
            ComputeRows(0, n);
        else
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, n, Math.Max(16, n / (4 * Environment.ProcessorCount))),
                r => ComputeRows(r.Item1, r.Item2));
        _rPending.Clear();
    }

    void ComputeRows(int from, int to)
    {
        AVFrame* prev = _cPrev, cur = _cCur, next = _cNext;
        int parity = _rTff ? 0 : 1; // yadif filters with parity tff ^ 1: these rows are interpolated
        for (int i = from; i < to; i++)
        {
            var (p, y) = _rPending[i];
            int h = PlaneHeight(p), w = PlaneWidth(p);
            int srcStride = cur->linesize[(uint)p], dstStride = _frame->linesize[(uint)p];
            byte* dst = _frame->data[(uint)p], c = cur->data[(uint)p];
            if (((y ^ parity) & 1) != 0)
                _yadif!.FilterRow((nint)dst, (nint)prev->data[(uint)p], (nint)c, (nint)next->data[(uint)p], srcStride, dstStride, w, h, y, _rBytes);
            else
                Buffer.MemoryCopy(c + (long)y * srcStride, dst + (long)y * dstStride, w * _rBytes, w * _rBytes);
        }
    }

    /// <summary>Collects the rows <see cref="EnsureRows"/> needs that aren't computed yet.</summary>
    void MarkRows(int from, int to)
    {
        if (_swapped)
            return; // the oriented frame: Orient computed every row first
        for (int p = 0; p < _rPlanes; p++)
        {
            // Converting a row reads that row; with vertically subsampled chroma, the chroma rows around it too.
            int shift = p is 1 or 2 ? _rChromaH : 0, margin = shift > 0 ? 2 : 0, h = PlaneHeight(p);
            int start = Math.Max(0, (from >> shift) - margin), end = Math.Min(h, ((to + (1 << shift) - 1) >> shift) + margin);
            var done = _rRows![p];
            for (int y = start; y < end; y++)
                if (!done[y])
                {
                    done[y] = true;
                    _rPending.Add((p, y));
                }
        }
    }

    /// <summary>buffer (the decoded frames' size, format, aspect and time base) -> yadif -> buffersink.</summary>
    void BuildGraph(AVFrame* first)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        var sar = first->sample_aspect_ratio.num > 0 ? first->sample_aspect_ratio : new AVRational { num = 0, den = 1 };
        string args = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"video_size={first->width}x{first->height}:pix_fmt={first->format}:time_base={_timeBase.num}/{_timeBase.den}:pixel_aspect={sar.num}/{sar.den}");
        AVFilterContext* source, yadif, sink;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, _graph), "deinterlace");
        Check(ffmpeg.avfilter_graph_create_filter(&yadif, ffmpeg.avfilter_get_by_name("yadif"), "yadif", null, null, _graph), "deinterlace");
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph), "deinterlace");
        Check(ffmpeg.avfilter_link(source, 0, yadif, 0), "deinterlace");
        Check(ffmpeg.avfilter_link(yadif, 0, sink, 0), "deinterlace");
        Check(ffmpeg.avfilter_graph_config(_graph, null), "deinterlace");
        _source = source;
        _sink = sink;
    }

    /// <summary>Decodes the next frame into <paramref name="into"/>; false at the end.</summary>
    bool Decode(AVFrame* into)
    {
        while (!_done)
        {
            int got = ffmpeg.avcodec_receive_frame(_dec, into);
            if (got == 0)
                return true;
            if (got == ffmpeg.AVERROR_EOF)
            {
                _done = true;
                break;
            }
            if (_draining)
                continue;
            if (ffmpeg.av_read_frame(_fmt, _pkt) < 0)
            {
                _draining = true;
                ffmpeg.avcodec_send_packet(_dec, null);
                continue;
            }
            if (_pkt->stream_index == _stream)
                ffmpeg.avcodec_send_packet(_dec, _pkt); // errors (damaged data) only lose that packet
            ffmpeg.av_packet_unref(_pkt);
        }
        return false;
    }

    /// <summary>The frame's yuv420p planes, packed (Y, then U, then V), as -pix_fmt yuv420p rawvideo sends them.</summary>
    public void WriteYuv420(byte[] dst)
    {
        EnsureRows(0, _frame->height);
        int w = _frame->width, h = _frame->height, cw = (w + 1) / 2, ch = (h + 1) / 2, o = 0;
        fixed (byte* d = dst)
        {
            for (int p = 0; p < 3; p++)
            {
                int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
                byte* src = _frame->data[(uint)p];
                int stride = _frame->linesize[(uint)p];
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
        EnsureRows(0, _frame->height);
        byte* bgr = ToBgr(out int stride);
        fixed (byte* d = dst)
            for (int y = 0; y < crop.Height; y++)
                Buffer.MemoryCopy(bgr + (crop.Y + y) * stride + crop.X * 3, d + y * crop.Width * 3, crop.Width * 3, crop.Width * 3);
    }

    /// <summary>The converted frame's crop region scaled to width × height with swscale's bilinear (…,scale=W:H:flags=bilinear).</summary>
    public void WriteScaledBgr(byte[] dst, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
        EnsureRows(0, _frame->height);
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
        if (_bgr->width != _frame->width || _bgr->height != _frame->height)
        {
            ffmpeg.av_frame_unref(_bgr);
            _bgr->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
            (_bgr->width, _bgr->height) = (_frame->width, _frame->height);
            Check(ffmpeg.av_frame_get_buffer(_bgr, 32), "frame buffer");
        }
        if (rows is null)
        {
            EnsureRows(0, _frame->height);
            Check(ffmpeg.sws_scale_frame(_toBgr, _bgr, _frame), "convert");
        }
        else
        {
            // Deinterlacing: only the rows that are read (a slice's other rows come out wrong, and nothing reads them).
            if (_rowMode == true)
            {
                foreach (int r in rows)
                    MarkRows(r, r + 1);
                ComputeRows();
            }
            var ctx = RowsContext();
            Check(ffmpeg.sws_frame_start(ctx, _bgr, _frame), "convert");
            Check(ffmpeg.sws_send_slice(ctx, 0, (uint)_frame->height), "convert");
            int align = (int)ffmpeg.sws_receive_slice_alignment(ctx), h = _frame->height;
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
        (int W, int H, int Format, AVColorSpace Space, AVColorRange Range) key = (_frame->width, _frame->height, _frame->format, _frame->colorspace, _frame->color_range);
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
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_oriented != null) { var f = _oriented; ffmpeg.av_frame_free(&f); _oriented = null; }
        if (_decoded != null) { var f = _decoded; ffmpeg.av_frame_free(&f); _decoded = null; }
        if (_rNext != null) { var f = _rNext; ffmpeg.av_frame_free(&f); _rNext = null; }
        if (_rPrev != null && _rPrev != _rCur) { var f = _rPrev; ffmpeg.av_frame_free(&f); }
        if (_rCur != null) { var f = _rCur; ffmpeg.av_frame_free(&f); }
        _rPrev = _rCur = null;
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; _source = null; _sink = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }

    /// <summary>
    /// Whether FFmpeg 8's libraries load from <paramref name="directory"/> (null: the system's usual places), or were
    /// already loaded; tried once per folder (<see cref="VideoDecoder.Auto"/>).
    /// </summary>
    internal static bool CanDeinterlace(string? directory)
    {
        if (!CanLoad(directory))
            return false;
        lock (InitLock)
        {
            if (_canDeinterlace is null)
            {
                try { _canDeinterlace = ffmpeg.avfilter_get_by_name("yadif") != null && ffmpeg.avfilter_get_by_name("buffer") != null; }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException) { _canDeinterlace = false; }
            }
            return _canDeinterlace.Value;
        }
    }
    static bool? _canDeinterlace;

    /// <summary>
    /// Whether FFmpeg 8's libraries load from <paramref name="directory"/>, as <see cref="CanLoad"/>; and libavfilter
    /// with yadif loads too, for deinterlacing in-process (<see cref="CanDeinterlace"/>).
    /// </summary>
    internal static bool CanLoad(string? directory)
    {
        lock (InitLock)
        {
            if (_loaded)
                return true;
            string dir = directory ?? "";
            if (!Loadable.TryGetValue(dir, out bool ok))
            {
                try { Load(directory); ok = true; }
                catch (ShotDetectionException) { ok = false; }
                Loadable[dir] = ok;
            }
            return ok;
        }
    }

    /// <summary>
    /// Loads FFmpeg's shared libraries from a folder or the system's usual places, once per process: the first copy
    /// loaded is the one used, whatever folder is asked for later.
    /// </summary>
    internal static void Load(string? directory)
    {
        lock (InitLock)
        {
            if (_loaded)
                return;
            Exception? error = null;
            // A folder given is the only place; otherwise next to the app first (a ShotDetector.Native.<rid> package:
            // runtimes/<rid>/native/ in a build's output, the app's folder once published), then the system's.
            string[] places = directory is not null ? [directory]
                : [Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"), AppContext.BaseDirectory, ""];
            foreach (string dir in places)
            {
                if (dir.Length > 0 && !Directory.Exists(dir))
                    continue;
                try
                {
                    ffmpeg.RootPath = dir;
                    DynamicallyLoadedBindings.Initialize();
                    _ = ffmpeg.avcodec_version(); // fails here if the libraries aren't there or aren't FFmpeg 8's
                    // A library doesn't write to stderr; failures surface as exceptions (the executable runs with -v error).
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                    _loaded = true;
                    return;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or BadImageFormatException)
                {
                    error = e;
                }
            }
            throw new ShotDetectionException(ShotDetectionError.FfmpegLibrariesNotFound,
                "In-process decoding needs FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, " +
                "libavutil 60): add the ShotDetector.Native.<rid> package for your platform, or install them (Alpine: " +
                "`apk add ffmpeg-libs`; Windows: a \"shared\" FFmpeg 8 build, with FfmpegDirectory set to its bin folder). " +
                $"Or decode with the ffmpeg executable (VideoDecoder.FfmpegProcess). ({error?.Message})", error);
        }
    }

    static void Check(int ret, string what, ShotDetectionError reason = ShotDetectionError.DecodeFailed)
    {
        if (ret < 0)
            throw new ShotDetectionException(reason, $"FFmpeg ({what}): {Error(ret)}");
    }

    /// <summary>FFmpeg's message for an error code.</summary>
    internal static string Error(int ret)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(ret, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? ret.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
