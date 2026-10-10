using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using FrameReader;

namespace ShotDetector;

/// <summary>
/// What VideoReader asks ffprobe for, read with FFmpeg's libraries in this process instead (when they load): FrameReader's
/// <see cref="MediaProbe"/> reads the values, and this prints them as ffprobe does, field by field with its formatting,
/// so the parsing and everything after it stay as they are. Files and streams (a Stream's first bytes, as ffprobe reads
/// them from stdin); URLs keep ffprobe.
/// </summary>
internal static unsafe class InProcessProbe
{
    /// <summary>
    /// <c>ffprobe -select_streams v:0 -show_entries stream=...:stream_side_data=rotation:format=duration:packet=pts
    /// -of default=nw=1</c>'s output (the entries VideoReader reads). With <c>streamPrefix</c>, a Stream's first bytes are
    /// probed instead of the path (no packets).
    /// </summary>
    public static string Properties(string path, string? libraryDirectory, bool packets = true, string[]? inputOptions = null,
        byte[]? streamPrefix = null)
    {
        var options = new ProbeOptions { PacketTimestamps = packets && streamPrefix is null, InputOptions = Options(inputOptions) };
        var info = Probe(() => streamPrefix is null ? MediaProbe.Probe(path, options) : MediaProbe.Probe(new MemoryStream(streamPrefix), options), libraryDirectory);
        if (info.Video is not { } v)
            return ""; // no stream entries, as ffprobe prints none
        var sb = new StringBuilder();
        void Line(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
        Line("codec_name", v.Codec);
        // ffprobe's names for the field order.
        Line("field_order", v.FieldOrder switch
        {
            FrameReader.FieldOrder.Progressive => "progressive", FrameReader.FieldOrder.TopFirst => "tt", FrameReader.FieldOrder.BottomFirst => "bb",
            FrameReader.FieldOrder.TopCodedBottomFirst => "tb", FrameReader.FieldOrder.BottomCodedTopFirst => "bt", _ => "unknown",
        });
        Line("width", v.Width.ToString(CultureInfo.InvariantCulture));
        Line("height", v.Height.ToString(CultureInfo.InvariantCulture));
        Line("pix_fmt", v.PixelFormat ?? "unknown");
        Line("color_range", v.ColorRange ?? "unknown");
        Line("color_space", v.ColorSpace ?? "unknown");
        Line("r_frame_rate", v.FrameRate.ToString());
        Line("avg_frame_rate", v.AverageFrameRate.ToString());
        Line("time_base", v.TimeBase.ToString());
        Line("start_pts", Timestamp(v.StartPts));
        Line("start_time", Time(v.StartPts, v.TimeBase));
        Line("duration", Time(v.DurationPts, v.TimeBase));
        Line("nb_frames", v.FrameCount is { } n ? n.ToString(CultureInfo.InvariantCulture) : "N/A");
        if (v.Rotation is { } r)
            Line("rotation", ((long)r).ToString(CultureInfo.InvariantCulture)); // ffprobe's print_int: the double, truncated
        Line("format_name", info.Container);
        Line("duration", Time(info.Duration?.Ticks / 10, new Rational(1, 1_000_000)));
        foreach (var pts in info.PacketTimestamps ?? [])
            Line("pts", Timestamp(pts));
        return sb.ToString();
    }

    /// <summary>
    /// Every stream, as <see cref="VideoReader"/> reads them from ffprobe's <c>-show_entries</c> (VideoReader.StreamEntries):
    /// the same values, durations through ffprobe's six-decimal print.
    /// </summary>
    public static IReadOnlyList<StreamInfo> Streams(string path, string? libraryDirectory, string[]? inputOptions = null)
    {
        var info = Probe(() => MediaProbe.Probe(path, new ProbeOptions { InputOptions = Options(inputOptions) }), libraryDirectory);
        return info.Streams.Select(s => new StreamInfo
        {
            Index = s.Index,
            Type = (StreamType)(int)s.Type,
            Codec = s.Codec,
            CodecTag = s.CodecTag,
            Language = s.Language,
            Title = s.Title,
            IsDefault = s.IsDefault,
            IsForced = s.IsForced,
            BitRate = s.BitRate,
            Duration = VideoReader.Seconds(Time(s.DurationPts, s.TimeBase)),
            Width = s.Width,
            Height = s.Height,
            SampleRate = s.SampleRate,
            Channels = s.Channels,
            ChannelLayout = s.ChannelLayout,
            SampleFormat = s.SampleFormat,
        }).ToList();
    }

    /// <summary>
    /// <c>ffprobe -select_streams v:0 -show_entries frame=pts,pkt_dts</c> as VideoReader uses it (MPEG-PS, packets
    /// without pts): each decoded frame's pts when set and non-zero, else its packet's dts, in output order.
    /// </summary>
    public static List<long> FrameTimestamps(string path, string? libraryDirectory, string[]? inputOptions = null)
    {
        var result = new List<long>();
        try
        {
            InProcess.Load(libraryDirectory);
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions { InputOptions = Options(inputOptions) });
            while (decoder.Next())
                result.Add(decoder.FramePts is long p && p != 0 ? p : decoder.FramePacketDts ?? 0);
        }
        catch (FrameReaderException e)
        {
            throw InProcess.Map(e, libraryDirectory);
        }
        return result;
    }

    /// <summary>A probe through FrameReader, with ShotDetector's reasons and messages for its failures.</summary>
    static MediaInfo Probe(Func<MediaInfo> probe, string? libraryDirectory)
    {
        InProcess.Load(libraryDirectory); // ShotDetector's message when the libraries don't load
        try
        {
            return probe();
        }
        catch (FrameReaderException e) when (e.Reason == FrameReaderError.InvalidInput)
        {
            throw new ShotDetectionException(ShotDetectionError.InvalidInput, e.Message, e);
        }
    }

    /// <summary>ffmpeg's input options (-name value pairs) as FrameReader's demuxer options.</summary>
    internal static Dictionary<string, string>? Options(string[]? inputOptions)
    {
        if (inputOptions is not { Length: > 0 })
            return null;
        var result = new Dictionary<string, string>();
        for (int i = 0; i + 1 < inputOptions.Length; i += 2)
            result[inputOptions[i].TrimStart('-')] = inputOptions[i + 1];
        return result;
    }

    static string Timestamp(long? ts) => ts is { } t ? t.ToString(CultureInfo.InvariantCulture) : "N/A";

    /// <summary>ffprobe's print_time: the timestamp in seconds with printf's %f, or N/A.</summary>
    static string Time(long? ts, Rational tb) =>
        ts is { } t ? (t * (tb.Num / (double)tb.Den)).ToString("F6", CultureInfo.InvariantCulture) : "N/A";
}
