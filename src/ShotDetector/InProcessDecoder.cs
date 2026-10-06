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
    static string? _loadedFrom;

    AVFormatContext* _fmt;
    AVCodecContext* _dec;
    AVPacket* _pkt;
    AVFrame* _frame, _bgr;
    SwsContext* _toBgr, _toSize;
    readonly int _stream;
    readonly AVRational _timeBase;
    bool _draining, _done;

    /// <summary>The stream's time base in seconds per pts unit.</summary>
    public double TimeBase => _timeBase.num / (double)_timeBase.den;

    /// <summary>The current frame's best-effort timestamp (what ffmpeg's command line gives each frame).</summary>
    public long Pts => _frame->best_effort_timestamp;

    public InProcessDecoder(string path, string? libraryDirectory, int threads)
    {
        Load(libraryDirectory);
        AVFormatContext* fmt = null;
        Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "open " + path);
        _fmt = fmt;
        Check(ffmpeg.avformat_find_stream_info(_fmt, null), "read stream info");
        // The first video stream, as -map 0:v:0 (and ffprobe -select_streams v:0) take it.
        _stream = -1;
        for (int i = 0; i < _fmt->nb_streams; i++)
            if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _stream = i;
                break;
            }
        if (_stream < 0)
            throw new InvalidOperationException("The input has no video stream.");
        var st = _fmt->streams[_stream];
        _timeBase = st->time_base;
        var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
            throw new InvalidOperationException($"No decoder for {st->codecpar->codec_id} in these FFmpeg libraries.");
        _dec = ffmpeg.avcodec_alloc_context3(codec);
        Check(ffmpeg.avcodec_parameters_to_context(_dec, st->codecpar), "decoder parameters");
        _dec->pkt_timebase = st->time_base; // as ffmpeg's command line sets it (best-effort timestamps use it)
        _dec->thread_count = threads;
        Check(ffmpeg.avcodec_open2(_dec, codec, null), "open decoder");
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
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
        while (!_done)
        {
            int got = ffmpeg.avcodec_receive_frame(_dec, _frame);
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
        byte* bgr = ToBgr(out int stride);
        int o = 0;
        foreach (int r in rows)
        {
            byte* row = bgr + r * stride;
            foreach (int c in columns)
            {
                dst[o++] = row[3 * c];
                dst[o++] = row[3 * c + 1];
                dst[o++] = row[3 * c + 2];
            }
        }
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
                throw new InvalidOperationException("Can't set up the scaler.");
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

    /// <summary>The whole frame as BGR, converted like ffmpeg's scale filter (and OpenCV): bicubic flag, colour tags.</summary>
    byte* ToBgr(out int stride)
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
        Check(ffmpeg.sws_scale_frame(_toBgr, _bgr, _frame), "convert");
        stride = _bgr->linesize[0];
        return _bgr->data[0];
    }

    public void Dispose()
    {
        if (_toSize != null) { ffmpeg.sws_freeContext(_toSize); _toSize = null; }
        if (_toBgr != null) { var s = _toBgr; ffmpeg.sws_free_context(&s); _toBgr = null; }
        if (_bgr != null) { var f = _bgr; ffmpeg.av_frame_free(&f); _bgr = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
        if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }

    /// <summary>Loads FFmpeg's shared libraries once per process, from a folder or the system's default places.</summary>
    static void Load(string? directory)
    {
        lock (InitLock)
        {
            string dir = directory ?? "";
            if (_loadedFrom is not null)
            {
                if (_loadedFrom != dir)
                    throw new InvalidOperationException(
                        $"FFmpeg's libraries were already loaded from '{_loadedFrom}'; one process can use only one copy.");
                return;
            }
            try
            {
                ffmpeg.RootPath = dir;
                DynamicallyLoadedBindings.Initialize();
                _ = ffmpeg.avcodec_version(); // fails here if the libraries aren't there or aren't FFmpeg 8.1's
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or BadImageFormatException)
            {
                throw new InvalidOperationException(
                    "In-process decoding needs FFmpeg 8.1's shared libraries (libavcodec 62, libavformat 62, libswscale 9, " +
                    "libavutil 60): on Alpine `apk add ffmpeg-libs`, on Windows a \"shared\" FFmpeg 8.1 build (set FfmpegDirectory " +
                    $"to its bin folder). Or use the default decoder (the ffmpeg executable). ({e.Message})", e);
            }
            _loadedFrom = dir;
        }
    }

    static void Check(int ret, string what)
    {
        if (ret >= 0)
            return;
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(ret, buf, 256);
        throw new InvalidOperationException($"FFmpeg ({what}): {Marshal.PtrToStringAnsi((IntPtr)buf)}");
    }
}
