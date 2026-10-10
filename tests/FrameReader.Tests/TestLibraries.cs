using FrameReader;

/// <summary>
/// SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg; CI sets it), loaded the one way
/// FrameReader takes a folder: <see cref="FFmpegLibraries.Load"/>, before a test class uses the libraries.
/// </summary>
internal static class TestLibraries
{
    internal static string? Load()
    {
        string? dir = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (dir is not null)
            FFmpegLibraries.Load(dir);
        return dir;
    }
}
