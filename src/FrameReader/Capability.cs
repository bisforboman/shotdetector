namespace FrameReader;

/// <summary>
/// FrameReader's features, for <see cref="FFmpegLibraries.Missing"/>: what each needs from FFmpeg's libraries beyond
/// the decoders and demuxers of your input. Combine them with <c>|</c>.
/// </summary>
[Flags]
public enum Capability
{
    /// <summary>Frames from <see cref="VideoFrameReader"/> or <see cref="FrameDecoder"/> through a filter graph: <c>buffer</c>, <c>buffersink</c>.</summary>
    VideoFilters = 1,

    /// <summary>Deinterlacing (<see cref="FrameDecoderOptions.Deinterlace"/>, FFmpeg's <c>yadif</c>), with <see cref="VideoFilters"/>.</summary>
    Deinterlace = 1 << 1,

    /// <summary>A frame rate other than the stream's (<see cref="VideoFrameReaderOptions.FrameRate"/>, the <c>fps</c> filter), with <see cref="VideoFilters"/>.</summary>
    FrameRate = 1 << 2,

    /// <summary><see cref="FrameReader.AudioFilter"/>: <c>abuffer</c>, <c>abuffersink</c>, <c>aformat</c>, plus the graph's own filters (check those with <see cref="FFmpegLibraries.HasFilter"/>).</summary>
    AudioFilter = 1 << 3,

    /// <summary><see cref="FrameReader.SpectralStats"/>: <c>abuffer</c>, <c>abuffersink</c>, <c>aspectralstats</c>.</summary>
    SpectralStats = 1 << 4,

    /// <summary><see cref="AudioWriter"/> or <see cref="Audio.Convert"/> to MP3: the <c>libmp3lame</c> encoder and <c>mp3</c> muxer, and the writer's conversion filters.</summary>
    Mp3 = 1 << 5,

    /// <summary><see cref="AudioWriter"/> or <see cref="Audio.Convert"/> to AAC in .m4a: the <c>aac</c> encoder and <c>ipod</c> muxer, and the writer's conversion filters.</summary>
    Aac = 1 << 6,

    /// <summary><see cref="AudioWriter"/> or <see cref="Audio.Convert"/> to WAV: the <c>pcm_s16le</c> encoder and <c>wav</c> muxer, and the writer's conversion filters.</summary>
    Wav = 1 << 7,

    /// <summary><see cref="VideoWriter"/>'s H.264 in .mp4: the <c>libx264</c> encoder (not in ShotDetector.Native), the <c>mp4</c> muxer and its filters.</summary>
    H264 = 1 << 8,

    /// <summary><see cref="Remux.Copy"/> to DASH (an .mpd): the <c>dash</c> muxer.</summary>
    Dash = 1 << 9,
}
