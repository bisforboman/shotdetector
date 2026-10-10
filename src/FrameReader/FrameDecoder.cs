using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// Decodes a video's first video stream with FFmpeg's libraries in this process (FFmpeg.AutoGen bindings, FFmpeg 8.1
/// ABI), frame by frame as ffmpeg's command line does: the same decoder and timestamps, yadif deinterlacing (FFmpeg's
/// filter on whole frames, or an <see cref="IRowDeinterlacer"/> on the rows asked for), and the display matrix applied
/// as its autorotate does. Not thread-safe; the current frame is valid until the next <see cref="Next"/>.
/// Advanced: part of the low-level layer ShotDetector's exact pipelines use; the main API is VideoFrameReader, the audio
/// readers, the writers, Remux and MediaProbe.
/// </summary>
public sealed unsafe class FrameDecoder : IDisposable
{
    AVFormatContext* _fmt;
    AVCodecContext* _dec;
    AVPacket* _pkt;
    AVFrame* _frame;
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
    // Deinterlacing with an IRowDeinterlacer instead: the previous, current and next decoded frames, and
    // _frame as the output, whose rows are computed when a writer asks for them (EnsureRows).
    readonly IRowDeinterlacer? _yadif;
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

    readonly StreamInput? _io;

    /// <summary>What reading a Stream input threw, if it did.</summary>
    public Exception? InputError => _io?.Error;

    /// <summary>
    /// What ffmpeg's command line adds to each timestamp without -copyts (its ts_offset, -start_time), in the stream's
    /// time base: streamed input's frame times start at 0.
    /// </summary>
    public long StartOffset => _fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0
        : ffmpeg.av_rescale_q(-_fmt->start_time, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, _timeBase);

    /// <summary>The stream's time base as FFmpeg keeps it.</summary>
    internal AVRational StreamTimeBase => _timeBase;

    /// <summary>The stream's time base in seconds per pts unit.</summary>
    public Rational TimeBase => new(_timeBase.num, _timeBase.den);

    /// <summary>The current frame's best-effort timestamp (what ffmpeg's command line gives each frame).</summary>
    public long Pts => _rowMode == true ? _rPts : _sink == null ? _frame->best_effort_timestamp
        : ffmpeg.av_rescale_q(_frame->pts, _sink->inputs[0]->time_base, _timeBase); // yadif doubles the time base: exact

    /// <summary>The current frame's own pts, if set (ffprobe's frame pts).</summary>
    public long? FramePts => _frame->pts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pts;

    /// <summary>The dts of the packet the current frame came from, if set (ffprobe's frame pkt_dts).</summary>
    public long? FramePacketDts => _frame->pkt_dts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pkt_dts;

    /// <summary>Opens a file, image sequence pattern or URL and its first video stream's decoder.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">How to decode; null: the defaults.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, the input can't be opened, has no video or no
    /// decoder for it.</exception>
    public FrameDecoder(string path, FrameDecoderOptions? options = null) : this(path, options, null) { }

    /// <summary>
    /// Opens the bytes a stream gives, read once from where it is (not seekable, as ffmpeg reads a pipe): the headers
    /// must come first (mkv, webm, ts, mov or a faststart mp4). Frame times are the stream's own; see
    /// <see cref="StartOffset"/> for ffmpeg's shift to 0.
    /// </summary>
    /// <param name="stream">The media's bytes; not disposed.</param>
    /// <param name="options">How to decode; null: the defaults.</param>
    public FrameDecoder(Stream stream, FrameDecoderOptions? options = null) : this("pipe:0", options, new StreamInput([], stream)) { }

    /// <param name="path">What to open (a name only when <paramref name="io"/> is given).</param>
    /// <param name="options">How to decode.</param>
    /// <param name="io">A Stream to read instead of <paramref name="path"/>; the decoder disposes it.</param>
    internal FrameDecoder(string path, FrameDecoderOptions? options, StreamInput? io)
    {
        var o = options ?? new FrameDecoderOptions();
        bool deinterlace = o.Deinterlace;
        int threads = o.Threads;
        _io = io;
        // Whatever fails after this point, what was opened so far is freed (the input, the decoder).
        try
        {
            FFmpegLibraries.Load();
            _deinterlace = deinterlace;
            _yadif = deinterlace ? o.RowDeinterlacer : null;
            _fmt = Demuxer.Open(path, o.InputOptions, io);
            _stream = Demuxer.FirstVideoStream(_fmt);
            if (_stream < 0)
                throw new FrameReaderException(FrameReaderError.InvalidInput, "The input has no video stream.");
            var st = _fmt->streams[_stream];
            _timeBase = st->time_base;
            var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (codec == null)
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"No decoder for {st->codecpar->codec_id} in these FFmpeg libraries.");
            _dec = ffmpeg.avcodec_alloc_context3(codec);
            Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "decoder parameters");
            _dec->pkt_timebase = st->time_base; // as ffmpeg's command line sets it (best-effort timestamps use it)
            _dec->thread_count = threads;
            if (o.HardwareDecoding)
                SetUpHardware(codec, st->codecpar);

