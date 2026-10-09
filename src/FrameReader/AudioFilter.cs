using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// An FFmpeg audio filter graph over an <see cref="AudioReader"/>'s audio: the samples <c>ffmpeg -af GRAPH [-ar R -ac C]
/// -f f32le</c> gives, as interleaved float32 chunks. The graph gets the decoded audio as ffmpeg's does (the decoder's
/// own sample format, timestamps from the input's start or the seek point), so filters that work on integers compute
/// the same; the reader's <see cref="AudioReaderOptions.SampleRate"/> and <see cref="AudioReaderOptions.Channels"/>
/// convert after it, as -ar and -ac do. Time windows are ffmpeg's own: <c>enable='between(t,10,20)'</c> on a filter.
/// </summary>
/// <remarks>
/// Filters in the ShotDetector.Native libraries: volume, equalizer, bass, treble, highpass, lowpass, bandpass,
/// bandreject, afade, pan, acompressor, alimiter, dynaudnorm, agate, ebur128, loudnorm, silencedetect, astats (and
/// aformat, aresample). Any FFmpeg build in <see cref="AudioReaderOptions.LibraryDirectory"/> brings its own set.
/// Analysis filters' results (ebur128 with metadata=1, silencedetect, astats with metadata=1) are in
/// <see cref="Metadata"/>.
/// </remarks>
public sealed unsafe class AudioFilter : IDisposable
{
    readonly AudioReader _audio;
    readonly string _graphText;
    AVFilterGraph* _graph;
    AVFilterContext* _in, _out;
    AVFrame* _result, _feed;
    float[] _samples = [];
    readonly Dictionary<string, string> _metadata = [];
    bool _flushed;

    /// <summary>Sets up the filter over <paramref name="audio"/>; reading reads it on (from where it is: seek it first).</summary>
    /// <param name="audio">The audio to filter; not disposed with this.</param>
    /// <param name="graph">The filter graph as ffmpeg's -af takes it, e.g. <c>highpass=f=200,volume=0.5</c>.</param>
    /// <exception cref="ArgumentException">The graph is empty.</exception>
    public AudioFilter(AudioReader audio, string graph)
    {
        if (string.IsNullOrWhiteSpace(graph))
            throw new ArgumentException("The filter graph is empty.", nameof(graph));
        (_audio, _graphText) = (audio, graph);
    }

    /// <summary>
    /// The current chunk's frame metadata: what analysis filters attach (<c>lavfi.r128.I</c>, <c>lavfi.silence_start</c>,
    /// <c>lavfi.astats.Overall.RMS_level</c>, ...), as <c>ametadata=print</c> prints it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    /// <summary>The next filtered chunk; false at the end.</summary>
    /// <param name="chunk">The samples, float32 interleaved, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding failed, the graph doesn't parse, or a filter isn't in these libraries.</exception>
    public bool TryRead(out AudioChunk chunk)
    {
        while (true)
        {
            if (_out != null)
            {
                ffmpeg.av_frame_unref(_result);
                int got = ffmpeg.av_buffersink_get_frame(_out, _result);
                if (got == 0)
                {
                    chunk = Chunk();
                    return true;
                }
                if (got == ffmpeg.AVERROR_EOF)
                    break;
                if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(got, "filter");
            }
            if (_flushed)
                break;
            if (_audio.TryDecode(out int skip, out long position))
            {
                var frame = _audio.Decoded;
                if (skip >= frame->nb_samples)
                    continue;
                if (_graph == null)
                    Build(frame);
                Feed(frame, skip, position);
            }
            else
            {
                if (_graph == null)
                    break; // no audio at all
                Check(ffmpeg.av_buffersrc_add_frame_flags(_in, null, 0), "filter");
                _flushed = true;
            }
        }
        _metadata.Clear();
        chunk = default;
        return false;
    }

