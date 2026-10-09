using System.Globalization;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>How <see cref="SpectralStats"/> windows the audio: aspectralstats' own options.</summary>
public sealed record SpectralStatsOptions
{
    /// <summary>Samples per window, 32 to 65536 (aspectralstats' win_size; default 2048).</summary>
    public int WindowSize { get; init; } = 2048;

    /// <summary>The window function by FFmpeg's name: hann (default), hamming, blackman, rect, ... (win_func).</summary>
    public string WindowFunction { get; init; } = "hann";

    /// <summary>How much consecutive windows overlap, 0 to 1 (overlap; default 0.5: a result every half window).</summary>
    public double Overlap { get; init; } = 0.5;
}

/// <summary>One channel's spectral measures for a window, as aspectralstats computes and prints them.</summary>
public readonly record struct SpectralMeasures(
    double Mean, double Variance, double Centroid, double Spread, double Skewness, double Kurtosis, double Entropy,
    double Flatness, double Crest, double Flux, double Slope, double Decrease, double Rolloff);

/// <summary>A window's measures, valid until the next read from its <see cref="SpectralStats"/>.</summary>
public readonly ref struct SpectralWindow
{
    internal SpectralWindow(TimeSpan time, ReadOnlySpan<SpectralMeasures> channels)
    {
        Time = time;
        Channels = channels;
    }

    /// <summary>When the window's samples start, on the <see cref="AudioChunk.Time"/> timeline.</summary>
    public TimeSpan Time { get; }

    /// <summary>The measures per channel (channel 0 first).</summary>
    public ReadOnlySpan<SpectralMeasures> Channels { get; }
}

/// <summary>
/// Spectral statistics of an <see cref="AudioReader"/>'s samples (its rate and channels), window by window: FFmpeg's own
/// aspectralstats filter run over them, so the values are those <c>-af aspectralstats,ametadata=print</c> prints for the
/// same samples (6 significant digits: the filter hands them out as text). Centroid, spread and rolloff are in Hz.
/// </summary>
/// <remarks>Needs the aspectralstats, abuffer and abuffersink filters (the ShotDetector.Native libraries have them).</remarks>
public sealed unsafe class SpectralStats : IDisposable
{
    static readonly string[] Names = ["mean", "variance", "centroid", "spread", "skewness", "kurtosis", "entropy", "flatness", "crest", "flux", "slope", "decrease", "rolloff"];

    readonly AudioReader _audio;
    readonly int _channels, _rate;
    AVFilterGraph* _graph;
    AVFilterContext* _in, _out;
    AVFrame* _result;
    readonly SpectralMeasures[] _measures;
    readonly string[] _keys;
    long _fed;
    TimeSpan? _start;
    bool _flushed;

