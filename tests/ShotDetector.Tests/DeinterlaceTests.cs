using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>Deinterlace gives what detection on a lossless <c>ffmpeg -vf yadif</c> copy gives (issue #16).</summary>
public class DeinterlaceTests(Clips clips) : IClassFixture<Clips>
{
    [Theory]
    [InlineData("plain")]
    [InlineData("start")]
    [InlineData("fastyuv")]
    public void SameAsALosslessYadifCopy(string name)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"shotdetector-yadif-{Guid.NewGuid():N}.mkv");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-vf", "yadif", "-c:v", "ffv1", copy], default);
        try
        {
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true };
            options = name switch
            {
                "start" => options with { StartTime = "2.5" },
                "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
                _ => options,
            };
            var expected = ShotDetection.Detect(copy, options);
            var actual = ShotDetection.Detect(clips.ThreeShots, options with { Deinterlace = true });
            var original = ShotDetection.Detect(clips.ThreeShots, options);
            Assert.Equal(expected.FrameCount, actual.FrameCount);
            Assert.Equal(expected.Stats!.Csv(expected.Video.Position), actual.Stats!.Csv(actual.Video.Position));
            Assert.Equal(Shots.Csv(expected.Shots), Shots.Csv(actual.Shots));
            // yadif changed the frames, so the option took effect.
            Assert.NotEqual(original.Stats!.Csv(original.Video.Position), actual.Stats!.Csv(actual.Video.Position));
        }
        finally
        {
            File.Delete(copy);
        }
    }
}
