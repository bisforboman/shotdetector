using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>Which of an input's streams <see cref="Remux.Copy"/> copies.</summary>
[Flags]
public enum StreamSelection
{
    /// <summary>Every video stream (ffmpeg's <c>-map 0:v</c>).</summary>
    Video = 1,

    /// <summary>Every audio stream (<c>-map 0:a</c>).</summary>
    Audio = 2,

    /// <summary>Every subtitle stream (<c>-map 0:s</c>).</summary>
    Subtitle = 4,

    /// <summary>Every stream, data and attachments included (<c>-map 0</c>).</summary>
    All = 0x7fffffff,
}

/// <summary>What <see cref="Remux.Copy"/> copies and how it writes.</summary>
public sealed record RemuxOptions
{
    /// <summary>Which streams, by type; ignored when <see cref="StreamIndices"/> is set. Default: all.</summary>
    public StreamSelection Streams { get; init; } = StreamSelection.All;

    /// <summary>These streams, in this order (<c>-map 0:i</c> each); null: by <see cref="Streams"/>.</summary>
    public IReadOnlyList<int>? StreamIndices { get; init; }

    /// <summary>FFmpeg's muxer name (<c>mp4</c>, <c>ipod</c>, <c>matroska</c>, <c>mp3</c>, ...); null: from the output's name.</summary>
    public string? Container { get; init; }

    /// <summary>Leave out what makes a file differ between FFmpeg versions, as ffmpeg's <c>-fflags +bitexact</c>.</summary>
    public bool Bitexact { get; init; }

    /// <summary>FFmpeg demuxer options, as ffmpeg's input options without the dash.</summary>
    public IReadOnlyDictionary<string, string>? InputOptions { get; init; }
}

/// <summary>
/// Copies streams from one container into another without re-encoding, in this process: the file
/// <c>ffmpeg -i IN -map ... -c copy OUT</c> writes. For example the video alone into an .mp4 and the audio into an .m4a.
/// </summary>
public static unsafe class Remux
{
    /// <summary>Copies the selected streams of <paramref name="input"/> into <paramref name="output"/> (created or overwritten).</summary>
    /// <param name="input">A file or URL FFmpeg can read.</param>
    /// <param name="output">The file to write; its name picks the container unless <see cref="RemuxOptions.Container"/> does.</param>
    /// <param name="options">Streams and format; null: every stream, the container from the name.</param>
    /// <exception cref="FrameReaderException">The input can't be read, nothing is selected, or the container can't hold a
    /// selected stream (<see cref="FrameReaderError.InvalidInput"/>), or writing failed.</exception>
    public static void Copy(string input, string output, RemuxOptions? options = null)
    {
        var o = options ?? new RemuxOptions();
        FFmpegLibraries.Load();
        AVFormatContext* ic = Demuxer.Open(input, o.InputOptions);
        AVFormatContext* oc = null;
        AVPacket* pkt = null;
        try
        {
            var map = Select(ic, o); // output stream i comes from input stream map[i]
            if (map.Count == 0)
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"\"{input}\" has no stream of the kinds asked for.");
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, o.Container, output);
            if (ret < 0 || oc == null)
                throw new FrameReaderException(FrameReaderError.InvalidInput,
                    $"No muxer for \"{output}\"{(o.Container is null ? "" : $" ({o.Container})")} in these FFmpeg libraries: {FFmpegLibraries.ErrorMessage(ret)}");
            if (o.Bitexact)
                oc->flags |= ffmpeg.AVFMT_FLAG_BITEXACT;
            oc->max_delay = 700000; // ffmpeg's -muxdelay default, 0.7 s

            var outOf = new int[ic->nb_streams]; // input index -> output index, -1 when not copied
            Array.Fill(outOf, -1);
            for (int i = 0; i < map.Count; i++)
            {
                outOf[map[i]] = i;
                AddStream(oc, ic->streams[map[i]]);
            }
            CopyMetadataAndChapters(ic, oc);

