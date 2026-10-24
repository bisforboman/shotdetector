using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// DetectionOptions.OnShot: every overload tells exactly the result's shots, in order, once; with one detector while the
/// frames are still being read, with several at the end. The chunked file test decodes in-process, so it needs
/// SHOTDETECTOR_FFMPEG_LIBS; the frame-source tests need nothing.
/// </summary>
public class OnShotTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    /// <summary>Records each shot on the thread that reports it (System.Progress would post it elsewhere).</summary>
    sealed class Told : IProgress<Shot>
    {
        public List<Shot> Shots { get; } = [];

        public void Report(Shot value) => Shots.Add(value);
    }

    static YuvVideo Video() => new() { Width = 64, Height = 48, FrameRate = new(25, 1) };

    /// <summary>Three shots of still noise (cuts at frames 25 and 50), 75 frames; <paramref name="pulled"/> sees each frame's index as it's taken.</summary>
    static IEnumerable<YuvFrame> Frames(YuvVideo video, Action<int>? pulled = null)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        for (int i = 0; i < 75; i++)
        {
            var random = new Random((i / 25) + 1);
            byte[] Plane(int width, int height)
            {
                var plane = new byte[width * height];
                random.NextBytes(plane);
                return plane;
            }

            pulled?.Invoke(i);
            yield return new YuvFrame(Plane(w, h), w, Plane(cw, ch), cw, Plane(cw, ch), cw);
        }
    }

    [Fact]
    public void FrameSourceTellsTheResultsShotsWhileFramesArePulled()
    {
        var told = new Told();
        int toldAtLastFrame = -1;
        var video = Video();
        var result = ShotDetection.Detect(video,
            Frames(video, i => { if (i == 74) toldAtLastFrame = told.Shots.Count; }),
            new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), OnShot = told });
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);

        // The shots ending at 25 and 50 were told before the last frame was pulled.
        Assert.Equal(2, toldAtLastFrame);
    }

    [Fact]
    public void SeveralDetectorsTellTheShotsAtTheEnd()
    {
        var told = new Told();
        int toldAtLastFrame = -1;
        var video = Video();
        var result = ShotDetection.Detect(video,
            Frames(video, i => { if (i == 74) toldAtLastFrame = told.Shots.Count; }),
            new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), OnShot = told, Detectors = [new(DetectorKind.Content), new(DetectorKind.Histogram)] });
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);
        Assert.Equal(0, toldAtLastFrame);
    }

    [Fact]
    public void FilePathSequentialTellsTheResultsShots()
    {
        var told = new Told();
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Mode = DetectionMode.Sequential, OnShot = told });
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);
    }

    [Fact]
    public void StreamTellsTheResultsShots()
    {
        var told = new Told();
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var result = ShotDetection.Detect(file, new DetectionOptions { OnShot = told });
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);
    }

    // Auto still chunks a file with OnShot set (it only listens), and the chunks' shots are told in order, once.
    [Fact]
    public void FilePathInChunksTellsTheResultsShots()
    {
        if (Libs is null)
            return;
        var told = new Told();
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Chunks = 3, OnShot = told });
        Assert.Equal(3, result.Video.Chunks);
        Assert.Equal(3, result.Shots.Count);
        Assert.Equal(result.Shots, told.Shots);
    }

    // DetectStreamAsync tells OnShot too: both get every shot.
    [Fact]
    public async Task DetectStreamAsyncTellsOnShotAndYields()
    {
        var told = new Told();
        var options = new DetectionOptions { OnShot = told };
        var expected = ShotDetection.Detect(clips.ThreeShots).Shots;
        var streamed = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(clips.ThreeShots, options))
            streamed.Add(shot);
        Assert.Equal(expected, streamed);
        Assert.Equal(expected, told.Shots);
    }
}
