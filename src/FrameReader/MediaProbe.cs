using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A media file's properties from its headers, read in this process with FFmpeg's libraries (no ffprobe): the values
/// ffprobe prints, and optionally every video packet's timestamp (a demux-only pass, no decoding).
/// </summary>
public static unsafe class MediaProbe
{
    /// <summary>Probes a file, image sequence pattern or URL FFmpeg can open.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">What else to read; null: the headers only.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or FFmpeg can't open the input.</exception>
    public static MediaInfo Probe(string path, ProbeOptions? options = null) => Probe(path, null, options);

    /// <summary>
    /// Probes the bytes a stream gives, read once from where it is (not seekable, as ffprobe reads a pipe): the headers
    /// must come first (mkv, webm, ts, mov or a faststart mp4).
    /// </summary>
    /// <param name="stream">The media's bytes.</param>
    /// <param name="options">What else to read; null: the headers only.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or FFmpeg can't read the stream.</exception>
    public static MediaInfo Probe(Stream stream, ProbeOptions? options = null)
    {
        using var io = new StreamInput([], stream);
        return Probe("pipe:0", io, options);
    }

    static MediaInfo Probe(string path, StreamInput? io, ProbeOptions? options)
    {
        FFmpegLibraries.Load();
        var fmt = Demuxer.Open(path, options?.InputOptions, io);
        try
        {
            bool audio = false;
            for (int i = 0; i < fmt->nb_streams; i++)
                audio |= fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
            int index = Demuxer.FirstVideoStream(fmt);
            var streams = new List<StreamInfo>();
            for (int i = 0; i < fmt->nb_streams; i++)
                streams.Add(Stream(fmt->streams[i], i));
            return new MediaInfo
            {
                Container = Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) ?? "",
                Duration = Known(fmt->duration) is long us ? TimeSpan.FromTicks(us * 10) : null,
                Video = index < 0 ? null : Video(fmt->streams[index], index),
                HasAudio = audio,
                Streams = streams,
                PacketTimestamps = index >= 0 && options?.PacketTimestamps == true ? Packets(fmt, index) : null,
            };
        }
        finally
        {
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    static VideoStreamInfo Video(AVStream* st, int index)
    {
        var par = st->codecpar;
        double? rotation = null;
        var matrix = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (matrix != null && matrix->size >= 36)
        {
            var m = new int_array9();
            for (uint i = 0; i < 9; i++)
                m[i] = ((int*)matrix->data)[i];
            rotation = ffmpeg.av_display_rotation_get(in m);
        }
        return new VideoStreamInfo
        {
            Index = index,
            Codec = ffmpeg.avcodec_get_name(par->codec_id) ?? "unknown",
            Width = par->width,
            Height = par->height,
            PixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format),
            ColorRange = par->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? null : ffmpeg.av_color_range_name(par->color_range),
            ColorSpace = par->color_space == AVColorSpace.AVCOL_SPC_UNSPECIFIED ? null : ffmpeg.av_color_space_name(par->color_space),
            FieldOrder = par->field_order switch
            {
                AVFieldOrder.AV_FIELD_PROGRESSIVE => FieldOrder.Progressive,
                AVFieldOrder.AV_FIELD_TT => FieldOrder.TopFirst,
                AVFieldOrder.AV_FIELD_BB => FieldOrder.BottomFirst,
                AVFieldOrder.AV_FIELD_TB => FieldOrder.TopCodedBottomFirst,
                AVFieldOrder.AV_FIELD_BT => FieldOrder.BottomCodedTopFirst,
                _ => FieldOrder.Unknown,
            },
            FrameRate = new(st->r_frame_rate.num, st->r_frame_rate.den),
            AverageFrameRate = new(st->avg_frame_rate.num, st->avg_frame_rate.den),
            TimeBase = new(st->time_base.num, st->time_base.den),
            StartPts = Known(st->start_time),
            DurationPts = Known(st->duration),
            FrameCount = st->nb_frames != 0 ? st->nb_frames : null,
            Rotation = rotation,
        };
    }

    static StreamInfo Stream(AVStream* st, int index)
    {
        var par = st->codecpar;
        var desc = ffmpeg.avcodec_descriptor_get(par->codec_id);
        byte* tag = stackalloc byte[ffmpeg.AV_FOURCC_MAX_STRING_SIZE];
        ffmpeg.av_fourcc_make_string(tag, par->codec_tag);
        bool video = par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO, audio = par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
        string? layout = null;
        if (audio && par->ch_layout.order != AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
        {
            byte* buf = stackalloc byte[128];
            if (ffmpeg.av_channel_layout_describe(&par->ch_layout, buf, 128) > 0)
                layout = Marshal.PtrToStringAnsi((IntPtr)buf);
        }
        string? Tag(string key)
        {
            var e = ffmpeg.av_dict_get(st->metadata, key, null, 0);
            return e == null ? null : Marshal.PtrToStringUTF8((IntPtr)e->value);
        }
        return new StreamInfo
        {
            Index = index,
            Type = par->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => StreamType.Video,
                AVMediaType.AVMEDIA_TYPE_AUDIO => StreamType.Audio,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => StreamType.Subtitle,
                AVMediaType.AVMEDIA_TYPE_DATA => StreamType.Data,
                AVMediaType.AVMEDIA_TYPE_ATTACHMENT => StreamType.Attachment,
                _ => StreamType.Unknown,
            },
            Codec = desc != null ? Marshal.PtrToStringAnsi((IntPtr)desc->name) ?? "unknown" : "unknown",
            CodecTag = Marshal.PtrToStringAnsi((IntPtr)tag) ?? "",
            Language = Tag("language"),
            Title = Tag("title"),
            IsDefault = (st->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
            IsForced = (st->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0,
            BitRate = par->bit_rate > 0 ? par->bit_rate : null,
            DurationPts = Known(st->duration),
            TimeBase = new(st->time_base.num, st->time_base.den),
            Width = video ? par->width : null,
            Height = video ? par->height : null,
            SampleRate = audio ? par->sample_rate : null,
            Channels = audio ? par->ch_layout.nb_channels : null,
            ChannelLayout = layout,
            SampleFormat = audio ? ffmpeg.av_get_sample_fmt_name((AVSampleFormat)par->format) : null,
        };
    }

    static List<long?> Packets(AVFormatContext* fmt, int index)
    {
        var result = new List<long?>();
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index == index)
                    result.Add(Known(pkt->pts));
                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
        return result;
    }

    static long? Known(long ts) => ts == ffmpeg.AV_NOPTS_VALUE ? null : ts;
}
