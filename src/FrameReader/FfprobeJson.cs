using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// ffprobe's <c>-print_format json -show_format -show_streams</c>, ported from fftools/ffprobe.c (FFmpeg 8.1): the same
/// keys, value types and omissions (JSON leaves out what ffprobe calls optional: unknown times, rates, profiles, ...).
/// </summary>
internal static unsafe class FfprobeJson
{
    internal static string Write(AVFormatContext* fmt)
    {
        var decoders = new AVCodecContext*[fmt->nb_streams];
        try
        {
            for (int i = 0; i < fmt->nb_streams; i++)
                decoders[i] = OpenDecoder(fmt->streams[i]);
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteStartArray("streams");
                for (int i = 0; i < fmt->nb_streams; i++)
                    Stream(w, fmt, fmt->streams[i], decoders[i]);
                w.WriteEndArray();
                Format(w, fmt);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        finally
        {
            for (int i = 0; i < decoders.Length; i++)
            {
                var d = decoders[i];
                if (d != null)
                    ffmpeg.avcodec_free_context(&d);
            }
        }
    }

    /// <summary>The stream's decoder opened as ffprobe binds one (open_input_file); null when there's none or it doesn't open.</summary>
    static AVCodecContext* OpenDecoder(AVStream* st)
    {
        var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
            return null;
        var dec = ffmpeg.avcodec_alloc_context3(codec);
        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "flags", "+copy_opaque", ffmpeg.AV_DICT_MULTIKEY);
        if (ffmpeg.avcodec_parameters_to_context(dec, st->codecpar) < 0)
        {
            ffmpeg.av_dict_free(&opts);
            ffmpeg.avcodec_free_context(&dec);
            return null;
        }

        dec->pkt_timebase = st->time_base;
        int ret = ffmpeg.avcodec_open2(dec, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0)
            ffmpeg.avcodec_free_context(&dec);
        return dec;
    }

