using System.Diagnostics;
using System.Runtime.InteropServices;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Detection on frames the caller decoded (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// a file's frames, decoded here with FrameReader, give exactly the file's result with the same converter (FastYuv's).
/// Needs FFmpeg 8.1's shared libraries for the decoding (SHOTDETECTOR_FFMPEG_LIBS); without them the comparisons do
/// nothing. Its checks of the input need nothing.
/// </summary>
public class FrameInputTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Cases => ["adaptive", "content", "threshold", "histogram", "hash", "start", "end", "crop", "crop-scale", "scale", "skip", "drop", "v071", "parallel"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsTheFile(string name)
    {
        if (Libs is null)
            return;

        // Without a resize the file's frames are FFmpeg's conversion, which FastYuv reproduces on x86 (elsewhere FFmpeg's
        // assembly rounds differently).
        if (name is "scale" or "crop-scale" && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var options = new DetectionOptions { CollectStats = name != "skip", FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "content" => options with { Detector = DetectorKind.Content },
            "threshold" => options with { Detector = DetectorKind.Threshold, Threshold = 40 },
            "histogram" => options with { Detector = DetectorKind.Histogram },
            "hash" => options with { Detector = DetectorKind.Hash },
            "start" => options with { Detector = DetectorKind.Content, StartTime = "1.5" },
            "end" => options with { Detector = DetectorKind.Content, StartTime = "41", EndTime = "4.5" },
            "crop" => options with { Detector = DetectorKind.Content, Crop = (10, 20, 300, 200) },
            "crop-scale" => options with { Detector = DetectorKind.Content, Crop = (10, 20, 300, 200), Downscale = 1 },
            "scale" => options with { Downscale = 1 },
            "skip" => options with { Detector = DetectorKind.Content, FrameSkip = 2 },
            "drop" => options with { Detectors = [new(DetectorKind.Content), new(DetectorKind.Histogram)], DropShortScenes = true, MinSceneLength = "60" },
            "v071" => options with { Compatibility = PySceneDetectVersion.V0_7_1 },
            "parallel" => options with { Mode = DetectionMode.Parallel },
            _ => options,
        };
        AssertSame(clips.ThreeShots, options);
    }

    // H.264 with B-frames at 29.97 fps in mp4 (an edit list, decode order unlike presentation order, timestamps whose
    // microseconds round): the showcase's input. The same stream in MPEG-TS starts at 1.4 s (StartPts). Made with the
    // ffmpeg executable's libx264; skipped without it.
    [Theory]
    [InlineData(DetectorKind.Adaptive, "mp4")]
    [InlineData(DetectorKind.Content, "mp4")]
    [InlineData(DetectorKind.Histogram, "mp4")]
    [InlineData(DetectorKind.Content, "ts")]
    public void SameAsTheFileForH264WithBFrames(DetectorKind detector, string container)
    {
        if (Libs is null || H264Clip() is not { } mp4)
            return;
        string clip = container == "ts" ? Path.ChangeExtension(mp4, ".ts") : mp4;
        if (container == "ts")
            Assert.NotEqual(0, FrameReader.MediaProbe.Probe(clip).Video!.StartPts);
        AssertSame(clip, new DetectionOptions { Detector = detector, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
    }

    [Fact]
    public void NeedsAConverter()
    {
        var e = Assert.Throws<ArgumentException>(() => ShotDetection.Detect(Video(), Frames(Video(), 3)));
        Assert.Contains("Yuv420Converter", e.Message);
    }

    [Theory]
    [InlineData("deinterlace")]
    [InlineData("ffmpeg-resize")]
    [InlineData("tone")]
    [InlineData("format")]
    [InlineData("odd")]
    [InlineData("colour")]
    [InlineData("plane")]
    public void RejectsWhatItCantReproduce(string name)
    {
        var video = name switch
        {
            "format" => Video() with { PixelFormat = "yuv422p" },
            "odd" => Video() with { Height = 63 },
            "colour" => Video() with { ColorSpace = "ycgco" },
            _ => Video(),
        };
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        options = name switch
        {
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.Auto },
            "ffmpeg-resize" => options with { FfmpegResize = true },
            "tone" => options with { TrimReferenceTone = true },
            _ => options,
        };
        var frames = Frames(video, 3);
        if (name == "plane")
            frames = frames.Select(f => f with { U = f.U[..^4] });
        Assert.Throws<ArgumentException>(() => ShotDetection.Detect(video, frames, options));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    [InlineData(30, true)]
    public void FramesMustComeAtTheFrameRate(int late, bool fails)
    {
        // Frame 5 is `late` hundredths of a second after where the rate puts it: up to half a frame (0.02 s) is fine.
        var video = Video() with { TimeBase = new(1, 100) };
        var frames = Frames(video, 10).Select((f, i) => f with { Pts = i * 4 + (i == 5 ? late : 0) });
        var options = new DetectionOptions { Yuv420Converter = new SwscaleYuv420() };
        if (fails)
            Assert.Equal(ShotDetectionError.InvalidInput, Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(video, frames, options)).Reason);
        else
            Assert.Equal(10, ShotDetection.Detect(video, frames, options).FrameCount);
    }

    [Fact]
    public void FramesAreAtTheirPtsFromTheStreamsStart()
    {
        // As a file's: (pts - start) in microseconds, frame 0 (at 0) by its number.
        var video = Video() with { TimeBase = new(1, 100), StartPts = 100 };
        var frames = Frames(video, 10).Select((f, i) => f with { Pts = 100 + i * 4 });
        var result = ShotDetection.Detect(video, frames, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        Assert.Equal(FrameTime.Pts(280_000, 1_000_000, new Fps(25, 1)), result.Video.Position(7));
        Assert.Equal(FrameTime.Pts(0, 25, new Fps(25, 1)), result.Video.Position(0));
    }

    [Fact]
    public void WithoutTimestampsFramesAreAtTheirNumberOverTheRate()
    {
        var video = Video();
        var result = ShotDetection.Detect(video, Frames(video, 50), new DetectionOptions { Yuv420Converter = new SwscaleYuv420(), CollectStats = true });
        Assert.Equal(50, result.FrameCount);
        Assert.Equal(FrameTime.Pts(7, 25, new Fps(25, 1)), result.Video.Position(7));
        Assert.False(result.Video.DecodedInProcess);
        Assert.Null(result.VideoPath);
        Assert.Equal((64, 48), (result.Video.ProcessedWidth, result.Video.ProcessedHeight));
    }

    // Nothing on this path loads FFmpeg: in a process of its own (this one has FFmpeg.AutoGen loaded by other tests), with
    // no ffmpeg on PATH and no FFmpeg libraries named, FramesWithoutFfmpeg detects on generated frames and checks that
    // neither FFmpeg.AutoGen nor any of FFmpeg's libraries got loaded (the system's would be found if anything tried).
    [Fact]
    public void LoadsNothingOfFfmpeg()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string check = Path.Combine(bin.Parent!.Parent!.Parent!.Parent!.FullName, "FramesWithoutFfmpeg", "bin", bin.Parent.Name, bin.Name, "FramesWithoutFfmpeg.dll");
        string dotnet = Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet"
            ? host
            : Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(check);
        psi.Environment.Remove("SHOTDETECTOR_FFMPEG_LIBS");
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("DYLD_LIBRARY_PATH");
        psi.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"FramesWithoutFfmpeg failed ({p.ExitCode}): {err}");
        Assert.StartsWith("Timecode List:", output.Result);
    }

    // The frames of a file, as a decoder gives them, and the file's result for them.
    static void AssertSame(string path, DetectionOptions options)
    {
        var file = ShotDetection.Detect(path, options);
        Assert.True(file.Video.DecodedInProcess);
        var (video, frames) = Decoded(path);
        var ours = ShotDetection.Detect(video, frames, options);
        Assert.Equal(file.FrameCount, ours.FrameCount);
        Assert.Equal(file.MinSceneLengthFrames, ours.MinSceneLengthFrames);
        Assert.Equal(file.Cuts, ours.Cuts);
        Assert.Equal(file.Shots, ours.Shots);
        Assert.Equal(Shots.Csv(file.Shots, cuts: file.Cuts), Shots.Csv(ours.Shots, cuts: ours.Cuts));
        Assert.Equal(file.Stats?.Csv(file.Video.Position), ours.Stats?.Csv(ours.Video.Position));
        Assert.Equal((file.Video.ProcessedWidth, file.Video.ProcessedHeight, file.Video.FrameRate, file.Video.OpenCvFrameCount, file.Video.Duration),
            (ours.Video.ProcessedWidth, ours.Video.ProcessedHeight, ours.Video.FrameRate, ours.Video.OpenCvFrameCount, ours.Video.Duration));

        // Parallel asked for: both read sequentially, for their own reason.
        static string Why(string warning) => warning.Split(": ")[0];
        Assert.Equal(file.Warnings.Select(Why), ours.Warnings.Select(Why));
        Assert.False(ours.Video.DecodedInProcess);
    }

    /// <summary>The file's properties as FFmpeg gives them, and its frames decoded as the file path decodes them.</summary>
    static (YuvVideo Video, IEnumerable<YuvFrame> Frames) Decoded(string path)
    {
        var info = FrameReader.MediaProbe.Probe(path).Video!;
        var reader = new VideoReader(path, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess });
        var decoderOptions = new FrameReader.FrameDecoderOptions
        {
            Autorotate = false,
            CodecOptions = VideoReader.IdctOptions(reader.Codec, reader.UsesXvidIdct) is [var key, var value] ? new Dictionary<string, string> { [key[1..]] = value } : null,
        };
        string colorSpace, colorRange;
        using (var first = new FrameReader.FrameDecoder(path, decoderOptions))
        {
            Assert.True(first.Next());
            colorSpace = first.ColorSpace ?? info.ColorSpace ?? "";
            colorRange = first.ColorRange ?? info.ColorRange ?? "";
        }

        var video = new YuvVideo
        {
            Width = info.Width,
            Height = info.Height,
            FrameRate = info.AverageFrameRate,
            PixelFormat = info.PixelFormat!,
            ColorSpace = colorSpace,
            FullRange = colorRange == "pc",
            TimeBase = info.TimeBase,
            StartPts = info.StartPts ?? 0,
            FrameCount = info.FrameCount,
            Duration = info.DurationPts is { } d ? TimeSpan.FromTicks((long)Math.Round(d * 1e7 * info.TimeBase.Num / info.TimeBase.Den / 10) * 10) : null,
            Codec = info.Codec,
        };
        return (video, Frames());

        IEnumerable<YuvFrame> Frames()
        {
            using var decoder = new FrameReader.FrameDecoder(path, decoderOptions);
            while (decoder.Next())
            {
                var y = decoder.GetPlane(0, out int ys).ToArray();
                var u = decoder.GetPlane(1, out int us).ToArray();
                var v = decoder.GetPlane(2, out int vs).ToArray();
                yield return new YuvFrame(y, ys, u, us, v, vs) { Pts = decoder.Pts };
            }
        }
    }

    /// <summary>ffprobe's seconds become the nearest tick: TimeSpan.FromSeconds(4.56) is 4.5599999.</summary>
    [Fact]
    public void DurationIsTheNearestTick()
    {
        Assert.Equal(TimeSpan.FromTicks(45_600_000), VideoReader.Seconds("4.560000"));
        Assert.Null(VideoReader.Seconds("N/A"));
    }

    static YuvVideo Video() => new() { Width = 64, Height = 48, FrameRate = new(25, 1) };

    /// <summary>Noise frames, two shots of it (a cut at frame 25), with padded rows.</summary>
    static IEnumerable<YuvFrame> Frames(YuvVideo video, int count)
    {
        int w = video.Width, h = video.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        for (int i = 0; i < count; i++)
        {
            var random = new Random(i < 25 ? 1 : 2);
            byte[] Plane(int width, int height)
            {
                var plane = new byte[(width + 3) * height];
                random.NextBytes(plane);
                return plane;
            }

            yield return new YuvFrame(Plane(w, h), w + 3, Plane(cw, ch), cw + 3, Plane(cw, ch), cw + 3);
        }
    }

    static string? _h264;
    static readonly object H264Lock = new();

    static string? H264Clip()
    {
        lock (H264Lock)
        {
            if (_h264 is not null)
                return _h264.Length > 0 ? _h264 : null;
            string path = Path.Combine(Directory.CreateTempSubdirectory("shotdetector-frames-").FullName, "bframes.mp4");
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string arg in new[]
            {
                "-v", "error", "-y", "-filter_complex",
                "testsrc2=d=2:s=320x240:r=30000/1001[a];smptebars=d=2:s=320x240:r=30000/1001[b];mandelbrot=s=320x240:r=30000/1001,trim=duration=2[c];[a][b][c]concat=n=3,format=yuv420p",
                "-c:v", "libx264", "-bf", "3", "-g", "48", "-crf", "23", path,
            })
            {
                psi.ArgumentList.Add(arg);
            }

            try
            {
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                _h264 = p.ExitCode == 0 ? path : "";
                if (p.ExitCode == 0)
                {
                    using var ts = Process.Start("ffmpeg", ["-v", "error", "-y", "-i", path, "-c", "copy", Path.ChangeExtension(path, ".ts")]);
                    ts.WaitForExit();
                    Assert.Equal(0, ts.ExitCode);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _h264 = "";
            }

            return _h264.Length > 0 ? _h264 : null;
        }
    }
}
