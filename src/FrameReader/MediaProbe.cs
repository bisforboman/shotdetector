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

    /// <summary>
    /// ffprobe's <c>-print_format json -show_format -show_streams</c> for the input: the same keys and values (every
    /// field ffprobe prints: profile, level, aspect ratios, colour, disposition, tags, side data, ...), leaving out
    /// unknown values as ffprobe's JSON does. Opens each stream's decoder, as ffprobe does, for its coded size and the like.
    /// </summary>
    /// <param name="path">What to open; also the JSON's <c>filename</c>.</param>
    /// <param name="options">Input options (<see cref="ProbeOptions.InputOptions"/>) and <see cref="ProbeOptions.CancellationToken"/>; the rest is unused.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or FFmpeg can't open the input.</exception>
    public static string ProbeJson(string path, ProbeOptions? options = null)
    {
        FFmpegLibraries.Load();
        var fmt = Demuxer.Open(path, options?.InputOptions, cancellationToken: options?.CancellationToken ?? default);
        try
        {
            return FfprobeJson.Write(fmt);
        }
        finally
        {
            Demuxer.Close(&fmt);
        }
    }

    /// <summary>As <see cref="ProbeJson"/>, off the calling thread.</summary>
    /// <param name="path">What to open; also the JSON's <c>filename</c>.</param>
    /// <param name="options">Input options (<see cref="ProbeOptions.InputOptions"/>) and cancellation; the rest is unused.</param>
    /// <param name="cancellationToken">Interrupts opening and reading the input where FFmpeg waits (a network read):
    /// <see cref="OperationCanceledException"/>. When it can't be cancelled, <see cref="ProbeOptions.CancellationToken"/> does.</param>
    public static Task<string> ProbeJsonAsync(string path, ProbeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
            options = (options ?? new ProbeOptions()) with { CancellationToken = cancellationToken };
        return Task.Run(() => ProbeJson(path, options), cancellationToken);
    }

    static MediaInfo Probe(string path, StreamInput? io, ProbeOptions? options)
    {
        FFmpegLibraries.Load();
        bool timestamps = options?.PacketTimestamps == true, first = options?.FirstVideoPacket == true;
        var cancellationToken = options?.CancellationToken ?? default;

        // An MP4/MOV file's packet timestamps come from its sample tables: the scan needn't read the media data (FileInput).
        // Where it would (another container or codec), the file is opened again as before, through FFmpeg's file protocol.
        var file = io == null && timestamps && options?.ReadsMediaData != true ? FileInput.For(path, cancellationToken) : null;
        AVFormatContext* fmt = null;
        try
        {
            fmt = Demuxer.Open(path, options?.InputOptions, io, cancellationToken, file);
            if (file != null && !SkipsMediaData(fmt, Demuxer.FirstVideoStream(fmt)))
            {
                Demuxer.Close(&fmt);
                file.Dispose();
                file = null;
                fmt = Demuxer.Open(path, options?.InputOptions, io, cancellationToken);
            }
        }
        catch
        {
            file?.Dispose();
            throw;
        }

        try
        {
            bool audio = false;
            for (int i = 0; i < fmt->nb_streams; i++)
                audio |= fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
            int index = Demuxer.FirstVideoStream(fmt);
            var streams = new List<StreamInfo>();
            for (int i = 0; i < fmt->nb_streams; i++)
                streams.Add(Stream(fmt->streams[i], i));
            var (pts, pos, packet) = index >= 0 && (timestamps || first) ? Packets(fmt, index, timestamps, file) : (null, null, null);
            return new MediaInfo
            {
                Container = Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) ?? "",
                Duration = Known(fmt->duration) is long us ? TimeSpan.FromTicks(us * 10) : null,
                Video = index < 0 ? null : Video(fmt->streams[index], index),
                HasAudio = audio,
                Streams = streams,
                PacketTimestamps = pts,
                PacketPositions = pos,
                FirstVideoPacket = first && packet is not null ? new ReadOnlyMemory<byte>(packet) : (ReadOnlyMemory<byte>?)null,
            };
        }
        finally
        {
            Demuxer.Close(&fmt);
            if (file != null)
            {
                options?.OnFileBytesRead?.Invoke(file.BytesRead);
                file.Dispose();
            }
        }
    }

    static VideoStreamInfo Video(AVStream* st, int index)
    {
        var par = st->codecpar;
        double? rotation = null;
        var matrix = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (matrix != null && matrix->size >= 36)
        {
            var m = default(int_array9);
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
            ChromaLocation = par->chroma_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED ? null : ffmpeg.av_chroma_location_name(par->chroma_location),
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
            ExtraData = par->extradata_size > 0 ? new ReadOnlySpan<byte>(par->extradata, par->extradata_size).ToArray() : ReadOnlyMemory<byte>.Empty,
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

    /// <summary>
    /// The video codecs whose packets libavformat's mov demuxer gives the same timestamps, positions and order without
    /// their data (checked on clips of each: MediaProbeTests.TimestampsWithoutMediaDataAreTheSame). Codecs it parses in
    /// full (MPEG-1, VC-1, VP8/9, AV1: a parser splits or joins packets by their data) aren't among them.
    /// </summary>
    internal static readonly IReadOnlySet<AVCodecID> TimestampsWithoutData = new HashSet<AVCodecID>
    {
        AVCodecID.AV_CODEC_ID_H264, AVCodecID.AV_CODEC_ID_HEVC, AVCodecID.AV_CODEC_ID_MPEG4, AVCodecID.AV_CODEC_ID_MPEG2VIDEO,
        AVCodecID.AV_CODEC_ID_MJPEG, AVCodecID.AV_CODEC_ID_PRORES,
    };

    /// <summary>Whether the packet scan can skip the media data: an MP4/MOV file (FileInput) whose video codec is among <see cref="TimestampsWithoutData"/>.</summary>
    static bool SkipsMediaData(AVFormatContext* fmt, int index) =>
        index >= 0 && Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) == "mov,mp4,m4a,3gp,3g2,mj2"
        && TimestampsWithoutData.Contains(fmt->streams[index]->codecpar->codec_id);

    /// <summary>
    /// The video packets' pts and positions (with <paramref name="all"/>; else only the first packet is read), and the first
    /// one's bytes. With <paramref name="skipping"/>, the media data reads as zeros from here on (the other streams' packets
    /// aren't read at all): the first packets come whole all the same, as avformat_find_stream_info read them and keeps
    /// them for av_read_frame.
    /// </summary>
    static (List<long?>? Pts, List<long?>? Pos, byte[]? First) Packets(AVFormatContext* fmt, int index, bool all, FileInput? skipping)
    {
        List<long?> result = [], positions = [];
        byte[]? first = null;
        var pkt = ffmpeg.av_packet_alloc();
        if (skipping != null)
        {
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (i != index)
                    fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }

            skipping.SkipMediaData = true;
        }

        try
        {
            int read;
            while ((read = ffmpeg.av_read_frame(fmt, pkt)) >= 0)
            {
                if (pkt->stream_index == index)
                {
                    first ??= new ReadOnlySpan<byte>(pkt->data, pkt->size).ToArray();
                    result.Add(Known(pkt->pts));
                    positions.Add(pkt->pos < 0 ? null : pkt->pos);
                }

                ffmpeg.av_packet_unref(pkt);
                if (!all && first is not null)
                    return (null, null, first);
            }

            Demuxer.ThrowIfReadFailed(fmt, read, skipping?.Error);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }

        return (all ? result : null, all ? positions : null, first);
    }

    static long? Known(long ts) => ts == ffmpeg.AV_NOPTS_VALUE ? null : ts;
}