            if (o.KeyframesOnly)
                _dec->skip_frame = AVDiscard.AVDISCARD_NONKEY;
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
                    throw new FrameReaderException(FrameReaderError.DecodeFailed, "Deinterlacing rotated or flipped video isn't supported in-process.");
                _oriented = ffmpeg.av_frame_alloc();
            }
        }
        catch
        {
            Dispose();
            throw;
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
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"Rotation by {theta} degrees isn't supported in-process.");
        return (-1, false, m[4] < 0);
    }

    /// <summary>Whether the loaded libraries have a decoder for the codec (ffprobe's codec_name) that gives frames.</summary>
    /// <param name="codec">The codec's FFmpeg name.</param>
    public static bool HasDecoder(string codec)
    {
        var desc = ffmpeg.avcodec_descriptor_get_by_name(codec);
        if (desc != null && desc->id == AVCodecID.AV_CODEC_ID_AV1)
            // FFmpeg's own av1 decoder needs a GPU (it gives no frames): dav1d or libaom it is.
            return ffmpeg.avcodec_find_decoder_by_name("libdav1d") != null || ffmpeg.avcodec_find_decoder_by_name("libaom-av1") != null;
        return desc != null && ffmpeg.avcodec_find_decoder(desc->id) != null;
    }

    /// <summary>Whether a rotation (ffprobe's, degrees) of this pixel format is one this decoder applies.</summary>
    /// <param name="rotation">The display rotation in degrees.</param>
    /// <param name="pixelFormat">FFmpeg's pixel format name.</param>
    public static bool CanRotate(int rotation, string? pixelFormat)
    {
        if (rotation % 90 != 0)
            return false;
        if (rotation % 180 == 0)
            return true;
        var desc = ffmpeg.av_pix_fmt_desc_get(ffmpeg.av_get_pix_fmt(pixelFormat ?? ""));
        return desc != null && desc->log2_chroma_w == desc->log2_chroma_h; // as the transpose filter's formats
    }

    /// <summary>Seeks to the keyframe at or before <paramref name="pts"/> (stream time base), as ffmpeg's -ss does.</summary>
    /// <param name="pts">The timestamp, in <see cref="TimeBase"/> units.</param>
    public void Seek(long pts) =>
        SeekMicroseconds(ffmpeg.av_rescale_q(pts, _timeBase, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }));

    /// <summary>
    /// Seeks as ffmpeg's <c>-ss</c> does for <paramref name="time"/> from the input's start: to the keyframe at or before
    /// it (with ffmpeg's margin for formats that seek by dts when a stream has B-frames). Returns what to add to
    /// <see cref="Pts"/> so that the frames to keep are those at 0 or later (ffmpeg's ts_offset, which its trim then
    /// compares with 0).
    /// </summary>
    internal long SeekLikeFfmpeg(TimeSpan time)
    {
        long timestamp = time.Ticks / 10; // ffmpeg's -ss in microseconds
        if (_fmt->start_time != ffmpeg.AV_NOPTS_VALUE)
            timestamp += _fmt->start_time;
        SeekMicroseconds(Demuxer.SeekPoint(_fmt, timestamp));
        return TrimOffset(time);
    }

    /// <summary>
    /// The keyframe at or before <paramref name="pts"/> from the container's index (mp4, mov, mkv cues; none for
    /// MPEG-TS until read), in the stream's time base (the index's timestamps, dts in some containers); null when the
    /// index doesn't say.
    /// </summary>
    internal long? KeyframeAtOrBefore(long pts)
    {
        var st = _fmt->streams[_stream];
        int i = ffmpeg.av_index_search_timestamp(st, pts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        return i < 0 ? null : ffmpeg.avformat_index_get_entry(st, i)->timestamp;
    }

    /// <summary>Whether the current frame is a keyframe.</summary>
    internal bool IsKeyframe => (_frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

    /// <summary>Whether this decoder can still seek (not deinterlacing, or before its first frame).</summary>
    internal bool CanSeek => !_deinterlace || !_started;

    /// <summary>
    /// ffmpeg's ts_offset for <c>-ss</c> <paramref name="time"/>, in the stream's time base: a frame is at or after the
    /// time when its <see cref="Pts"/> plus this is 0 or more (ffmpeg's trim).
    /// </summary>
    internal long TrimOffset(TimeSpan time)
    {
        long timestamp = time.Ticks / 10;
        if (_fmt->start_time != ffmpeg.AV_NOPTS_VALUE)
            timestamp += _fmt->start_time;
        return ffmpeg.av_rescale_q(-timestamp, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, _timeBase);
    }

    void SeekMicroseconds(long us)
    {
        // Deinterlacing keeps frames in yadif (or the row deinterlacer); only a seek before the first frame is clean.
        if (_deinterlace && _started)
            throw new InvalidOperationException("A deinterlacing decoder can seek only before its first frame.");
        Check(ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, us, us, 0), "seek");
        ffmpeg.avcodec_flush_buffers(_dec);
        _draining = _done = false;
    }

    bool _started;

    /// <summary>Decodes the next frame; false at the end. Packets that don't decode are skipped, as ffmpeg's command line does.</summary>
    public bool Next()
    {
        _started = true;
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
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"Rotating {(AVPixelFormat)_frame->format} isn't supported in-process.");
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
    /// Row-wise deinterlacing (<see cref="IRowDeinterlacer"/>): yadif's frame order (output i from frames i-1, i and i+1, the
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
                throw new FrameReaderException(FrameReaderError.DecodeFailed, "Deinterlacing: frames with differing strides.");
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
    internal void EnsureRows(int from, int to)
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
    internal void ComputeRows()
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
    internal void MarkRows(int from, int to)
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
            {
                if (_hwDevice != null && (AVPixelFormat)into->format == _hwFormat)
                    FromHardware(into);
                return true;
            }
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

    /// <summary>The current frame, as FFmpeg holds it (ShotDetector's conversions read it directly).</summary>
    internal AVFrame* Frame => _frame;

    /// <summary>Whether rows are deinterlaced on demand (<see cref="EnsureRows"/>).</summary>
    internal bool RowMode => _rowMode == true;

    /// <summary>The current frame's width in pixels (after rotation).</summary>
    public int Width => _frame->width;

    /// <summary>The current frame's height in pixels (after rotation).</summary>
    public int Height => _frame->height;

    /// <summary>The current frame's FFmpeg pixel format name (yuv420p, ...).</summary>
    public string PixelFormat => ffmpeg.av_get_pix_fmt_name((AVPixelFormat)_frame->format) ?? "unknown";

    /// <summary>
    /// One plane of the current frame (0: luma or the packed pixels; 1, 2: chroma; 3: alpha), its rows
    /// <paramref name="stride"/> bytes apart; valid until the next <see cref="Next"/>.
    /// </summary>
    /// <param name="plane">The plane's index.</param>
    /// <param name="stride">Bytes from one row to the next.</param>
    public ReadOnlySpan<byte> GetPlane(int plane, out int stride)
    {
        EnsureRows(0, _frame->height);
        if (plane is < 0 or > 3 || _frame->data[(uint)plane] == null)
            throw new ArgumentOutOfRangeException(nameof(plane));
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)_frame->format);
        int shift = plane is 1 or 2 && desc != null ? desc->log2_chroma_h : 0;
        int rows = -((-_frame->height) >> shift);
        stride = _frame->linesize[(uint)plane];
        return new ReadOnlySpan<byte>(_frame->data[(uint)plane], stride * rows);
    }

    // Hardware decoding: the device, the GPU's pixel format for this codec, and the frame each GPU frame is copied to.
    AVBufferRef* _hwDevice;
    AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    AVFrame* _hwCopy, _hwYuv;
    AVCodecContext_get_format? _getFormat; // kept alive while FFmpeg holds a pointer to it

    /// <summary>Whether frames are decoded on the GPU (<see cref="FrameDecoderOptions.HardwareDecoding"/>, and a GPU decoder took the stream).</summary>
    public bool UsesHardware => _hwDevice != null;

    void SetUpHardware(AVCodec* codec, AVCodecParameters* par)
    {
        // 8-bit 4:2:0 only: the GPU gives NV12, which turns back into yuv420p losslessly.
        var format = (AVPixelFormat)par->format;
        if (!OperatingSystem.IsWindows() || format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
            return;
        for (int i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                return;
            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA && (config->methods & 1 /* AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX */) != 0)
            {
                _hwFormat = config->pix_fmt;
                break;
            }
        }
        AVBufferRef* device = null;
        if (ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) < 0)
        {
            _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            return;
        }
        _hwDevice = device;
        _dec->hw_device_ctx = ffmpeg.av_buffer_ref(device);
        _getFormat = (ctx, formats) =>
        {
            // The GPU's format when offered, else the first software one (FFmpeg's choice without a GPU).
            AVPixelFormat first = AVPixelFormat.AV_PIX_FMT_NONE;
            for (var f = formats; *f != AVPixelFormat.AV_PIX_FMT_NONE; f++)
            {
                if (*f == _hwFormat)
                    return *f;
                var desc = ffmpeg.av_pix_fmt_desc_get(*f);
                if (first == AVPixelFormat.AV_PIX_FMT_NONE && desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
                    first = *f;
            }
            return first;
        };
        _dec->get_format = _getFormat;
        _hwCopy = ffmpeg.av_frame_alloc();
        _hwYuv = ffmpeg.av_frame_alloc();
    }

    /// <summary>A GPU frame copied back (NV12) and laid out as the software decoder's yuv420p, its properties kept.</summary>
    void FromHardware(AVFrame* frame)
    {
        ffmpeg.av_frame_unref(_hwCopy);
        Check(ffmpeg.av_hwframe_transfer_data(_hwCopy, frame, 0), "copy from the GPU");
        if ((AVPixelFormat)_hwCopy->format != AVPixelFormat.AV_PIX_FMT_NV12)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"The GPU gave {(AVPixelFormat)_hwCopy->format}, not NV12.");
        // One output buffer, reused once nobody holds the last frame (the decoder unrefs it before the next).
        var yuv = _hwYuv;
        var format = _dec->sw_pix_fmt is AVPixelFormat.AV_PIX_FMT_YUVJ420P ? AVPixelFormat.AV_PIX_FMT_YUVJ420P : AVPixelFormat.AV_PIX_FMT_YUV420P;
        if (yuv->width != frame->width || yuv->height != frame->height || yuv->format != (int)format || ffmpeg.av_frame_is_writable(yuv) == 0)
        {
            ffmpeg.av_frame_unref(yuv);
            (yuv->format, yuv->width, yuv->height) = ((int)format, frame->width, frame->height);
            Check(ffmpeg.av_frame_get_buffer(yuv, 32), "frame buffer");
        }
        int w = frame->width, h = frame->height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        for (int y = 0; y < h; y++)
            Buffer.MemoryCopy(_hwCopy->data[0] + (long)y * _hwCopy->linesize[0], yuv->data[0] + (long)y * yuv->linesize[0], w, w);
        for (int y = 0; y < ch; y++)
            Deinterleave(_hwCopy->data[1] + (long)y * _hwCopy->linesize[1], yuv->data[1] + (long)y * yuv->linesize[1], yuv->data[2] + (long)y * yuv->linesize[2], cw);
        ffmpeg.av_frame_side_data_free(&yuv->side_data, &yuv->nb_side_data);
        ffmpeg.av_dict_free(&yuv->metadata);
        Check(ffmpeg.av_frame_copy_props(yuv, frame), "frame");
        ffmpeg.av_frame_unref(frame);
        Check(ffmpeg.av_frame_ref(frame, yuv), "frame");
    }

    /// <summary>NV12's interleaved chroma row into U and V rows (16 pairs at a time).</summary>
    static void Deinterleave(byte* uv, byte* u, byte* v, int pairs)
    {
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
            for (; x + 16 <= pairs; x += 16)
            {
                var a = Vector128.Load((ushort*)(uv + 2 * x));
                var b = Vector128.Load((ushort*)(uv + 2 * x + 16));
                var mask = Vector128.Create((ushort)0xFF);
                Vector128.Narrow(a & mask, b & mask).Store(u + x);
                Vector128.Narrow(a >>> 8, b >>> 8).Store(v + x);
            }
        for (; x < pairs; x++)
        {
            u[x] = uv[2 * x];
            v[x] = uv[2 * x + 1];
        }
    }

    /// <summary>Frees the decoder, its frames and FFmpeg's contexts.</summary>
    public void Dispose()
    {
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
        _io?.Dispose();
        if (_hwCopy != null) { var f = _hwCopy; ffmpeg.av_frame_free(&f); _hwCopy = null; }
        if (_hwYuv != null) { var f = _hwYuv; ffmpeg.av_frame_free(&f); _hwYuv = null; }
        if (_hwDevice != null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
