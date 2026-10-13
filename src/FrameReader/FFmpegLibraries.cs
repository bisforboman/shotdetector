using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, libavutil 60; libavfilter 11 for
/// deinterlacing), loaded once per process through FFmpeg.AutoGen: from a given folder, or next to the app
/// (runtimes/&lt;rid&gt;/native/ in a build's output, the app's folder once published), then the system's usual places.
/// The one place to choose the folder: call <see cref="Load"/> with it before anything else; every reader and writer
/// loads them with the default search otherwise.
/// </summary>
public static unsafe class FFmpegLibraries
{
    static readonly object InitLock = new();
    static bool _loaded;
    static bool? _canDeinterlace;
    static readonly Dictionary<string, bool> Loadable = [];

    /// <summary>
    /// Loads the libraries, once per process: the first copy loaded is the one used, whatever folder is asked for
    /// later. Logging is turned off (failures surface as exceptions).
    /// </summary>
    /// <param name="directory">The only folder to load from; null: next to the app, then the system's places.</param>
    /// <exception cref="FrameReaderException">The libraries aren't there or aren't FFmpeg 8's
    /// (<see cref="FrameReaderError.LibrariesNotFound"/>).</exception>
    public static void Load(string? directory = null)
    {
        lock (InitLock)
        {
            if (_loaded)
                return;
            Exception? error = null;
            string[] places = directory is not null
                ? [directory]
                : [Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"), AppContext.BaseDirectory, ""];
            foreach (string dir in places)
            {
                if (dir.Length > 0 && !Directory.Exists(dir))
                    continue;
                try
                {
                    ffmpeg.RootPath = dir;
                    DynamicallyLoadedBindings.Initialize();
                    _ = ffmpeg.avcodec_version(); // fails here if the libraries aren't there or aren't FFmpeg 8's
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                    _loaded = true;
                    return;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or BadImageFormatException)
                {
                    error = e;
                }
            }

            throw new FrameReaderException(FrameReaderError.LibrariesNotFound,
                "In-process decoding needs FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, "
                + "libavutil 60): add the ShotDetector.Native.<rid> package for your platform, or install them (Alpine: "
                + "`apk add ffmpeg-libs`; Windows: a \"shared\" FFmpeg 8 build, with the folder set to its bin folder). "
                + $"({error?.Message})",
                error);
        }
    }

    /// <summary>Whether the libraries load (<see cref="Load"/>), or already have; tried once per folder.</summary>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    public static bool CanLoad(string? directory = null)
    {
        lock (InitLock)
        {
            if (_loaded)
                return true;
            string dir = directory ?? "";
            if (!Loadable.TryGetValue(dir, out bool ok))
            {
                try
                {
                    Load(directory);
                    ok = true;
                }
                catch (FrameReaderException)
                {
                    ok = false;
                }

                Loadable[dir] = ok;
            }

            return ok;
        }
    }

    /// <summary>Whether the libraries load and include libavfilter with yadif, for deinterlacing.</summary>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    public static bool CanDeinterlace(string? directory = null)
    {
        if (!CanLoad(directory))
            return false;
        lock (InitLock)
        {
            if (_canDeinterlace is null)
            {
                try
                {
                    _canDeinterlace = ffmpeg.avfilter_get_by_name("yadif") != null && ffmpeg.avfilter_get_by_name("buffer") != null;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
                {
                    _canDeinterlace = false;
                }
            }

            return _canDeinterlace.Value;
        }
    }

    /// <summary>
    /// Whether the libraries load and have this encoder (e.g. <c>libmp3lame</c>, <c>aac</c>, <c>libx264</c>): to check a
    /// system FFmpeg at startup rather than fail on the first write. What each class needs is in the README.
    /// </summary>
    /// <param name="name">The encoder's name, as <c>ffmpeg -encoders</c> lists it.</param>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    public static bool HasEncoder(string name, string? directory = null) => Has(directory, () => ffmpeg.avcodec_find_encoder_by_name(name) != null);

    /// <summary>Whether the libraries load and have this filter (e.g. <c>equalizer</c>, <c>format</c>), libavfilter included.</summary>
    /// <param name="name">The filter's name, as <c>ffmpeg -filters</c> lists it.</param>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    public static bool HasFilter(string name, string? directory = null) => Has(directory, () => ffmpeg.avfilter_get_by_name(name) != null);

    /// <summary>Whether the libraries load and have this muxer (e.g. <c>mp3</c>, <c>mp4</c>, <c>matroska</c>).</summary>
    /// <param name="name">The muxer's name, as <c>ffmpeg -muxers</c> lists it.</param>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    public static bool HasMuxer(string name, string? directory = null) => Has(directory, () => ffmpeg.av_guess_format(name, null, null) != null);

    /// <summary>
    /// What these libraries lack for the given features, as <c>"filter yadif"</c>, <c>"encoder libmp3lame"</c>,
    /// <c>"muxer dash"</c>; empty when they have everything. To check a system FFmpeg at startup without tracking what
    /// each feature uses. A filter graph of your own (an <see cref="AudioFilter"/>'s, Audio.Convert's): add
    /// <see cref="MissingFilters"/>.
    /// </summary>
    /// <param name="capabilities">The features, combined with <c>|</c>.</param>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    /// <returns>Everything missing, or <c>"libraries"</c> alone when they don't load.</returns>
    public static IReadOnlyList<string> Missing(Capability capabilities, string? directory = null)
    {
        if (!CanLoad(directory))
            return ["libraries"];
        var missing = new List<string>();
        foreach (var (capability, needs) in Needs)
        {
            if ((capabilities & capability) == 0)
                continue;
            foreach (string need in needs)
            {
                string[] kindName = need.Split(' ');
                bool has = kindName[0] switch
                {
                    "encoder" => HasEncoder(kindName[1], directory),
                    "muxer" => HasMuxer(kindName[1], directory),
                    _ => HasFilter(kindName[1], directory),
                };
                if (!has && !missing.Contains(need))
                    missing.Add(need);
            }
        }

        return missing;
    }

    /// <summary>
    /// The filters a filter graph of your own uses that these libraries lack (an <see cref="AudioFilter"/>'s, Audio.Convert's
    /// <c>Filter</c>, ...), as <c>"filter equalizer"</c>: <see cref="Missing"/>'s format, so the two lists concatenate.
    /// <c>"filter graph"</c> when the graph doesn't parse (a typo is caught at startup too); empty when they have
    /// everything. The graph is only parsed (avfilter_graph_segment_parse), nothing is created or run.
    /// </summary>
    /// <param name="filterGraph">The graph, as ffmpeg's <c>-af</c> or <c>-filter_complex</c> takes it.</param>
    /// <param name="directory">As for <see cref="Load"/>.</param>
    /// <returns>Every missing filter once, in the graph's order, or <c>"libraries"</c> alone when they don't load.</returns>
    public static IReadOnlyList<string> MissingFilters(string filterGraph, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(filterGraph);
        if (!CanLoad(directory))
            return ["libraries"];
        var missing = new List<string>();
        bool parsed = Has(directory, () =>
        {
            var graph = ffmpeg.avfilter_graph_alloc();
            AVFilterGraphSegment* segment = null;
            try
            {
                if (ffmpeg.avfilter_graph_segment_parse(graph, filterGraph, 0, &segment) < 0)
                    return false;
                for (ulong c = 0; c < (ulong)segment->nb_chains; c++)
                {
                    var chain = segment->chains[c];
                    for (ulong f = 0; f < (ulong)chain->nb_filters; f++)
                    {
                        string name = Marshal.PtrToStringUTF8((nint)chain->filters[f]->filter_name) ?? "";
                        if (ffmpeg.avfilter_get_by_name(name) == null && !missing.Contains("filter " + name))
                            missing.Add("filter " + name);
                    }
                }

                return true;
            }
            finally
            {
                ffmpeg.avfilter_graph_segment_free(&segment);
                ffmpeg.avfilter_graph_free(&graph);
            }
        });
        return parsed ? missing : ["filter graph"];
    }

    // What each feature creates (README's table): the filters of its graphs, its encoder and muxer.
    static readonly (Capability Capability, string[] Needs)[] Needs =
    [
        (Capability.VideoFilters, ["filter buffer", "filter buffersink"]),
        (Capability.Deinterlace, ["filter buffer", "filter buffersink", "filter yadif"]),
        (Capability.FrameRate, ["filter buffer", "filter buffersink", "filter fps"]),
        (Capability.AudioFilter, ["filter abuffer", "filter abuffersink", "filter aformat"]),
        (Capability.SpectralStats, ["filter abuffer", "filter abuffersink", "filter aspectralstats"]),
        (Capability.Mp3, ["encoder libmp3lame", "muxer mp3", "filter abuffer", "filter abuffersink", "filter aformat", "filter aresample"]),
        (Capability.Aac, ["encoder aac", "muxer ipod", "filter abuffer", "filter abuffersink", "filter aformat", "filter aresample"]),
        (Capability.Wav, ["encoder pcm_s16le", "muxer wav", "filter abuffer", "filter abuffersink", "filter aformat", "filter aresample"]),
        (Capability.H264, ["encoder libx264", "muxer mp4", "filter buffer", "filter buffersink", "filter format", "filter null", "filter scale"]),
        (Capability.Dash, ["muxer dash"]),
    ];

    static bool Has(string? directory, Func<bool> lookup)
    {
        if (!CanLoad(directory))
            return false;
        try
        {
            return lookup();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return false;
        } // no libavfilter
    }

    /// <summary>FFmpeg's message for an error code (av_strerror).</summary>
    /// <param name="error">A negative AVERROR code.</param>
    internal static string ErrorMessage(int error)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
