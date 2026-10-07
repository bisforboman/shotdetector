using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// In-process decoding gives the same per-frame stats, labels and shots as the ffmpeg executable. Needs FFmpeg 8.1's
/// shared libraries: SHOTDETECTOR_FFMPEG_LIBS names their folder (CI sets it); without it these tests do nothing.
/// </summary>
public class InProcessTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Cases => ["plain", "start", "end", "crop", "full", "scale", "fastyuv", "dropped", "mpegps", "skip"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsTheFfmpegExecutable(string name)
    {
        if (Libs is null)
            return;
        string path = name switch { "dropped" => clips.ThreeShotsDropped, "mpegps" => clips.ThreeShotsMpegPs, _ => clips.ThreeShots };
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = name != "skip", FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
        options = name switch
        {
            "start" => options with { StartTime = "1.5" },
            "end" => options with { StartTime = "41", EndTime = "4.5" },
            "crop" => options with { Crop = (10, 20, 300, 200) },
            "full" => options with { FullFrames = true },
            "scale" => options with { Downscale = 1 },
            "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
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
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var pipe = ShotDetection.Detect(copy, options);
            var inProcess = ShotDetection.Detect(copy, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodesInProcess);
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
            // The colour tags, pixel format and the rest, read in-process as ffprobe reads them.
            string ffprobe = Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            Assert.Equal(Values(VideoReader.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", VideoReader.ProbeEntries, "-of", "default=nw=1", copy], default)),
                Values(InProcessProbe.Properties(copy, Libs).Split('\n').Where(l => !l.StartsWith("pts=")).Aggregate("", (a, l) => a + l + "\n")));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AutoDecodesInProcessWhenTheLibrariesLoad()
    {
        if (Libs is null)
            return;
        var auto = new VideoReader(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Libs });
        Assert.Equal(VideoDecoder.Auto, new DetectionOptions().Decoder);
        Assert.True(auto.DecodesInProcess);
        Assert.False(new VideoReader(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess }).DecodesInProcess);
        // Inputs in-process doesn't handle use the executable, in Auto as with InProcess.
        Assert.False(new VideoReader(clips.Rotated, new DetectionOptions { FfmpegDirectory = Libs }).DecodesInProcess);
    }

    [Fact]
    public void AutoFindsTheFramesTheLibrariesCannotDecode()
    {
        // AV1: our build (ShotDetector.Native) has no software AV1 decoder, so Auto must fall back to the executable
        // rather than give an empty scene list. With libraries that do decode it, it stays in-process.
        if (Libs is null)
            return;
        string av1 = Path.Combine(Path.GetTempPath(), $"shotdetector-av1-{Guid.NewGuid():N}.mkv");
        try
        {
            try { VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "40", av1], default); }
            catch (ShotDetectionException) { return; } // this ffmpeg can't encode AV1
            var result = ShotDetection.Detect(av1, new DetectionOptions { FfmpegDirectory = Libs });
            Assert.Equal(150, result.FrameCount);
            Assert.Equal(3, result.Shots.Count);
        }
        finally
        {
            File.Delete(av1);
        }
    }

    public static TheoryData<string> ProbeClips => ["three", "rotated", "mkv", "faststart", "mpegps", "dropped", "long", "interlaced"];

    [Theory]
    [MemberData(nameof(ProbeClips))]
    public void ProbesLikeFfprobe(string name)
    {
        // The in-process probe must read what ffprobe prints: the same values once parsed, the same packet timestamps.
        if (Libs is null)
            return;
        string path = name switch
        {
            "rotated" => clips.Rotated, "mkv" => clips.ThreeShotsMkv, "faststart" => clips.ThreeShotsFaststart,
            "mpegps" => clips.ThreeShotsMpegPs, "dropped" => clips.ThreeShotsDropped, "long" => clips.Long, "interlaced" => clips.Interlaced, _ => clips.ThreeShots,
        };
        string ffprobe = Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var expected = Parse(VideoReader.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries",
            VideoReader.ProbeEntries + ":packet=pts", "-of", "default=nw=1", path], default));
        var actual = Parse(InProcessProbe.Properties(path, Libs));
        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(expected.Pts, actual.Pts);
        if (name == "mpegps")
        {
            var frames = VideoReader.Run(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts,pkt_dts", "-of", "default=nw=1", path], default);
            Assert.Equal(FrameTimes(frames), InProcessProbe.FrameTimestamps(path, Libs));
        }

        // As VideoReader reads it: the last value of a key wins (format=duration after the stream's).
        static (SortedDictionary<string, string> Values, List<string> Pts) Parse(string text)
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pts = new List<string>();
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (line[..eq] == "pts") pts.Add(line[(eq + 1)..]); else values[line[..eq]] = line[(eq + 1)..];
            }
            return (values, pts);
        }

        static List<long> FrameTimes(string text)
        {
            var result = new List<long>();
            long? framePts = null;
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
                if (line.StartsWith("pts="))
                    framePts = long.TryParse(line[4..], out long p) && p != 0 ? p : null;
                else if (line.StartsWith("pkt_dts="))
                {
                    result.Add(framePts ?? (long.TryParse(line[8..], out long d) ? d : 0));
                    framePts = null;
                }
            return result;
        }
    }

    /// <summary>ffprobe's key=value lines as VideoReader reads them: the last value of a key wins.</summary>
    static SortedDictionary<string, string> Values(string text)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            values[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
        return values;
    }

    [Fact]
    public void CanDecodeInProcessFindsTheLibraries()
    {
        if (Libs is null)
            return;
        Assert.True(ShotDetection.CanDecodeInProcess(Libs));
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
