using FrameReader;

/// <summary>
/// Loading FFmpeg's libraries. SHOTDETECTOR_FFMPEG_LIBS names an FFmpeg 8.1 shared build's folder (CI sets it); without
/// it the tests that need the libraries do nothing. Loading is once per process, so the failure case runs first.
/// </summary>
public class FFmpegLibrariesTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    [Fact]
    public void LoadsOnceAndReportsWhatItHas()
    {
        // A folder without the libraries: a clear reason, unless another test loaded them already (once per process).
        string empty = Directory.CreateTempSubdirectory("framereader-").FullName;
        try
        {
            if (!FFmpegLibraries.CanLoad(empty))
            {
                var e = Assert.Throws<FrameReaderException>(() => FFmpegLibraries.Load(empty));
                Assert.Equal(FrameReaderError.LibrariesNotFound, e.Reason);
                Assert.Contains("FFmpeg 8", e.Message);
            }
        }
        finally
        {
            Directory.Delete(empty);
        }
        if (Libs is null)
            return;
        FFmpegLibraries.Load(Libs);
        Assert.True(FFmpegLibraries.CanLoad(Libs));
        Assert.True(FFmpegLibraries.CanLoad(null)); // loaded: any folder asked for later gets the same copy
        Assert.Equal("End of file", FFmpegLibraries.ErrorMessage(FFmpeg.AutoGen.ffmpeg.AVERROR_EOF));
    }
}
