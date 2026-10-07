using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>Deinterlace gives what detection on a lossless <c>ffmpeg -vf yadif</c> copy gives (issue #16).</summary>
public class DeinterlaceTests(Clips clips) : IClassFixture<Clips>
{
    [Fact]
    public void ProbeReportsTheFieldOrder()
    {
        var interlaced = ShotDetection.Probe(clips.Interlaced);
        Assert.Equal(FieldOrder.TopFirst, interlaced.FieldOrder);
        Assert.True(interlaced.IsInterlaced);
        Assert.Equal("mpeg2video", interlaced.Codec);
        var progressive = ShotDetection.Probe(clips.ThreeShots);
        Assert.False(progressive.IsInterlaced);
        Assert.Equal((320, 240, 25.0, 150L, "mpeg4", "yuv420p", 0), (progressive.Width, progressive.Height,
            progressive.FrameRate.Value, progressive.FrameCount, progressive.Codec, progressive.PixelFormat, progressive.Rotation));
        Assert.Equal(6.0, progressive.Duration!.Value.TotalSeconds, 2);
        // A 90-degree rotation tag swaps the size, as decoding does.
        var rotated = ShotDetection.Probe(clips.Rotated);
        Assert.Equal((240, 320), (rotated.Width, rotated.Height));
        Assert.Equal(90, Math.Abs(rotated.Rotation));
    }

    [Fact]
    public void AutoDeinterlacesOnlyInterlacedStreams()
    {
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true };
        string Stats(string path, DeinterlaceMode mode)
        {
            var r = ShotDetection.Detect(path, options with { Deinterlace = mode });
            Assert.Equal(mode == DeinterlaceMode.On || mode == DeinterlaceMode.Auto && path == clips.Interlaced, r.Video.Deinterlaces);
            return r.Stats!.Csv(r.Video.Position);
        }
        Assert.Equal(Stats(clips.Interlaced, DeinterlaceMode.On), Stats(clips.Interlaced, DeinterlaceMode.Auto));
        Assert.Equal(Stats(clips.ThreeShots, DeinterlaceMode.Off), Stats(clips.ThreeShots, DeinterlaceMode.Auto));
        Assert.NotEqual(Stats(clips.Interlaced, DeinterlaceMode.Off), Stats(clips.Interlaced, DeinterlaceMode.On));
    }

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
            var actual = ShotDetection.Detect(clips.ThreeShots, options with { Deinterlace = DeinterlaceMode.On });
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
