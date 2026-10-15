using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>Opening an input as ffprobe and ffmpeg do.</summary>
internal static unsafe class Demuxer
{
    /// <summary>
    /// After av_read_frame failed: the end of the input, unless reading it failed (a network reset, a Stream that threw:
    /// FFmpeg's I/O context records those), which is a <see cref="FrameReaderException"/> rather than a shortened
    /// result. A demuxer's own error (a truncated or damaged file) still ends the input, as in OpenCV.
    /// </summary>
    /// <param name="fmt">The input.</param>
    /// <param name="ret">What av_read_frame returned.</param>
    /// <param name="inner">What a Stream input threw, if it did.</param>
    internal static void ThrowIfReadFailed(AVFormatContext* fmt, int ret, Exception? inner)
    {
        if (ret == ffmpeg.AVERROR_EOF || fmt->pb == null || fmt->pb->error >= 0)
            return;
        throw new FrameReaderException(FrameReaderError.DecodeFailed,
            $"Reading the input failed part way: {inner?.Message ?? FFmpegLibraries.ErrorMessage(fmt->pb->error)}",
            inner);
    }

    /// <summary>
    /// Opens <paramref name="path"/> (or <paramref name="io"/>'s stream) as ffprobe and ffmpeg do (scan_all_pmts for
    /// MPEG-TS), with input options, and reads its stream info. Close with avformat_close_input (then dispose io).
    /// </summary>
    internal static AVFormatContext* Open(string path, IReadOnlyDictionary<string, string>? inputOptions = null, StreamInput? io = null)
    {
        AVFormatContext* fmt = null;
        if (io != null)
        {
            // Read from a Stream: avformat_close_input leaves the context to its owner.
            fmt = ffmpeg.avformat_alloc_context();
            fmt->pb = io.Context;
            fmt->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
        }

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
        foreach (var (name, value) in inputOptions ?? new Dictionary<string, string>())
            ffmpeg.av_dict_set(&options, name, value, 0);
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, &options);
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.InvalidInput, $"FFmpeg (open {path}): {FFmpegLibraries.ErrorMessage(ret)}");
        ret = ffmpeg.avformat_find_stream_info(fmt, null);
        if (ret < 0)
        {
            ffmpeg.avformat_close_input(&fmt);
            throw new FrameReaderException(FrameReaderError.InvalidInput, $"FFmpeg (read stream info): {FFmpegLibraries.ErrorMessage(ret)}");
        }

        return fmt;
    }

    /// <summary>The first video stream's index, as -select_streams v:0 / -map 0:v:0 take it; -1 if none.</summary>
    internal static int FirstVideoStream(AVFormatContext* fmt)
    {
        for (int i = 0; i < fmt->nb_streams; i++)
        {
            if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Where ffmpeg's <c>-ss</c> seeks for <paramref name="timestamp"/> (AV_TIME_BASE, start time included): 3/23 s
    /// earlier for formats that seek by dts when any stream (video, even when reading audio) has B-frames.
    /// </summary>
    internal static long SeekPoint(AVFormatContext* fmt, long timestamp)
    {
        if ((fmt->iformat->flags & ffmpeg.AVFMT_SEEK_TO_PTS) == 0)
        {
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->video_delay > 0)
                    return timestamp - 3 * ffmpeg.AV_TIME_BASE / 23;
            }
        }

        return timestamp;
    }
}
