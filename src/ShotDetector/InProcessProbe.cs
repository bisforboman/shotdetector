using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace ShotDetector;

/// <summary>
/// What VideoReader asks ffprobe for, read with FFmpeg's libraries in this process instead (when they load): the
/// same text ffprobe prints, field by field with its formatting, so the parsing and everything after it stay as
/// they are. Files only (streams, URLs and image sequences keep ffprobe).
/// </summary>
internal static unsafe class InProcessProbe
{
    /// <summary>
    /// <c>ffprobe -select_streams v:0 -show_entries stream=...:stream_side_data=rotation:format=duration:packet=pts
    /// -of default=nw=1</c>'s output (the entries VideoReader reads).
    /// </summary>
    public static string Properties(string path, string? libraryDirectory, bool packets = true)
    {
        InProcessDecoder.Load(libraryDirectory);
        var fmt = Open(path);
        try
        {
            int index = FirstVideoStream(fmt);
            var sb = new StringBuilder();
            if (index < 0)
                return ""; // no stream entries, as ffprobe prints none
            var st = fmt->streams[index];
            var par = st->codecpar;
            void Line(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
            Line("codec_name", Name(ffmpeg.avcodec_get_name(par->codec_id)) ?? "unknown");
            // ffprobe's names for the field order.
            Line("field_order", par->field_order switch
            {
                AVFieldOrder.AV_FIELD_PROGRESSIVE => "progressive", AVFieldOrder.AV_FIELD_TT => "tt", AVFieldOrder.AV_FIELD_BB => "bb",
                AVFieldOrder.AV_FIELD_TB => "tb", AVFieldOrder.AV_FIELD_BT => "bt", _ => "unknown",
            });
            Line("width", par->width.ToString(CultureInfo.InvariantCulture));
            Line("height", par->height.ToString(CultureInfo.InvariantCulture));
            Line("pix_fmt", Name(ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format)) ?? "unknown");
            Line("color_range", par->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? "unknown" : Name(ffmpeg.av_color_range_name(par->color_range)) ?? "unknown");
            Line("color_space", par->color_space == AVColorSpace.AVCOL_SPC_UNSPECIFIED ? "unknown" : Name(ffmpeg.av_color_space_name(par->color_space)) ?? "unknown");
            Line("r_frame_rate", Rational(st->r_frame_rate));
            Line("avg_frame_rate", Rational(st->avg_frame_rate));
            Line("time_base", Rational(st->time_base));
            Line("start_pts", Timestamp(st->start_time));
            Line("start_time", Time(st->start_time, st->time_base));
            Line("duration", Time(st->duration, st->time_base));
            Line("nb_frames", st->nb_frames != 0 ? st->nb_frames.ToString(CultureInfo.InvariantCulture) : "N/A");
            var matrix = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
            if (matrix != null && matrix->size >= 36)
            {
                var m = new int_array9();
                for (uint i = 0; i < 9; i++)
                    m[i] = ((int*)matrix->data)[i];
                // ffprobe prints it with print_int: the double, truncated.
                Line("rotation", ((long)ffmpeg.av_display_rotation_get(in m)).ToString(CultureInfo.InvariantCulture));
            }
            Line("format_name", Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) ?? "");
            Line("duration", Time(fmt->duration, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }));
            if (!packets)
                return sb.ToString();
            var pkt = ffmpeg.av_packet_alloc();
            try
            {
                while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
                {
                    if (pkt->stream_index == index)
                        Line("pts", Timestamp(pkt->pts));
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
            }
            return sb.ToString();
        }
        finally
        {
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    /// <summary>Whether the file has an audio stream (<c>ffprobe -select_streams a</c> finds one).</summary>
    public static bool HasAudio(string path, string? libraryDirectory)
    {
        InProcessDecoder.Load(libraryDirectory);
        var fmt = Open(path);
        try
        {
            for (int i = 0; i < fmt->nb_streams; i++)
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    return true;
            return false;
        }
        finally
        {
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    /// <summary>
    /// <c>ffprobe -select_streams v:0 -show_entries frame=pts,pkt_dts</c> as VideoReader uses it (MPEG-PS, packets
    /// without pts): each decoded frame's pts when set and non-zero, else its packet's dts, in output order.
    /// </summary>
    public static List<long> FrameTimestamps(string path, string? libraryDirectory)
    {
        var result = new List<long>();
        using var decoder = new InProcessDecoder(path, libraryDirectory, threads: 0);
        while (decoder.Next())
            result.Add(decoder.FramePts is long p && p != 0 ? p : decoder.FramePacketDts ?? 0);
        return result;
    }

    /// <summary>Opens a file as ffprobe and ffmpeg do (scan_all_pmts for MPEG-TS) and reads its stream info.</summary>
    internal static AVFormatContext* Open(string path)
    {
        AVFormatContext* fmt = null;
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, &options);
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"FFmpeg (open {path}): {InProcessDecoder.Error(ret)}");
        ret = ffmpeg.avformat_find_stream_info(fmt, null);
        if (ret < 0)
        {
            ffmpeg.avformat_close_input(&fmt);
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"FFmpeg (read stream info): {InProcessDecoder.Error(ret)}");
        }
        return fmt;
    }

    /// <summary>The first video stream's index, as -select_streams v:0 / -map 0:v:0 take it; -1 if none.</summary>
    internal static int FirstVideoStream(AVFormatContext* fmt)
    {
        for (int i = 0; i < fmt->nb_streams; i++)
            if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return i;
        return -1;
    }

    static string? Name(string? s) => s;

    static string Rational(AVRational r) => $"{r.num}/{r.den}";

    static string Timestamp(long ts) => ts == ffmpeg.AV_NOPTS_VALUE ? "N/A" : ts.ToString(CultureInfo.InvariantCulture);

    /// <summary>ffprobe's print_time: the timestamp in seconds with printf's %f, or N/A.</summary>
    static string Time(long ts, AVRational tb) =>
        ts == ffmpeg.AV_NOPTS_VALUE ? "N/A" : (ts * (tb.num / (double)tb.den)).ToString("F6", CultureInfo.InvariantCulture);
}
