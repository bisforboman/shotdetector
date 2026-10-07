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

    /// <summary>The stream's time base in seconds per pts unit.</summary>
    public double TimeBase => _timeBase.num / (double)_timeBase.den;

    /// <summary>The current frame's best-effort timestamp (what ffmpeg's command line gives each frame).</summary>
    public long Pts => _sink == null ? _frame->best_effort_timestamp
        : ffmpeg.av_rescale_q(_frame->pts, _sink->inputs[0]->time_base, _timeBase); // yadif doubles the time base: exact

    /// <summary>The current frame's own pts, if set (ffprobe's frame pts).</summary>
    public long? FramePts => _frame->pts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pts;

    /// <summary>The dts of the packet the current frame came from, if set (ffprobe's frame pkt_dts).</summary>
    public long? FramePacketDts => _frame->pkt_dts == ffmpeg.AV_NOPTS_VALUE ? null : _frame->pkt_dts;

    /// <param name="path">The file.</param>
    /// <param name="libraryDirectory">Where FFmpeg's libraries are (null: next to the app, then the system).</param>
    /// <param name="threads">Decoder threads (0: FFmpeg's choice).</param>
    /// <param name="deinterlace">Deinterlace with yadif (its defaults, as ffmpeg's -vf yadif); needs libavfilter.</param>
    public InProcessDecoder(string path, string? libraryDirectory, int threads, bool deinterlace = false)
    {
        Load(libraryDirectory);
        _deinterlace = deinterlace;
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
        if (!_deinterlace)
            return Decode(_frame);
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
            if (!Decode(_decoded))
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
        byte* bgr = ToBgr(out int stride);
        fixed (byte* d = dst)
            for (int y = 0; y < crop.Height; y++)
                Buffer.MemoryCopy(bgr + (crop.Y + y) * stride + crop.X * 3, d + y * crop.Width * 3, crop.Width * 3, crop.Width * 3);
    }

    /// <summary>The converted frame's crop region scaled to width × height with swscale's bilinear (…,scale=W:H:flags=bilinear).</summary>
    public void WriteScaledBgr(byte[] dst, (int X, int Y, int Width, int Height) crop, int width, int height)
    {
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
            Check(ffmpeg.sws_scale_frame(_toBgr, _bgr, _frame), "convert");
        else
        {
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
        if (_decoded != null) { var f = _decoded; ffmpeg.av_frame_free(&f); _decoded = null; }
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
            throw new ShotDetectionException(ShotDetectionError.FfmpegNotFound,
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