    /// <summary>Sets up the filter over <paramref name="audio"/>; reading reads it on.</summary>
    /// <param name="audio">The samples to measure; not disposed with this.</param>
    /// <param name="options">The windows; null: aspectralstats' defaults (2048 samples, hann, half overlap).</param>
    /// <exception cref="FrameReaderException">The libraries lack the filter, or it rejected the options.</exception>
    public SpectralStats(AudioReader audio, SpectralStatsOptions? options = null)
    {
        var o = options ?? new SpectralStatsOptions();
        (_audio, _channels, _rate) = (audio, audio.Channels, audio.SampleRate);
        _measures = new SpectralMeasures[_channels];
        _keys = [.. Enumerable.Range(1, _channels).SelectMany(c => Names.Select(n => $"lavfi.aspectralstats.{c}.{n}"))];
        if (ffmpeg.avfilter_get_by_name("aspectralstats") == null || ffmpeg.avfilter_get_by_name("abuffer") == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "SpectralStats needs FFmpeg's aspectralstats filter, which these libraries leave out.");
        try
        {
            _graph = ffmpeg.avfilter_graph_alloc();
            _result = ffmpeg.av_frame_alloc();
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, _channels);
            byte* name = stackalloc byte[64];
            ffmpeg.av_channel_layout_describe(&layout, name, 64);
            string args = $"time_base=1/{_rate}:sample_rate={_rate}:sample_fmt=fltp:channel_layout={new string((sbyte*)name)}";
            string stats = string.Create(CultureInfo.InvariantCulture,
                $"win_size={o.WindowSize}:win_func={o.WindowFunction}:overlap={o.Overlap:R}");
            AVFilterContext* source, filter, sink;
            Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "abuffer");
            Check(ffmpeg.avfilter_graph_create_filter(&filter, ffmpeg.avfilter_get_by_name("aspectralstats"), "stats", stats, null, _graph), "aspectralstats options");
            Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "abuffersink");
            Check(ffmpeg.avfilter_link(source, 0, filter, 0), "link");
            Check(ffmpeg.avfilter_link(filter, 0, sink, 0), "link");
            Check(ffmpeg.avfilter_graph_config(_graph, null), "filter graph");
            _in = source;
            _out = sink;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The next window's measures; false at the end of the audio.</summary>
    /// <param name="window">Its time and per-channel measures, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding or the filter failed.</exception>
    public bool TryRead(out SpectralWindow window)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(_result);
            int got = ffmpeg.av_buffersink_get_frame(_out, _result);
            if (got == 0)
            {
                for (int c = 0; c < _channels; c++)
                    _measures[c] = Measures(c);
                window = new SpectralWindow((_start ?? TimeSpan.Zero) + TimeSpan.FromSeconds(_result->pts / (double)_rate), _measures);
                return true;
            }
            if (got == ffmpeg.AVERROR_EOF || (got == ffmpeg.AVERROR(ffmpeg.EAGAIN) && _flushed))
                break;
            if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                Check(got, "aspectralstats");
            if (_audio.TryRead(out var chunk))
                Feed(chunk);
            else
            {
                Check(ffmpeg.av_buffersrc_add_frame_flags(_in, null, 0), "aspectralstats");
                _flushed = true;
            }
        }
        window = default;
        return false;
    }

    /// <summary>A chunk into the filter, deinterleaved (fltp, what aspectralstats takes, so no conversion in between).</summary>
    void Feed(AudioChunk chunk)
    {
        _start ??= chunk.Time;
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            frame->sample_rate = _rate;
            frame->nb_samples = chunk.Length;
            ffmpeg.av_channel_layout_default(&frame->ch_layout, _channels);
            frame->pts = _fed;
            Check(ffmpeg.av_frame_get_buffer(frame, 0), "frame buffer");
            var samples = chunk.Samples;
            for (int c = 0; c < _channels; c++)
            {
                float* plane = (float*)frame->extended_data[c];
                for (int i = 0; i < chunk.Length; i++)
                    plane[i] = samples[i * _channels + c];
            }
            _fed += chunk.Length;
            Check(ffmpeg.av_buffersrc_add_frame_flags(_in, frame, 0), "aspectralstats");
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    SpectralMeasures Measures(int channel)
    {
        Span<double> v = stackalloc double[Names.Length];
        for (int i = 0; i < Names.Length; i++)
        {
            var entry = ffmpeg.av_dict_get(_result->metadata, _keys[channel * Names.Length + i], null, 0);
            v[i] = entry == null ? double.NaN : Parse(System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)entry->value)!);
        }
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12]);
    }

    /// <summary>C's %g text: digits and exponents as .NET reads them, plus inf and nan.</summary>
    internal static double Parse(string s) => s.TrimStart('-') switch
    {
        "inf" => s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity,
        "nan" => double.NaN,
        _ => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    /// <summary>Frees the filter graph (not the <see cref="AudioReader"/>).</summary>
    public void Dispose()
    {
        if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; _in = _out = null; }
        if (_result != null) { var f = _result; ffmpeg.av_frame_free(&f); _result = null; }
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
