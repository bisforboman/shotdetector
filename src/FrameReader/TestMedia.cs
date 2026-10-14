using System.Globalization;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>What <see cref="TestMedia.Write"/> makes.</summary>
public sealed record TestMediaOptions
{
    /// <summary>
    /// Video sources in FFmpeg's filter syntax, concatenated in this order (hard cuts at known frames), each at
    /// <see cref="Size"/> and <see cref="FrameRate"/>: <c>testsrc2=d=4</c>, <c>smptebars=d=3</c>, <c>color=c=red:d=1</c>,
    /// or a chain such as <c>mandelbrot,trim=duration=2</c> for a source without a duration. Each must end. Empty: no
    /// video.
    /// </summary>
    public IReadOnlyList<string> Video { get; init; } = [];

    /// <summary>The video's size in pixels.</summary>
    public (int Width, int Height) Size { get; init; } = (320, 240);

    /// <summary>The video's frame rate.</summary>
    public Rational FrameRate { get; init; } = new(25, 1);

    /// <summary>
    /// Progressive (the default), or interlaced with this field order: the frames are flagged as such and the encoder
    /// codes them interlaced (mpeg2video and mpeg4: interlaced motion estimation and DCT, as ffmpeg's <c>+ilme+ildct</c>).
    /// </summary>
    public FieldOrder FieldOrder { get; init; } = FieldOrder.Progressive;

    /// <summary>An audio source in FFmpeg's filter syntax (<c>sine=frequency=1000:duration=12</c>); it must end. Null: no audio.</summary>
    public string? Audio { get; init; }

    /// <summary>The video encoder (<c>mpeg2video</c>, <c>mpeg4</c>, ...); null: the container's usual one.</summary>
    public string? VideoEncoder { get; init; }

    /// <summary>The audio encoder (<c>aac</c>, <c>pcm_s16le</c>, ...); null: the container's usual one.</summary>
    public string? AudioEncoder { get; init; }
}

/// <summary>
/// Writes synthetic test media from FFmpeg's own sources (test patterns, colour bars, tones) and encoders, in-process: for
/// tests that need real media without committing any and without the ffmpeg executable (issue #237). The same as the
/// ffmpeg command line with <c>-f lavfi</c> inputs, a concat and these encoders, not byte for byte. Needs the source
/// filters and encoders it uses (the FrameReader.Native libraries have testsrc2, smptebars, mandelbrot, color, sine,
/// anullsrc, concat, trim; mpeg2video, mpeg4, aac and PCM): <see cref="FFmpegLibraries.MissingFilters"/> checks a source.
/// </summary>
public static unsafe class TestMedia
{
    /// <summary>Writes <paramref name="path"/> (its extension picks the container: .ts, .mkv, .mp4, .avi, .wav, ...).</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="options">The sources and encoders.</param>
    /// <exception cref="FrameReaderException">InvalidInput: a source that doesn't parse or a filter, encoder or muxer the libraries lack.</exception>
    public static void Write(string path, TestMediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Video.Count == 0 && options.Audio is null)
            throw new ArgumentException("TestMedia needs a video or an audio source.", nameof(options));
        FFmpegLibraries.Load();

        var o = options;
        string rate = string.Create(CultureInfo.InvariantCulture, $"{o.FrameRate.Num}/{o.FrameRate.Den}");
        string video = o.Video.Count == 0 ? "" : string.Join(";", o.Video.Select((source, i) =>
                string.Create(CultureInfo.InvariantCulture,
                    $"{Sized(source, o.Size, rate)},setsar=1,format=yuv420p[v{i}]")))
            + $";{string.Concat(o.Video.Select((_, i) => $"[v{i}]"))}concat=n={o.Video.Count}:v=1:a=0"
            + o.FieldOrder switch
            {
                FieldOrder.TopFirst or FieldOrder.TopCodedBottomFirst => ",setfield=tff",
                FieldOrder.BottomFirst or FieldOrder.BottomCodedTopFirst => ",setfield=bff",
                _ => "",
            };