    /// <summary>
    /// abuffer (the decoded format, timestamps in samples) -> the graph -> aformat to interleaved float (and the reader's
    /// rate and channels when set) -> abuffersink.
    /// </summary>
    void Build(AVFrame* first)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        _result = ffmpeg.av_frame_alloc();
        _feed = ffmpeg.av_frame_alloc();
        byte* layout = stackalloc byte[128];
        // An unknown layout (a WAV without a channel mask) gets the default one for its channel count, as ffmpeg guesses.
        AVChannelLayout input;
        if (first->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            ffmpeg.av_channel_layout_default(&input, first->ch_layout.nb_channels);
        else
            Check(ffmpeg.av_channel_layout_copy(&input, &first->ch_layout), "channel layout");
        ffmpeg.av_channel_layout_describe(&input, layout, 128);
        ffmpeg.av_channel_layout_uninit(&input);
        string args = string.Create(CultureInfo.InvariantCulture,
            $"time_base=1/{first->sample_rate}:sample_rate={first->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name((AVSampleFormat)first->format)}:channel_layout={Marshal.PtrToStringAnsi((nint)layout)}");
        string format = "aformat=sample_fmts=flt";
        if (_audio.SampleRate != first->sample_rate)
            format += string.Create(CultureInfo.InvariantCulture, $":sample_rates={_audio.SampleRate}");
        if (_audio.Channels != first->ch_layout.nb_channels)
        {
            AVChannelLayout target;
            ffmpeg.av_channel_layout_default(&target, _audio.Channels);
            ffmpeg.av_channel_layout_describe(&target, layout, 128);
            format += $":channel_layouts={Marshal.PtrToStringAnsi((nint)layout)}";
        }
        AVFilterContext* source, sink;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "abuffer");
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "abuffersink");
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            int ret = ffmpeg.avfilter_graph_parse_ptr(_graph, $"{_graphText},{format}", &inputs, &outputs, null);
            if (ret < 0)
                throw new FrameReaderException(FrameReaderError.InvalidInput,
                    $"The filter graph \"{_graphText}\" doesn't parse, or uses a filter these libraries leave out: {FFmpegLibraries.ErrorMessage(ret)}");
            Check(ffmpeg.avfilter_graph_config(_graph, null), "filter graph");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
        _in = source;
        _out = sink;
    }

    /// <summary>A decoded frame into the graph, without its first <paramref name="skip"/> samples, at its position.</summary>
    void Feed(AVFrame* frame, int skip, long position)
    {
        ffmpeg.av_frame_unref(_feed);
        if (skip == 0)
            Check(ffmpeg.av_frame_ref(_feed, frame), "filter");
        else
        {
            _feed->format = frame->format;
            _feed->sample_rate = frame->sample_rate;
            _feed->nb_samples = frame->nb_samples - skip;
            Check(ffmpeg.av_channel_layout_copy(&_feed->ch_layout, &frame->ch_layout), "filter");
            Check(ffmpeg.av_frame_get_buffer(_feed, 0), "filter");
            Check(ffmpeg.av_samples_copy(_feed->extended_data, frame->extended_data, 0, skip, _feed->nb_samples,
                frame->ch_layout.nb_channels, (AVSampleFormat)frame->format), "filter");
        }
        _feed->pts = position;
        Check(ffmpeg.av_buffersrc_add_frame_flags(_in, _feed, 0), "filter");
    }

    AudioChunk Chunk()
    {
        int channels = _result->ch_layout.nb_channels, n = _result->nb_samples * channels;
        if (_samples.Length < n)
            _samples = new float[n];
        new ReadOnlySpan<float>(_result->data[0], n).CopyTo(_samples);
        _metadata.Clear();
        AVDictionaryEntry* e = null;
        while ((e = ffmpeg.av_dict_iterate(_result->metadata, e)) != null)
            _metadata[Marshal.PtrToStringAnsi((nint)e->key)!] = Marshal.PtrToStringAnsi((nint)e->value)!;
        var tb = ffmpeg.av_buffersink_get_time_base(_out);
        var time = _audio.Origin + TimeSpan.FromSeconds(_result->pts * tb.num / (double)tb.den);
        return new AudioChunk(_samples.AsSpan(0, n), channels, _result->sample_rate, time);
    }

    /// <summary>Frees the filter graph (not the <see cref="AudioReader"/>).</summary>
    public void Dispose()
    {
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; _in = _out = null; }
        if (_result != null) { var f = _result; ffmpeg.av_frame_free(&f); _result = null; }
        if (_feed != null) { var f = _feed; ffmpeg.av_frame_free(&f); _feed = null; }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
