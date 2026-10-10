using FrameReader;

namespace ShotDetector;

/// <summary>
/// FrameReader as ShotDetector uses it (docs/frame-reader-library.md): loading FFmpeg's libraries, and FrameReader's
/// failures as ShotDetector's reasons and messages.
/// </summary>
internal static class InProcess
{
    /// <summary>Whether the libraries load from <paramref name="directory"/> with libavfilter's yadif too.</summary>
    internal static bool CanDeinterlace(string? directory) => FFmpegLibraries.CanDeinterlace(directory);

    /// <summary>Whether FFmpeg 8's libraries load from <paramref name="directory"/>, or already have (<see cref="VideoDecoder.Auto"/>).</summary>
    internal static bool CanLoad(string? directory) => FFmpegLibraries.CanLoad(directory);

    /// <summary>Loads FFmpeg's shared libraries once per process (<see cref="FFmpegLibraries.Load"/>).</summary>
    internal static void Load(string? directory)
    {
        try
        {
            FFmpegLibraries.Load(directory);
        }
        catch (FrameReaderException e) when (e.Reason == FrameReaderError.LibrariesNotFound)
        {
            throw new ShotDetectionException(ShotDetectionError.FfmpegLibrariesNotFound,
                "In-process decoding needs FFmpeg 8's shared libraries (libavcodec 62, libavformat 62, libswscale 9, " +
                "libavutil 60): add the ShotDetector.Native.<rid> package for your platform, or install them (Alpine: " +
                "`apk add ffmpeg-libs`; Windows: a \"shared\" FFmpeg 8 build, with FfmpegDirectory set to its bin folder). " +
                $"Or decode with the ffmpeg executable (VideoDecoder.FfmpegProcess). ({e.InnerException?.Message})", e);
        }
    }


    /// <summary>A FrameReader failure as ShotDetector's (the same message; the libraries' with ShotDetector's advice).</summary>
    internal static ShotDetectionException Map(FrameReaderException e, string? libraryDirectory)
    {
        if (e.Reason == FrameReaderError.LibrariesNotFound)
            try { Load(libraryDirectory); } catch (ShotDetectionException mapped) { return mapped; }
        return new ShotDetectionException(e.Reason switch
        {
            FrameReaderError.InvalidInput => ShotDetectionError.InvalidInput,
            FrameReaderError.LibrariesNotFound => ShotDetectionError.FfmpegLibrariesNotFound,
            _ => ShotDetectionError.DecodeFailed,
        }, e.Message, e);
    }
}

/// <summary>ShotDetector's <see cref="IYadif"/> as FrameReader's row deinterlacer.</summary>
internal sealed class RowDeinterlacer(IYadif yadif) : IRowDeinterlacer
{
    public void FilterRow(nint dst, nint prev, nint cur, nint next, int srcStride, int dstStride, int width, int height, int y, int bytesPerSample) =>
        yadif.FilterRow(dst, prev, cur, next, srcStride, dstStride, width, height, y, bytesPerSample);
}
