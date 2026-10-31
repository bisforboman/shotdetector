using ShotDetector;

/// <summary>
/// The executable's scale filter has in_chroma_loc only from FFmpeg 7.1: an older ffmpeg (Ubuntu 24.04's 6.1) still reads
/// 8-bit 4:2:0, whose conversion doesn't interpolate chroma, and says what it needs for the formats it can't.
/// </summary>
public class FfmpegVersionTests
{
    [Theory]
    [InlineData("yuv420p")]
    [InlineData("yuv420p10le")]
    [InlineData("yuv422p")]
    public void ANewFfmpegIsToldTheSiting(string pixelFormat) =>
        Assert.Equal("left", VideoReader.ScaleChromaLocation("left", pixelFormat, "ffmpeg", ffmpegHasIt: true));

    [Theory]
    [InlineData("yuv420p")]
    [InlineData("yuvj420p")]
    public void AnOldFfmpegReads8Bit420WithoutIt(string pixelFormat) =>
        Assert.Null(VideoReader.ScaleChromaLocation("left", pixelFormat, "ffmpeg", ffmpegHasIt: false));

    [Theory]
    [InlineData("yuv420p10le")]
    [InlineData("yuv422p")]
    [InlineData("yuv444p")]
    public void AnOldFfmpegFailsWhereTheSitingMatters(string pixelFormat)
    {
        var e = Assert.Throws<ShotDetectionException>(() => VideoReader.ScaleChromaLocation("left", pixelFormat, "/opt/old/ffmpeg", ffmpegHasIt: false));
        Assert.Equal(ShotDetectionError.FfmpegNotFound, e.Reason);
        Assert.Contains("FFmpeg 7.1", e.Message, StringComparison.Ordinal);
        Assert.Contains(pixelFormat, e.Message, StringComparison.Ordinal);
        Assert.Contains("/opt/old/ffmpeg", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFilterHelpSaysWhichItIs(bool has)
    {
        // A stand-in ffmpeg printing the scale filter's help as 8.1 or 6.1 does (shell script: not on Windows).
        if (OperatingSystem.IsWindows())
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-fakeffmpeg-").FullName;
        try
        {
            string exe = Path.Combine(dir, "ffmpeg");
            string option = has ? "  in_chroma_loc     <int>        ..FV....... set input chroma sample location" : "";
            File.WriteAllText(exe, $"#!/bin/sh\necho 'Filter scale'\necho '  in_range          <int>        ..FV....... set input color range'\necho '{option}'\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(has, VideoReader.ScaleHasChromaLocation(exe, CancellationToken.None));
            Assert.Equal(has, VideoReader.ScaleHasChromaLocation(exe, CancellationToken.None)); // cached
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