        var fmt = Output.Create(path, null, bitexact: true);
        Source? v = null, a = null;
        Encoder? ve = null, ae = null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            if (video.Length > 0)
            {
                v = new Source(video, "buffersink");
                var codec = Encoder.Find(fmt, path, o.VideoEncoder, AVMediaType.AVMEDIA_TYPE_VIDEO, "video");
                var encoderOptions = o.FieldOrder is FieldOrder.Progressive or FieldOrder.Unknown
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["flags"] = "+ilme+ildct" };
                ve = Encoder.Video(fmt,
                    codec,
                    AVPixelFormat.AV_PIX_FMT_YUV420P,
                    o.Size.Width,
                    o.Size.Height,
                    o.FrameRate,
                    null,
                    null,
                    null,
                    null,
                    encoderOptions,
                    bitexact: true);
            }

            // The audio encoder opens from its first frame (the source's format).
            AVFrame* firstAudio = null;
            if (o.Audio is not null)
            {
                a = new Source(o.Audio, "abuffersink");
                firstAudio = ffmpeg.av_frame_alloc();
                if (!a.Next(firstAudio))
                    throw new FrameReaderException(FrameReaderError.InvalidInput, $"The audio source \"{o.Audio}\" gave no samples.");
                ae = Encoder.AudioFrom(fmt,
                    Encoder.Find(fmt, path, o.AudioEncoder, AVMediaType.AVMEDIA_TYPE_AUDIO, "audio"),
                    firstAudio,
                    null,
                    null,
                    bitexact: true);
            }

            ve?.EnsureOpen();
            Output.Start(fmt, path);

            // Video first, then audio: the muxer interleaves (small clips; it buffers what it must).
            for (long n = 0; v is not null && v.Next(frame); n++)
            {
                frame->pts = n; // in 1/rate, the encoder graph's time base
                ve!.Send(frame);
                ffmpeg.av_frame_unref(frame);
            }

            ve?.Send(null);
            if (ae is not null)
            {
                ae.Send(firstAudio);
                ffmpeg.av_frame_free(&firstAudio);
                while (a!.Next(frame))
                {
                    ae.Send(frame);
                    ffmpeg.av_frame_unref(frame);
                }

                ae.Send(null);
            }

            Encoder.Check(ffmpeg.av_write_trailer(fmt), "write the trailer");
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ve?.Dispose();
            ae?.Dispose();
            v?.Dispose();
            a?.Dispose();
            Output.Free(ref fmt);
        }
    }

    /// <summary>A video source at the size and rate asked for (its own <c>s</c>/<c>r</c> options; a chain gets them on its first filter).</summary>
    static string Sized(string source, (int Width, int Height) size, string rate)
    {
        int end = source.IndexOf(',');
        string first = end < 0 ? source : source[..end], rest = end < 0 ? "" : source[end..];
        string extra = string.Create(CultureInfo.InvariantCulture, $"s={size.Width}x{size.Height}:r={rate}");
        return first + (first.Contains('=') ? ":" : "=") + extra + rest;
    }

    /// <summary>A filter graph with no input: its frames, one at a time, until the sources end.</summary>
    sealed class Source : IDisposable
    {
        AVFilterGraph* _graph = ffmpeg.avfilter_graph_alloc();
        readonly AVFilterContext* _sink;

        internal Source(string graph, string sink)
        {
            AVFilterContext* snk;
            Encoder.Check(ffmpeg.avfilter_graph_create_filter(&snk, ffmpeg.avfilter_get_by_name(sink), "out", null, null, _graph), sink);
            _sink = snk;
            var inputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* outputs = null;
            try
            {
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = snk;
                int ret = ffmpeg.avfilter_graph_parse_ptr(_graph, graph, &inputs, &outputs, null);
                if (ret >= 0)
                    ret = ffmpeg.avfilter_graph_config(_graph, null);
                if (ret < 0)
                {
                    throw new FrameReaderException(FrameReaderError.InvalidInput,
                        $"The source \"{graph}\" doesn't parse, or uses a filter these libraries leave out: {FFmpegLibraries.ErrorMessage(ret)}");
                }
            }
            finally
            {
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
            }
        }

        /// <summary>The next frame into <paramref name="frame"/>; false at the end.</summary>
        internal bool Next(AVFrame* frame)
        {
            int ret = ffmpeg.av_buffersink_get_frame(_sink, frame);
            if (ret == ffmpeg.AVERROR_EOF)
                return false;
            Encoder.Check(ret, "source");
            return true;
        }

        public void Dispose()
        {
            if (_graph != null)
            {
                var g = _graph;
                ffmpeg.avfilter_graph_free(&g);
                _graph = null;
            }
        }
    }
}
