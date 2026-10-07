using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// In-process decoding gives the same per-frame stats, labels and shots as the ffmpeg executable. Needs FFmpeg 8.1's
/// shared libraries: SHOTDETECTOR_FFMPEG_LIBS names their folder (CI sets it); without it these tests do nothing.
/// </summary>
public class InProcessTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Cases => ["plain", "start", "end", "crop", "full", "scale", "fastyuv", "compat064", "dropped", "mpegps", "skip"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsTheFfmpegExecutable(string name)
    {
        if (Libs is null)
            return;
        string path = name switch { "dropped" => clips.ThreeShotsDropped, "mpegps" => clips.ThreeShotsMpegPs, _ => clips.ThreeShots };
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = name != "skip", FfmpegDirectory = Libs };
        options = name switch
        {
            "start" => options with { StartTime = "1.5" },
            "end" => options with { StartTime = "41", EndTime = "4.5" },
            "crop" => options with { Crop = (10, 20, 300, 200) },
            "full" => options with { FullFrames = true },
            "scale" => options with { Downscale = 1 },
            "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
            "compat064" => options with { Compatibility = PySceneDetectVersion.V0_6_4 },
            "skip" => options with { FrameSkip = 2 },
            _ => options,
        };
        var pipe = ShotDetection.Detect(path, options);
        var inProcess = ShotDetection.Detect(path, options with { Decoder = VideoDecoder.InProcess });
        Assert.True(inProcess.Video.DecodesInProcess);
        Assert.False(pipe.Video.DecodesInProcess);
        Assert.Equal(pipe.Stats?.Csv(pipe.Video.Position), inProcess.Stats?.Csv(inProcess.Video.Position));
        Assert.Equal(Shots.Csv(pipe.Shots), Shots.Csv(inProcess.Shots));
    }

    // Colour tags, ranges, chroma layouts and bit depths the converter handles differently (lossless ffv1 copies).
    [Theory]
    [InlineData("bt709", "-pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709")]
    [InlineData("fullrange", "-pix_fmt yuv420p -color_range pc")]
    [InlineData("yuvj", "-pix_fmt yuvj420p")]
    [InlineData("422", "-pix_fmt yuv422p")]
    [InlineData("444", "-pix_fmt yuv444p -colorspace bt709")]
    [InlineData("10bit", "-pix_fmt yuv420p10le")]
    [InlineData("oddheight", "-vf crop=320:237:0:0 -pix_fmt yuv420p")]
    public void SameAsTheFfmpegExecutableForEachFormat(string name, string args)
    {
        if (Libs is null)
            return;
        string copy = Path.Combine(Path.GetTempPath(), $"shotdetector-{name}-{Guid.NewGuid():N}.mkv");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, .. args.Split(' '), "-c:v", "ffv1", copy], default);
        try
        {
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs };
            var pipe = ShotDetection.Detect(copy, options);
            var inProcess = ShotDetection.Detect(copy, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodesInProcess);
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void RotatedVideoUsesTheExecutable()
    {
        if (Libs is null)
            return;
        var r = ShotDetection.Detect(clips.Rotated, new DetectionOptions { Decoder = VideoDecoder.InProcess, FfmpegDirectory = Libs });
        Assert.False(r.Video.DecodesInProcess);
    }
}