            if ((oc->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Check(ffmpeg.avio_open(&oc->pb, output, ffmpeg.AVIO_FLAG_WRITE), $"create \"{output}\"");
            ret = ffmpeg.avformat_write_header(oc, null);
            if (ret < 0)
                throw new FrameReaderException(FrameReaderError.InvalidInput,
                    $"The {new string((sbyte*)oc->oformat->name)} container can't take these streams: {FFmpegLibraries.ErrorMessage(ret)}");

            // ffmpeg shifts every timestamp by the input's start time (no -copyts), drops each stream's packets before
            // its first keyframe, and fixes up what the muxer would reject.
            long tsOffset = ic->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : -ic->start_time;
            var started = new bool[map.Count];
            var lastDts = new long[map.Count];
            Array.Fill(lastDts, ffmpeg.AV_NOPTS_VALUE);
            var rescaleLast = new long[map.Count]; // av_rescale_delta's state, from 0 as in ffmpeg
            pkt = ffmpeg.av_packet_alloc();
            while ((ret = ffmpeg.av_read_frame(ic, pkt)) >= 0)
            {
                int to = pkt->stream_index < outOf.Length ? outOf[pkt->stream_index] : -1;
                if (to < 0 || (!started[to] && (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0))
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }
                started[to] = true;
                Write(ic, oc, pkt, to, tsOffset, ref lastDts[to], ref rescaleLast[to]);
            }
            if (ret != ffmpeg.AVERROR_EOF)
                Check(ret, "read");
            Check(ffmpeg.av_write_trailer(oc), "write the trailer");
        }
        finally
        {
            if (pkt != null)
                ffmpeg.av_packet_free(&pkt);
            if (oc != null)
            {
                if ((oc->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && oc->pb != null)
                    ffmpeg.avio_closep(&oc->pb);
                ffmpeg.avformat_free_context(oc);
            }
            ffmpeg.avformat_close_input(&ic);
        }
    }

    static List<int> Select(AVFormatContext* ic, RemuxOptions o)
    {
        if (o.StreamIndices is { } indices)
        {
            foreach (int i in indices)
                if (i < 0 || i >= ic->nb_streams)
                    throw new FrameReaderException(FrameReaderError.InvalidInput, $"No stream {i}: the input has {ic->nb_streams}.");
            return [.. indices];
        }
        var map = new List<int>();
        for (int i = 0; i < ic->nb_streams; i++)
        {
            var kind = ic->streams[i]->codecpar->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => StreamSelection.Video,
                AVMediaType.AVMEDIA_TYPE_AUDIO => StreamSelection.Audio,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => StreamSelection.Subtitle,
                _ => (StreamSelection)0,
            };
            if (o.Streams == StreamSelection.All || (o.Streams & kind) != 0)
                map.Add(i);
        }
        return map;
    }

    /// <summary>An output stream as ffmpeg's stream copy sets it up (fftools' streamcopy_init).</summary>
    static void AddStream(AVFormatContext* oc, AVStream* ist)
    {
        var st = ffmpeg.avformat_new_stream(oc, null);
        var par = st->codecpar;
        // Through a codec context and back, as ffmpeg does.
        var ctx = ffmpeg.avcodec_alloc_context3(null);
        try
        {
            Check(ffmpeg.avcodec_parameters_to_context(ctx, ist->codecpar), "stream parameters");
            Check(ffmpeg.avcodec_parameters_from_context(par, ctx), "stream parameters");
        }
        finally
        {
            ffmpeg.avcodec_free_context(&ctx);
        }
        // Keep the input's codec tag only where the container would map it to the same codec (or has no tag for it).
        uint tag = 0;
        var tags = oc->oformat->codec_tag;
        uint unused;
        if (tags == null || ffmpeg.av_codec_get_id(tags, par->codec_tag) == par->codec_id || ffmpeg.av_codec_get_tag2(tags, par->codec_id, &unused) == 0)
            tag = par->codec_tag;
        par->codec_tag = tag;

        st->avg_frame_rate = ist->avg_frame_rate;
        st->time_base = ffmpeg.av_add_q(ist->time_base, new AVRational { num = 0, den = 1 }); // common factors removed
        switch (par->codec_type)
        {
            case AVMediaType.AVMEDIA_TYPE_AUDIO:
                if (par->codec_id == AVCodecID.AV_CODEC_ID_MP3 && par->block_align is 1 or 1152 or 576)
                    par->block_align = 0;
                if (par->codec_id == AVCodecID.AV_CODEC_ID_AC3)
                    par->block_align = 0;
                break;
            case AVMediaType.AVMEDIA_TYPE_VIDEO:
                var sar = ist->sample_aspect_ratio.num != 0 ? ist->sample_aspect_ratio : par->sample_aspect_ratio;
                st->sample_aspect_ratio = par->sample_aspect_ratio = sar;
                st->r_frame_rate = ist->r_frame_rate;
                break;
        }
        st->disposition = ist->disposition;
        ffmpeg.av_dict_copy(&st->metadata, ist->metadata, ffmpeg.AV_DICT_DONT_OVERWRITE);
        if (ist->duration > 0) // a hint to the muxer, as ffmpeg gives it
            st->duration = ffmpeg.av_rescale_q(ist->duration, ist->time_base, st->time_base);
    }

