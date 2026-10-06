using ShotDetector;

// Expected values computed with scenedetect 0.6.4 itself.
public class Compat064Tests
{
    [Theory]
    [InlineData(29.97, 2997, 100)]
    [InlineData(30.0, 30, 1)]
    [InlineData(23.976, 2997, 125)]
    public void FrameRateOverrideKeepsTheFloat(double fps, int num, int den)
    {
        // 0.6.4 uses the --framerate float as is; a decimal fraction gives back exactly that double.
        Assert.Equal(new Fps(num, den), Fps.FromDecimal(fps));
        Assert.Equal(fps, Fps.FromDecimal(fps).Value);
    }

    [Theory]
    [InlineData(854, 480, 285, 160)]
    [InlineData(853, 480, 284, 160)]
    [InlineData(1920, 1080, 274, 154)]
    [InlineData(1280, 720, 256, 144)]
    [InlineData(720, 400, 360, 200)]
    [InlineData(640, 360, 320, 180)]
    [InlineData(480, 270, 480, 270)] // factor 1: no resize
    public void DownscaleUsesAWholeNumberFactorOfTheWidth(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), VideoReader.DownscaledSize064(w, h));

    [Theory]
    [InlineData(90, 30000, 1001, "00:00:03.003", "3.003")]
    [InlineData(100, 9924, 413, "00:00:04.162", "4.162")] // average rate of a variable frame rate clip
    [InlineData(1499, 25, 1, "00:00:59.960", "59.960")]
    [InlineData(37, 24000, 1001, "00:00:01.543", "1.543")]
    public void FrameNumbersAtTheFloatFrameRate(long frame, int num, int den, string timecode, string seconds)
    {
        var t = FrameTime.Frame064(frame, new Fps(num, den));
        Assert.Equal((timecode, seconds, frame), (t.Timecode(), t.SecondsText(), t.FrameNum));
    }

    [Theory]
    [InlineData(0.0, 25)]
    [InlineData(0.1, 25)]   // 0.7.1 gives 26 here
    [InlineData(-1.0, 20)]
    [InlineData(1.0, 30)]
    public void FadeCutUsesTheOldRounding(double bias, long expected)
    {
        var fps = new Fps(25, 1);
        var d = new ThresholdDetector(i => FrameTime.Frame064(i, fps), fps, 12, 15, bias, PySceneDetectVersion.V0_6_4);
        long? cut = null;
        for (int i = 0; i < 60; i++)
            cut ??= d.ProcessAverage(i, i is >= 20 and < 30 ? 3.0 : 100.0)?.FrameNum;
        Assert.Equal(expected, cut);
    }

    [Theory]
    [InlineData(0, 334, 3, new[] { 1, 167, 332 })]
    [InlineData(334, 396, 3, new[] { 335, 365, 394 })]
    [InlineData(100, 101, 3, new[] { 100, 100, 100 })] // padded with the last frame
    [InlineData(100, 102, 3, new[] { 100, 101, 101 })]
    [InlineData(100, 100, 3, new[] { 100, 100, 100 })] // empty shot
    [InlineData(0, 10, 1, new[] { 5 })]
    [InlineData(0, 10, 4, new[] { 1, 4, 7, 8 })]
    [InlineData(50, 57, 5, new[] { 51, 53, 54, 55, 56 })]
    public void ImageFramesMatchSaveImages(int start, int end, int numImages, int[] expected)
    {
        var fps = new Fps(25, 1);
        var shot = new Shot(1, FrameTime.Frame064(start, fps), FrameTime.Frame064(end, fps));
        Assert.Equal(expected, Export.ImageFrames064([shot], numImages, 1)[0]);
    }
}