    static void Stream(Utf8JsonWriter w, AVFormatContext* fmt, AVStream* st, AVCodecContext* dec)
    {
        var par = st->codecpar;
        w.WriteStartObject();
        w.WriteNumber("index", st->index);
        var cd = ffmpeg.avcodec_descriptor_get(par->codec_id);
        if (cd != null)
        {
            w.WriteString("codec_name", Str(cd->name));
            w.WriteString("codec_long_name", cd->long_name != null ? Str(cd->long_name) : "unknown");
        }

        if (ffmpeg.avcodec_profile_name(par->codec_id, par->profile) is { } profile)
            w.WriteString("profile", profile);
        else if (par->profile != ffmpeg.AV_PROFILE_UNKNOWN)
            w.WriteString("profile", par->profile.ToString(CultureInfo.InvariantCulture));
        if (ffmpeg.av_get_media_type_string(par->codec_type) is { } type)
            w.WriteString("codec_type", type);
        byte* fourcc = stackalloc byte[32];
        w.WriteString("codec_tag_string", Str(ffmpeg.av_fourcc_make_string(fourcc, par->codec_tag)));
        w.WriteString("codec_tag", "0x" + par->codec_tag.ToString("x4", CultureInfo.InvariantCulture));
        if (MimeCodec(par, st->avg_frame_rate) is { } mime)
            w.WriteString("mime_codec_string", mime);

        switch (par->codec_type)
        {
            case AVMediaType.AVMEDIA_TYPE_VIDEO:
                w.WriteNumber("width", par->width);
                w.WriteNumber("height", par->height);
                if (dec != null)
                {
                    w.WriteNumber("coded_width", dec->coded_width);
                    w.WriteNumber("coded_height", dec->coded_height);
                }

                w.WriteNumber("has_b_frames", par->video_delay);
                var sar = ffmpeg.av_guess_sample_aspect_ratio(fmt, st, null);
                if (sar.num != 0)
                {
                    w.WriteString("sample_aspect_ratio", $"{sar.num}:{sar.den}");
                    int dn, dd;
                    ffmpeg.av_reduce(&dn, &dd, (long)par->width * sar.num, (long)par->height * sar.den, 1024 * 1024);
                    w.WriteString("display_aspect_ratio", $"{dn}:{dd}");
                }

                if (ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format) is { } pix)
                    w.WriteString("pix_fmt", pix);
                w.WriteNumber("level", par->level);
                Named(w, "color_range", par->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? null : ffmpeg.av_color_range_name(par->color_range));
                Named(w, "color_space", par->color_space == AVColorSpace.AVCOL_SPC_UNSPECIFIED ? null : ffmpeg.av_color_space_name(par->color_space));
                Named(w, "color_transfer", par->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED ? null : ffmpeg.av_color_transfer_name(par->color_trc));
                Named(w, "color_primaries", par->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED ? null : ffmpeg.av_color_primaries_name(par->color_primaries));
                Named(w, "chroma_location", par->chroma_location == AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED ? null : ffmpeg.av_chroma_location_name(par->chroma_location));
                Named(w, "field_order", par->field_order switch
                {
                    AVFieldOrder.AV_FIELD_PROGRESSIVE => "progressive",
                    AVFieldOrder.AV_FIELD_TT => "tt",
                    AVFieldOrder.AV_FIELD_BB => "bb",
                    AVFieldOrder.AV_FIELD_TB => "tb",
                    AVFieldOrder.AV_FIELD_BT => "bt",
                    _ => null,
                });
                break;
            case AVMediaType.AVMEDIA_TYPE_AUDIO:
                Named(w, "sample_fmt", ffmpeg.av_get_sample_fmt_name((AVSampleFormat)par->format));
                w.WriteString("sample_rate", par->sample_rate.ToString(CultureInfo.InvariantCulture));
                w.WriteNumber("channels", par->ch_layout.nb_channels);
                if (par->ch_layout.order != AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                {
                    byte* layout = stackalloc byte[128];
                    ffmpeg.av_channel_layout_describe(&par->ch_layout, layout, 128);
                    w.WriteString("channel_layout", Str(layout));
                }

                w.WriteNumber("bits_per_sample", ffmpeg.av_get_bits_per_sample(par->codec_id));
                w.WriteNumber("initial_padding", par->initial_padding);
                break;
            case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                if (par->width != 0)
                    w.WriteNumber("width", par->width);
                if (par->height != 0)
                    w.WriteNumber("height", par->height);
                break;
        }

        // ffprobe's show_private_data (on by default): the decoder's and the demuxer's exported options.
        if (dec != null && dec->codec->priv_class != null)
            PrivateData(w, dec->priv_data);
        if (fmt->iformat->priv_class != null)
            PrivateData(w, fmt->priv_data);
        if ((fmt->iformat->flags & ffmpeg.AVFMT_SHOW_IDS) != 0)
            w.WriteString("id", "0x" + st->id.ToString("x", CultureInfo.InvariantCulture));
        w.WriteString("r_frame_rate", Q(st->r_frame_rate));
        w.WriteString("avg_frame_rate", Q(st->avg_frame_rate));
        w.WriteString("time_base", Q(st->time_base));
        if (st->start_time != ffmpeg.AV_NOPTS_VALUE)
        {
            w.WriteNumber("start_pts", st->start_time);
            w.WriteString("start_time", Seconds(st->start_time, st->time_base));
        }

        if (st->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            w.WriteNumber("duration_ts", st->duration);
            w.WriteString("duration", Seconds(st->duration, st->time_base));
        }

        if (par->bit_rate > 0)
            w.WriteString("bit_rate", par->bit_rate.ToString(CultureInfo.InvariantCulture));
        if (dec != null && dec->rc_max_rate > 0)
            w.WriteString("max_bit_rate", dec->rc_max_rate.ToString(CultureInfo.InvariantCulture));
        if (dec != null && dec->bits_per_raw_sample > 0)
            w.WriteString("bits_per_raw_sample", dec->bits_per_raw_sample.ToString(CultureInfo.InvariantCulture));
        if (st->nb_frames != 0)
            w.WriteString("nb_frames", st->nb_frames.ToString(CultureInfo.InvariantCulture));
        if (par->extradata_size > 0)
            w.WriteNumber("extradata_size", par->extradata_size);

        w.WriteStartObject("disposition");
        for (int bit = 0; bit < 32; bit++)
        {
            if (ffmpeg.av_disposition_to_string(1 << bit) is { } name)
                w.WriteNumber(name, (st->disposition & (1 << bit)) != 0 ? 1 : 0);
        }

        w.WriteEndObject();
        Tags(w, st->metadata);

        if (par->nb_coded_side_data > 0)
        {
            w.WriteStartArray("side_data_list");
            for (int i = 0; i < par->nb_coded_side_data; i++)
                SideData(w, &par->coded_side_data[i]);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    /// <summary>print_private_data: each exported option of the object, as av_opt_get gives it.</summary>
    static void PrivateData(Utf8JsonWriter w, void* obj)
    {
        AVOption* opt = null;
        while ((opt = ffmpeg.av_opt_next(obj, opt)) != null)
        {
            if ((opt->flags & ffmpeg.AV_OPT_FLAG_EXPORT) == 0)
                continue;
            byte* value;
            if (ffmpeg.av_opt_get(obj, Str(opt->name), 0, &value) >= 0)
            {
                w.WriteString(Str(opt->name), Str(value));
                ffmpeg.av_free(value);
            }
        }
    }

    static void Format(Utf8JsonWriter w, AVFormatContext* fmt)
    {
        long size = fmt->pb != null ? ffmpeg.avio_size(fmt->pb) : -1;
        w.WriteStartObject("format");
        w.WriteString("filename", Str(fmt->url));
        w.WriteNumber("nb_streams", fmt->nb_streams);
        w.WriteNumber("nb_programs", fmt->nb_programs);
        w.WriteNumber("nb_stream_groups", fmt->nb_stream_groups);
        w.WriteString("format_name", Str(fmt->iformat->name));
        w.WriteString("format_long_name", fmt->iformat->long_name != null ? Str(fmt->iformat->long_name) : "unknown");
        var us = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        if (fmt->start_time != ffmpeg.AV_NOPTS_VALUE)
            w.WriteString("start_time", Seconds(fmt->start_time, us));
        if (fmt->duration != ffmpeg.AV_NOPTS_VALUE)
            w.WriteString("duration", Seconds(fmt->duration, us));
        if (size >= 0)
            w.WriteString("size", size.ToString(CultureInfo.InvariantCulture));
        if (fmt->bit_rate > 0)
            w.WriteString("bit_rate", fmt->bit_rate.ToString(CultureInfo.InvariantCulture));
        w.WriteNumber("probe_score", fmt->probe_score);
        Tags(w, fmt->metadata);
        w.WriteEndObject();
    }

    static void Tags(Utf8JsonWriter w, AVDictionary* tags)
    {
        if (tags == null)
            return;
        w.WriteStartObject("tags");
        AVDictionaryEntry* e = null;
        while ((e = ffmpeg.av_dict_iterate(tags, e)) != null)
            w.WriteString(Str(e->key), Str(e->value));
        w.WriteEndObject();
    }

    /// <summary>
    /// A stream's side data as ffprobe prints it: its type's name and, for the kinds with plain fields (display matrix,
    /// skip samples, HDR mastering and light levels, Dolby Vision configuration, CPB properties, cropping, ...), those.
    /// Stereo 3D, spherical, ambient viewing and HDR10+ show their type only.
    /// </summary>
    static void SideData(Utf8JsonWriter w, AVPacketSideData* sd)
    {
        w.WriteStartObject();
        w.WriteString("side_data_type", SideDataName(sd->type));
        if (sd->type == AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX && (long)sd->size >= 9 * 4)
        {
            var m = (int*)sd->data;
            var text = new StringBuilder("\n");
            for (int row = 0; row < 3; row++)
            {
                text.Append(CultureInfo.InvariantCulture, $"{row:x8}: "); // "%08x: ", then " %11d" per value
                for (int col = 0; col < 3; col++)
                    text.Append(CultureInfo.InvariantCulture, $" {m[row * 3 + col],11}");
                text.Append('\n');
            }

            w.WriteString("displaymatrix", text.ToString());
            var matrix = default(int_array9);
            for (uint i = 0; i < 9; i++)
                matrix[i] = m[i];
            double rotation = ffmpeg.av_display_rotation_get(matrix);
            w.WriteNumber("rotation", double.IsNaN(rotation) ? 0 : (long)rotation);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_SKIP_SAMPLES && (long)sd->size == 10)
        {
            w.WriteNumber("skip_samples", BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(sd->data, 4)));
            w.WriteNumber("discard_padding", BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(sd->data + 4, 4)));
            w.WriteNumber("skip_reason", sd->data[8]);
            w.WriteNumber("discard_reason", sd->data[9]);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA)
        {
            var m = (AVMasteringDisplayMetadata*)sd->data;
            if (m->has_primaries != 0)
            {
                string[] names = ["red", "green", "blue"];
                for (uint c = 0; c < 3; c++)
                {
                    w.WriteString(names[c] + "_x", Q(m->display_primaries[c][0]));
                    w.WriteString(names[c] + "_y", Q(m->display_primaries[c][1]));
                }

                w.WriteString("white_point_x", Q(m->white_point[0]));
                w.WriteString("white_point_y", Q(m->white_point[1]));
            }

            if (m->has_luminance != 0)
            {
                w.WriteString("min_luminance", Q(m->min_luminance));
                w.WriteString("max_luminance", Q(m->max_luminance));
            }
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL)
        {
            var l = (AVContentLightMetadata*)sd->data;
            w.WriteNumber("max_content", l->MaxCLL);
            w.WriteNumber("max_average", l->MaxFALL);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF)
        {
            // AVDOVIDecoderConfigurationRecord: nine uint8_t fields (not in FFmpeg.AutoGen's bindings).
            byte* d = sd->data;
            w.WriteNumber("dv_version_major", d[0]);
            w.WriteNumber("dv_version_minor", d[1]);
            w.WriteNumber("dv_profile", d[2]);
            w.WriteNumber("dv_level", d[3]);
            w.WriteNumber("rpu_present_flag", d[4]);
            w.WriteNumber("el_present_flag", d[5]);
            w.WriteNumber("bl_present_flag", d[6]);
            w.WriteNumber("dv_bl_signal_compatibility_id", d[7]);
            w.WriteString("dv_md_compression", d[8] switch { 0 => "none", 1 => "limited", 2 => "reserved", 3 => "extended", _ => "unknown" });
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_AUDIO_SERVICE_TYPE)
        {
            w.WriteNumber("service_type", *(int*)sd->data);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_MPEGTS_STREAM_ID)
        {
            w.WriteNumber("id", *sd->data);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_CPB_PROPERTIES)
        {
            var c = (AVCPBProperties*)sd->data;
            w.WriteNumber("max_bitrate", c->max_bitrate);
            w.WriteNumber("min_bitrate", c->min_bitrate);
            w.WriteNumber("avg_bitrate", c->avg_bitrate);
            w.WriteNumber("buffer_size", c->buffer_size);
            w.WriteNumber("vbv_delay", unchecked((long)c->vbv_delay));
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_FRAME_CROPPING && (long)sd->size >= 16)
        {
            string[] names = ["crop_top", "crop_bottom", "crop_left", "crop_right"];
            for (int i = 0; i < 4; i++)
                w.WriteNumber(names[i], BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(sd->data + 4 * i, 4)));
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_AFD && (long)sd->size > 0)
        {
            w.WriteNumber("active_format", *sd->data);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_EXIF)
        {
            w.WriteNumber("size", (long)sd->size);
        }
        else if (sd->type == AVPacketSideDataType.AV_PKT_DATA_MATROSKA_BLOCKADDITIONAL && (long)sd->size >= 8)
        {
            w.WriteNumber("block_additional_id", unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(sd->data, 8))));
        }

        w.WriteEndObject();
    }

    /// <summary>av_packet_side_data_name (not in FFmpeg.AutoGen's bindings), FFmpeg 8.1's names.</summary>
    static string SideDataName(AVPacketSideDataType type) => type switch
    {
        AVPacketSideDataType.AV_PKT_DATA_PALETTE => "Palette",
        AVPacketSideDataType.AV_PKT_DATA_NEW_EXTRADATA => "New Extradata",
        AVPacketSideDataType.AV_PKT_DATA_PARAM_CHANGE => "Param Change",
        AVPacketSideDataType.AV_PKT_DATA_H263_MB_INFO => "H263 MB Info",
        AVPacketSideDataType.AV_PKT_DATA_REPLAYGAIN => "Replay Gain",
        AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX => "Display Matrix",
        AVPacketSideDataType.AV_PKT_DATA_STEREO3D => "Stereo 3D",
        AVPacketSideDataType.AV_PKT_DATA_AUDIO_SERVICE_TYPE => "Audio Service Type",
        AVPacketSideDataType.AV_PKT_DATA_QUALITY_STATS => "Quality stats",
        AVPacketSideDataType.AV_PKT_DATA_FALLBACK_TRACK => "Fallback track",
        AVPacketSideDataType.AV_PKT_DATA_CPB_PROPERTIES => "CPB properties",
        AVPacketSideDataType.AV_PKT_DATA_SKIP_SAMPLES => "Skip Samples",
        AVPacketSideDataType.AV_PKT_DATA_JP_DUALMONO => "JP Dual Mono",
        AVPacketSideDataType.AV_PKT_DATA_STRINGS_METADATA => "Strings Metadata",
        AVPacketSideDataType.AV_PKT_DATA_SUBTITLE_POSITION => "Subtitle Position",
        AVPacketSideDataType.AV_PKT_DATA_MATROSKA_BLOCKADDITIONAL => "Matroska BlockAdditional",
        AVPacketSideDataType.AV_PKT_DATA_WEBVTT_IDENTIFIER => "WebVTT ID",
        AVPacketSideDataType.AV_PKT_DATA_WEBVTT_SETTINGS => "WebVTT Settings",
        AVPacketSideDataType.AV_PKT_DATA_METADATA_UPDATE => "Metadata Update",
        AVPacketSideDataType.AV_PKT_DATA_MPEGTS_STREAM_ID => "MPEGTS Stream ID",
        AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA => "Mastering display metadata",
        AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL => "Content light level metadata",
        AVPacketSideDataType.AV_PKT_DATA_SPHERICAL => "Spherical Mapping",
        AVPacketSideDataType.AV_PKT_DATA_A53_CC => "A53 Closed Captions",
        AVPacketSideDataType.AV_PKT_DATA_ENCRYPTION_INIT_INFO => "Encryption initialization data",
        AVPacketSideDataType.AV_PKT_DATA_ENCRYPTION_INFO => "Encryption info",
        AVPacketSideDataType.AV_PKT_DATA_AFD => "Active Format Description data",
        AVPacketSideDataType.AV_PKT_DATA_PRFT => "Producer Reference Time",
        AVPacketSideDataType.AV_PKT_DATA_ICC_PROFILE => "ICC Profile",
        AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF => "DOVI configuration record",
        AVPacketSideDataType.AV_PKT_DATA_S12M_TIMECODE => "SMPTE ST 12-1:2014 timecode",
        AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS => "HDR10+ Dynamic Metadata (SMPTE 2094-40)",
        _ => "unknown",
    };

    /// <summary>av_mime_codec_str, as ffprobe prints mime_codec_string: null when FFmpeg has no string for the codec.</summary>
    static string? MimeCodec(AVCodecParameters* par, AVRational frameRate)
    {
        const int Size = 256;
        byte* text = stackalloc byte[Size];
        text[0] = 0;

        // av_bprint_init_for_buffer on AVBPrint's C layout (opaque in FFmpeg.AutoGen; 1024 bytes): str, len, size,
        // size_max. A fixed buffer, no reallocation (the string is short).
        byte* bp = stackalloc byte[1024];
        new Span<byte>(bp, 1024).Clear();
        *(byte**)bp = text;
        *(uint*)(bp + 8) = 0;
        *(uint*)(bp + 12) = Size;
        *(uint*)(bp + 16) = Size;
        return ffmpeg.av_mime_codec_str(par, frameRate, (AVBPrint*)bp) == 0 ? Str(text) : null;
    }

    static void Named(Utf8JsonWriter w, string key, string? value)
    {
        if (value is not null)
            w.WriteString(key, value);
    }

    static string Q(AVRational q) => string.Create(CultureInfo.InvariantCulture, $"{q.num}/{q.den}");

    /// <summary>ffprobe's times: av_q2d(time_base) * ts as a double, printed with C's "%f" (6 decimals, the exact value rounded half to even).</summary>
    static string Seconds(long ts, AVRational timeBase) => Fixed6(timeBase.num / (double)timeBase.den * ts);

    internal static string Fixed6(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            return d.ToString(CultureInfo.InvariantCulture);
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0)
            exp = 1;
        else
            mantissa |= 1L << 52;
        exp -= 1075;

        // value = mantissa * 2^exp exactly; scaled = value * 10^6 = num / den
        BigInteger num = mantissa * BigInteger.Pow(10, 6), den = BigInteger.One;
        if (exp >= 0)
            num <<= exp;
        else
            den <<= -exp;
        var q = BigInteger.DivRem(num, den, out var r);
        int cmp = (r * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        string digits = q.ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        string text = digits[..^6] + "." + digits[^6..];
        return negative ? "-" + text : text; // C prints -0.0 as "-0.000000" too
    }

    static string Str(byte* s) => Marshal.PtrToStringUTF8((nint)s) ?? "";
}