    /// <summary>The input's global metadata (less what ffmpeg drops) and its chapters, as ffmpeg copies them by default.</summary>
    static void CopyMetadataAndChapters(AVFormatContext* ic, AVFormatContext* oc)
    {
        long tsOffset = ic->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : -ic->start_time;
        if (ic->nb_chapters > 0)
        {
            oc->chapters = (AVChapter**)ffmpeg.av_calloc(ic->nb_chapters, (ulong)sizeof(AVChapter*));
            for (int i = 0; i < ic->nb_chapters; i++)
            {
                var c = ic->chapters[i];
                long off = ffmpeg.av_rescale_q(-tsOffset, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, c->time_base);
                if (c->end < off)
                    continue;
                var oc2 = (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
                oc2->id = c->id;
                oc2->time_base = c->time_base;
                oc2->start = Math.Max(0, c->start - off);
                oc2->end = c->end - off;
                ffmpeg.av_dict_copy(&oc2->metadata, c->metadata, 0);
                oc->chapters[oc->nb_chapters++] = oc2;
            }
        }
        ffmpeg.av_dict_copy(&oc->metadata, ic->metadata, ffmpeg.AV_DICT_DONT_OVERWRITE);
        foreach (var key in new[] { "creation_time", "company_name", "product_name", "product_version" })
            ffmpeg.av_dict_set(&oc->metadata, key, null, 0);
    }

    /// <summary>One packet to the muxer with ffmpeg's timestamp handling (the demuxer's offset, then fftools' mux fix-ups).</summary>
    static void Write(AVFormatContext* ic, AVFormatContext* oc, AVPacket* pkt, int to, long tsOffset, ref long lastDts, ref long rescaleLast)
    {
        var ist = ic->streams[pkt->stream_index];
        var st = oc->streams[to];
        var tb = ist->time_base;
        var usec = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        if (pkt->dts != ffmpeg.AV_NOPTS_VALUE)
            pkt->dts += ffmpeg.av_rescale_q(tsOffset, usec, tb);
        if (pkt->pts != ffmpeg.AV_NOPTS_VALUE)
            pkt->pts += ffmpeg.av_rescale_q(tsOffset, usec, tb);
        bool audio = st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO;
        if (audio && pkt->dts != ffmpeg.AV_NOPTS_VALUE)
            pkt->pts = pkt->dts;

        if (audio)
        {
            // Audio is rescaled with av_rescale_delta, keeping accuracy with coarse input time bases.
            int duration = ffmpeg.av_get_audio_frame_duration2(st->codecpar, pkt->size);
            if (duration == 0)
                duration = st->codecpar->frame_size;
            long last = rescaleLast;
            pkt->dts = ffmpeg.av_rescale_delta(tb, pkt->dts, new AVRational { num = 1, den = st->codecpar->sample_rate }, duration, &last, st->time_base);
            rescaleLast = last;
            pkt->pts = pkt->dts;
            pkt->duration = ffmpeg.av_rescale_q(pkt->duration, tb, st->time_base);
        }
        else
            ffmpeg.av_packet_rescale_ts(pkt, tb, st->time_base);

        if ((oc->oformat->flags & ffmpeg.AVFMT_NOTIMESTAMPS) == 0)
        {
            if (pkt->dts != ffmpeg.AV_NOPTS_VALUE && pkt->pts != ffmpeg.AV_NOPTS_VALUE && pkt->dts > pkt->pts)
            {
                long a = pkt->pts, b = pkt->dts, c = lastDts + 1;
                pkt->pts = pkt->dts = a + b + c - Math.Min(a, Math.Min(b, c)) - Math.Max(a, Math.Max(b, c));
            }
            var type = st->codecpar->codec_type;
            if (type is AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_SUBTITLE
                && pkt->dts != ffmpeg.AV_NOPTS_VALUE && lastDts != ffmpeg.AV_NOPTS_VALUE)
            {
                long max = lastDts + ((oc->oformat->flags & ffmpeg.AVFMT_TS_NONSTRICT) == 0 ? 1 : 0);
                if (pkt->dts < max)
                {
                    if (pkt->pts >= pkt->dts)
                        pkt->pts = Math.Max(pkt->pts, max);
                    pkt->dts = max;
                }
            }
        }
        lastDts = pkt->dts;
        pkt->stream_index = to;
        Check(ffmpeg.av_interleaved_write_frame(oc, pkt), "write");
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
