using System.Diagnostics;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>Synthetic clips made with ffmpeg (which must be on PATH, as for the library itself).</summary>
public sealed class Clips : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-tests-").FullName;

    /// <summary>Hard cuts at frames 50 and 100 between three test patterns; 150 frames at 25 fps.</summary>
    public string ThreeShots { get; }

    /// <summary><see cref="ThreeShots"/> with a 90° rotation tag, as phones store portrait video.</summary>
    public string Rotated { get; }

    /// <summary>5 minutes of moving test pattern: a full run takes seconds, so it can be cancelled mid-run.</summary>
    public string Long { get; }

    /// <summary><see cref="ThreeShots"/> as mkv and as an mp4 with its headers first: both can be piped.</summary>
    public string ThreeShotsMkv { get; }
    public string ThreeShotsFaststart { get; }

    /// <summary><see cref="ThreeShots"/> as MPEG-2 in an MPEG program stream: not every packet has a pts.</summary>
    public string ThreeShotsMpegPs { get; }

    /// <summary><see cref="ThreeShots"/> as an image sequence pattern (frames/0001.png ...).</summary>
    public string ThreeShotsImages { get; }

    /// <summary>
    /// <see cref="ThreeShots"/> with the first bytes of packets 60, 61 and 120 overwritten, so the decoder
    /// can't parse two of them and drops those frames (148 decoded), like a damaged download.
    /// </summary>
    public string ThreeShotsDropped { get; }

    /// <summary><see cref="ThreeShots"/> as interlaced MPEG-2 (top field first), as broadcast video is flagged.</summary>
    public string Interlaced { get; }

    public Clips()
    {
        ThreeShots = Make("three.mp4",
            "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i smptebars=s=320x240:r=25:d=2 -f lavfi -i mandelbrot=s=320x240:r=25 " +
            "-filter_complex [2]trim=duration=2,setpts=PTS-STARTPTS[m];[0][1][m]concat=n=3:v=1:a=0,format=yuv420p");
        Rotated = Path.Combine(_dir, "rotated.mp4");
        Run($"-v error -y -display_rotation 90 -i {ThreeShots} -c copy {Rotated}");
        Long = Make("long.mp4", "-f lavfi -i testsrc2=s=640x360:r=25:d=300 -vf format=yuv420p");
        ThreeShotsMkv = Path.Combine(_dir, "three.mkv");
        Run($"-v error -y -i {ThreeShots} -c copy {ThreeShotsMkv}");
        ThreeShotsFaststart = Path.Combine(_dir, "three-faststart.mp4");
        Run($"-v error -y -i {ThreeShots} -c copy -movflags +faststart {ThreeShotsFaststart}");
        ThreeShotsMpegPs = Path.Combine(_dir, "three.mpg");
        Run($"-v error -y -i {ThreeShots} -c:v mpeg2video -q:v 3 {ThreeShotsMpegPs}");
        Interlaced = Path.Combine(_dir, "interlaced.mpg");
        Run($"-v error -y -i {ThreeShots} -c:v mpeg2video -q:v 3 -flags +ildct+ilme -top 1 {Interlaced}");
        Directory.CreateDirectory(Path.Combine(_dir, "frames"));
        ThreeShotsImages = Path.Combine(_dir, "frames", "%04d.png");
        Run($"-v error -y -i {ThreeShots} {ThreeShotsImages}");
        ThreeShotsDropped = Path.Combine(_dir, "dropped.mp4");
        var bytes = File.ReadAllBytes(ThreeShots);
        var positions = VideoReader.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pos", "-of", "csv=p=0", ThreeShots], default)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(long.Parse).ToArray();
        foreach (int k in new[] { 60, 61, 120 })
            bytes.AsSpan((int)positions[k], 4).Fill(0xff);
        File.WriteAllBytes(ThreeShotsDropped, bytes);
    }

    string Make(string name, string input)
    {
        string path = Path.Combine(_dir, name);
        // mpeg4 is built into every ffmpeg, unlike libx264.
        Run($"-v error -y {input} -r 25 -c:v mpeg4 -q:v 3 {path}");
        return path;
    }

    static void Run(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"ffmpeg failed: {err}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class VideoTests(Clips clips) : IClassFixture<Clips>
{
    [Fact]
    public void FindsTheHardCuts()
    {
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Detector = DetectorKind.Content });
        Assert.Equal([0L, 50L, 100L], result.Shots.Select(s => s.Start.FrameNum));
        Assert.Equal(150, result.FrameCount);
    }

    [Fact]
    public void RotatedVideoIsReadUpright()
    {
        var result = ShotDetection.Detect(clips.Rotated, new DetectionOptions { Detector = DetectorKind.Content });
        Assert.Equal((240, 320), (result.Video.SourceWidth, result.Video.SourceHeight));
        Assert.Equal([0L, 50L, 100L], result.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void FastYuvGivesTheSameShots()
    {
        var core = ShotDetection.Detect(clips.ThreeShots);
        var fast = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.Equal(FramePipeline.Yuv420Sampled, fast.Video.Pipeline);
        Assert.Equal(Shots.Csv(core.Shots), Shots.Csv(fast.Shots));
    }

    sealed class Recorder : IProgress<DetectionProgress>
    {
        public List<DetectionProgress> Reports { get; } = [];
        public void Report(DetectionProgress value) => Reports.Add(value);
    }

    [Fact]
    public void ReportsProgressUpToAllFrames()
    {
        var recorder = new Recorder();
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { Progress = recorder });
        Assert.NotEmpty(recorder.Reports);
        Assert.Equal((150, 150), (recorder.Reports[^1].FramesProcessed, recorder.Reports[^1].ExpectedFrames));
        // The first report comes before any frame, and every report says how this run decodes.
        Assert.Equal(0, recorder.Reports[0].FramesProcessed);
        Assert.All(recorder.Reports, r => Assert.Equal((result.Video.DecodesInProcess, result.Video.Pipeline), (r.DecodesInProcess, r.Pipeline)));
        Assert.Equal(1.0, recorder.Reports[^1].Fraction);
        Assert.True(recorder.Reports.Zip(recorder.Reports.Skip(1)).All(p => p.First.FramesProcessed <= p.Second.FramesProcessed));
        Assert.Equal(150, result.FrameCount);
    }

    [Fact]
    public void FindsFfmpegInTheGivenDirectory()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string dir = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .First(d => File.Exists(Path.Combine(d, exe)));
        var result = ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = dir });
        Assert.Equal(3, result.Shots.Count);
    }

    [Fact]
    public void MissingFfmpegSaysWhatToDo()
    {
        var e = Assert.Throws<ShotDetectionException>(() =>
            ShotDetection.Detect(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Path.GetTempPath(), Decoder = VideoDecoder.FfmpegProcess }));
        Assert.Equal(ShotDetectionError.FfmpegNotFound, e.Reason);
        Assert.Contains("FfmpegDirectory", e.Message);
    }

    [Fact]
    public void EachDetectionIsATracingSpan()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == ShotDetection.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var parent = new System.Diagnostics.Activity("test").Start();
        var result = ShotDetection.Detect(clips.ThreeShots);
        parent.Stop();
        // Other tests may run detections meanwhile: take this one's span by its parent.
        var span = Assert.Single(spans, a => a.ParentSpanId == parent.SpanId);
        Assert.Equal("ShotDetection.Detect", span.OperationName);
        Assert.Equal(150, span.GetTagItem("shotdetector.frames"));
        Assert.Equal(3, span.GetTagItem("shotdetector.shots"));
        Assert.Equal(320, span.GetTagItem("shotdetector.video.width"));
        Assert.Equal(result.Video.DecodesInProcess ? "inprocess" : "process", span.GetTagItem("shotdetector.decoder"));
    }

    [Fact]
    public void MissingFileIsInvalidInput()
    {
        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(Path.Combine(Path.GetTempPath(), "no-such-video.mp4")));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
    }

    [Fact]
    public void AudioOnlyFileIsInvalidInput()
    {
        string audio = Path.Combine(Path.GetTempPath(), $"shotdetector-audio-{Guid.NewGuid():N}.wav");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=d=1", audio], default);
        try
        {
            var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(audio));
            Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
            Assert.Contains("no video stream", e.Message);
        }
        finally
        {
            File.Delete(audio);
        }
    }

    [Theory]
    [InlineData(DetectorKind.Adaptive)]
    [InlineData(DetectorKind.Content)]
    [InlineData(DetectorKind.Threshold)]
    public async Task StreamingGivesTheSameShotsAsDetect(DetectorKind kind)
    {
        var options = new DetectionOptions { Detector = kind, MinSceneLength = "5" };
        var expected = ShotDetection.Detect(clips.ThreeShots, options).Shots;
        var streamed = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(clips.ThreeShots, options))
            streamed.Add(shot);
        Assert.Equal(expected, streamed);
    }

    [Fact]
    public async Task StreamingYieldsShotsBeforeTheEnd()
    {
        // The long clip has no cuts, so use the three-shot clip and check the first shot arrives
        // as its own item, then stop early: the background decoding must stop too.
        await foreach (var shot in ShotDetection.DetectStreamAsync(clips.ThreeShots))
        {
            Assert.Equal(1, shot.Number);
            Assert.Equal(50, shot.End.FrameNum);
            break;
        }
    }

    [Fact]
    public async Task CancellingStopsStreaming()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ShotDetection.DetectStreamAsync(clips.Long, cancellationToken: cts.Token)) { }
        });
    }

    [Fact]
    public async Task StreamingReportsErrors()
    {
        var e = await Assert.ThrowsAsync<ShotDetectionException>(async () =>
        {
            await foreach (var _ in ShotDetection.DetectStreamAsync("does-not-exist.mp4")) { }
        });
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("faststart")]
    public void PipedInputGivesTheSameShotsAsTheFile(string container)
    {
        var expected = ShotDetection.Detect(clips.ThreeShots);
        using var file = File.OpenRead(container == "mkv" ? clips.ThreeShotsMkv : clips.ThreeShotsFaststart);
        var piped = ShotDetection.Detect(file);
        Assert.True(piped.Video.Streaming);
        Assert.Null(piped.VideoPath);
        Assert.Equal(expected.Shots.Select(s => (s.Start.Timecode(), s.End.Timecode())), piped.Shots.Select(s => (s.Start.Timecode(), s.End.Timecode())));
        Assert.Equal(expected.FrameCount, piped.FrameCount);
    }

    [Fact]
    public async Task PipedInputStreamsShotsOut()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var shots = new List<Shot>();
        await foreach (var shot in ShotDetection.DetectStreamAsync(file))
            shots.Add(shot);
        Assert.Equal([0L, 50L, 100L], shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void PipedInputHonoursTheEndTime()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var r = ShotDetection.Detect(file, new() { EndTime = "3s" });
        Assert.Equal(75, r.FrameCount);
        Assert.Equal(75, r.Shots[^1].End.FrameNum);
    }

    [Fact]
    public void PipedInputCannotSeek()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(file, new() { StartTime = "1s" }));
    }

    [Fact]
    public void PipedMp4WithoutFaststartIsRefusedClearly()
    {
        using var file = File.OpenRead(clips.ThreeShots);
        var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(file));
        Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
        Assert.Contains("faststart", e.Message);
    }

    [Fact]
    public void UrlInputIsStreamed()
    {
        using var server = new System.Net.HttpListener();
        int port = Random.Shared.Next(20000, 60000);
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                var ctx = await server.GetContextAsync();
                ctx.Response.ContentType = "video/mp4";
                try
                {
                    await using var f = File.OpenRead(clips.ThreeShotsFaststart);
                    await f.CopyToAsync(ctx.Response.OutputStream);
                    ctx.Response.Close();
                }
                catch (System.Net.HttpListenerException) { } // ffprobe hangs up after the headers
            }
        });
        try
        {
            var r = ShotDetection.Detect($"http://localhost:{port}/three.mp4");
            Assert.True(r.Video.Streaming);
            Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void ExportsNeedAPath()
    {
        using var file = File.OpenRead(clips.ThreeShotsMkv);
        var r = ShotDetection.Detect(file);
        Assert.Throws<InvalidOperationException>(() => Export.SaveImages(r, Path.GetTempPath()));
    }

    [Fact]
    public void PacketsWithoutPtsTakeTheFrameDtsLikeOpenCv()
    {
        // MPEG-PS leaves the pts out of most packets; every frame must still get its own timestamp.
        var video = new VideoReader(clips.ThreeShotsMpegPs);
        Assert.Equal(150, video.ExpectedFrames);
        Assert.Equal(Enumerable.Range(0, 150).Select(i => i / 25.0), Enumerable.Range(0, 150).Select(i => video.Position(i).Seconds));
        var r = ShotDetection.Detect(clips.ThreeShotsMpegPs, new() { Detector = DetectorKind.Content });
        Assert.Equal([0L, 50L, 100L], r.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void FrameRateOverrideChangesFrameNumbersNotTimes()
    {
        // 25 fps video read as 50 fps: the cut at 2 s is frame 100, not 50.
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content, FrameRate = 50 });
        Assert.Equal(new Fps(50, 1), r.Video.Fps);
        Assert.Equal([(0L, "00:00:00.000"), (100L, "00:00:02.000"), (200L, "00:00:04.000")],
            r.Shots.Select(s => (s.Start.FrameNum, s.Start.Timecode())));
    }

    [Fact]
    public void ImageSequenceIsReadAtTheFrameRate()
    {
        // An image sequence has no times of its own: at 50 fps the cuts (frames 50 and 100) are at 1 s and 2 s.
        var r = ShotDetection.Detect(clips.ThreeShotsImages, new() { Detector = DetectorKind.Content, FrameRate = 50 });
        Assert.Equal([(0L, "00:00:00.000"), (50L, "00:00:01.000"), (100L, "00:00:02.000")],
            r.Shots.Select(s => (s.Start.FrameNum, s.Start.Timecode())));
        Assert.Equal("00:00:03.000", r.Shots[^1].End.Timecode());
    }

    [Fact]
    public void SplitExpandCoversTheWholeVideo()
    {
        // Shots from 2 s to 4 s (frames 50..99); expanded, the clips run from 0 to the end (150 frames).
        var result = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content, StartTime = "2s", EndTime = "5s" });
        string dir = Path.Combine(Path.GetTempPath(), "shotdetector-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Export.SplitVideo(result, dir, new SplitOptions { Expand = true, Copy = false, Preset = "ultrafast" });
            double Duration(string f) => double.Parse(VideoReader.Run("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", f], default),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(2, files.Count);
            Assert.Equal(4.0, Duration(files[0]), 1);   // 0 .. 4 s instead of 2 .. 4 s
            Assert.Equal(2.0, Duration(files[1]), 1);   // 4 .. 6 s instead of 4 .. 5 s
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FramesKeepTheirOwnTimesWhenTheDecoderDropsSome()
    {
        // Two frames before the cut at 100 can't be decoded. Like scenedetect (OpenCV labels a frame by
        // its time), the cut stays frame 100 at 4 s rather than becoming the 98th decoded frame.
        var r = ShotDetection.Detect(clips.ThreeShotsDropped, new() { Detector = DetectorKind.Content });
        Assert.True(r.FrameCount < 150);
        Assert.Equal([(0L, "00:00:00.000"), (50L, "00:00:02.000"), (100L, "00:00:04.000")],
            r.Shots.Select(s => (s.Start.FrameNum, s.Start.Timecode())));
    }

    [Fact]
    public void PipedInputThatDoesNotStartAtZeroKeepsItsTimes()
    {
        // A transport stream starts at 1.4 s; times are measured from there, as for the file.
        string ts = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.ts");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c:v", "mpeg2video", "-q:v", "3", ts], default);
        try
        {
            var file = ShotDetection.Detect(ts, new() { Detector = DetectorKind.Content });
            using var stream = File.OpenRead(ts);
            var piped = ShotDetection.Detect(stream, new() { Detector = DetectorKind.Content });
            Assert.Equal(file.Shots.Select(s => s.Start.Timecode()), piped.Shots.Select(s => s.Start.Timecode()));
            Assert.Equal("00:00:02.000", piped.Shots[1].Start.Timecode());
        }
        finally { File.Delete(ts); }
    }

    [Fact]
    public async Task DetectAsyncGivesTheSameShots()
    {
        var expected = ShotDetection.Detect(clips.ThreeShots).Shots;
        var r = await ShotDetection.DetectAsync(clips.ThreeShots);
        Assert.Equal(expected, r.Shots);
        Assert.Equal("00:00:02.000", r.Shots[1].Start.ToString());
    }

    [Fact]
    public async Task DetectAsyncCanBeCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShotDetection.DetectAsync(clips.Long, cancellationToken: cts.Token));
    }

    [Fact]
    public void TimelineExportsPlaceTheCuts()
    {
        // Cuts at 2 s and 4 s (frames 50 and 100) of a 6 s, 25 fps clip.
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        string edl = Timeline.Edl(r, reel: "B01");
        Assert.StartsWith("* CREATED WITH SHOTDETECTOR", edl);
        Assert.Contains("\nTITLE: three\nFCM: NON-DROP FRAME\n\n001  B01 V     C        00:00:00:00 00:00:02:00 00:00:00:00 00:00:02:00\n", edl);
        Assert.EndsWith("003  B01 V     C        00:00:04:00 00:00:06:00 00:00:04:00 00:00:06:00\n", edl);
        Assert.Contains("01:00:02:00", Timeline.Edl(r, startTimecode: "01:00:00:00"));
        Assert.Equal("0 I -1\n50 I -1\n100 I -1\n", Timeline.Qp(r));
        string fcpx = Timeline.Fcpx(r);
        Assert.Contains("<format id=\"r1\" name=\"FFVideoFormat240p2500\" frameDuration=\"1/25s\" width=\"320\" height=\"240\"/>", fcpx);
        Assert.Contains("<asset-clip name=\"Shot 2\" ref=\"r2\" offset=\"2s\" start=\"2s\" duration=\"2s\"/>", fcpx);
        string fcp7 = Timeline.Fcp7(r);
        Assert.Contains("<start>100</start>", fcp7);
        Assert.Contains("<ntsc>False</ntsc>", fcp7);
        string otio = Timeline.Otio(r, audio: false);
        Assert.Contains("\"name\": \"Video 1\"", otio);
        Assert.DoesNotContain("Audio 1", otio);
        Assert.Contains("\"value\": 50.0", otio);
    }

    [Fact]
    public void SeveralDetectorsCombineTheirCuts()
    {
        var content = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        var both = ShotDetection.Detect(clips.ThreeShots, new()
        {
            Detectors = [new(DetectorKind.Content), new(DetectorKind.Histogram) { Threshold = 0.05 }],
        });
        Assert.Equal(content.Shots.Select(s => s.Start.FrameNum), both.Shots.Select(s => s.Start.FrameNum));
        Assert.Equal([50L, 100L], both.Cuts.Select(c => c.FrameNum));
        // A per-detector threshold too high to cut leaves the other detector's cuts.
        var one = ShotDetection.Detect(clips.ThreeShots, new()
        {
            Detectors = [new(DetectorKind.Content) { Threshold = 255 }, new(DetectorKind.Histogram)],
        });
        Assert.Equal([0L, 50L, 100L], one.Shots.Select(s => s.Start.FrameNum));
    }

    [Fact]
    public void DownscaleSetsTheProcessingSize()
    {
        Assert.Equal((320, 240), (ShotDetection.Detect(clips.ThreeShots, new() { Downscale = 1 }).Video.Width, 240));
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Downscale = 3 });
        Assert.Equal((107, 80), (r.Video.Width, r.Video.Height));
    }

    [Fact]
    public void LoadScenesReadsAShotListBack()
    {
        var detected = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        string csv = Path.Combine(Path.GetTempPath(), $"shotdetector-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(csv, Shots.Csv(detected.Shots));
            var loaded = ShotDetection.LoadScenes(clips.ThreeShots, csv);
            Assert.Equal(detected.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)), loaded.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));
            Assert.Equal([50L, 100L], loaded.Cuts.Select(c => c.FrameNum));
            // By time instead, and only from 3 s: the cut at 2 s falls outside.
            var fromThree = ShotDetection.LoadScenes(clips.ThreeShots, csv, "Start Timecode", new() { StartTime = "3s" });
            Assert.Equal([(75L, 100L), (100L, 150L)], fromThree.Shots.Select(s => (s.Start.FrameNum, s.End.FrameNum)));
            Assert.Equal("00:00:03.000", fromThree.Shots[0].Start.Timecode());
        }
        finally { File.Delete(csv); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(37, false)]
    [InlineData(0, true)]
    public void SampledFramesAreTheFullFramesResized(int start, bool crop)
    {
        // The default MIT pipeline pipes only the pixels the resize reads (when they are under a quarter of the frame,
        // hence the downscale); the frames must be the same.
        var o = new DetectionOptions { Crop = crop ? (10, 6, 301, 231) : null, Downscale = 5 };
        var sampled = new VideoReader(clips.ThreeShots, o);
        var full = new VideoReader(clips.ThreeShots, o with { FullFrames = true });
        Assert.Equal(FramePipeline.SampledBgr, sampled.Pipeline);
        Assert.Equal(FramePipeline.FullFrameResize, full.Pipeline);
        Assert.Equal(full.Frames(start, 40).Select(f => f.ToArray()), sampled.Frames(start, 40).Select(f => f.ToArray()));
    }

    [Fact]
    public void AlreadyCancelledTokenThrowsImmediately() =>
        Assert.Throws<OperationCanceledException>(() => ShotDetection.Detect(clips.Long, cancellationToken: new CancellationToken(true)));

    [Fact]
    public void CancellingStopsDetection()
    {
        // A full run takes several times longer than this; without cancellation Detect would return normally.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.ThrowsAny<OperationCanceledException>(() => ShotDetection.Detect(clips.Long, cancellationToken: cts.Token));
    }

    [Fact]
    public void CancellingStopsTheFrameStreamAtTheNextFrame()
    {
        using var cts = new CancellationTokenSource();
        int read = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var _ in new VideoReader(clips.Long).Frames(cts.Token))
                if (++read == 10)
                    cts.Cancel();
        });
        Assert.Equal(10, read);
    }

    [Fact]
    public void CancellingStopsExport()
    {
        var result = ShotDetection.Detect(clips.ThreeShots);
        string dir = Directory.CreateTempSubdirectory("shotdetector-split-").FullName;
        try
        {
            Assert.Throws<OperationCanceledException>(() => Export.SplitVideo(result, dir, cancellationToken: new CancellationToken(true)));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
