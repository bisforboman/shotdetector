using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// In-process decoding gives the same per-frame stats, labels and shots as the ffmpeg executable. Needs FFmpeg 8.1's
/// shared libraries: SHOTDETECTOR_FFMPEG_LIBS names their folder (CI sets it); without it these tests do nothing.
/// </summary>
public class InProcessTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    public static TheoryData<string> Cases => ["plain", "start", "end", "crop", "full", "scale", "fastyuv", "dropped", "mpegps", "skip", "mpegps-start", "deinterlace", "deinterlace-start", "deinterlace-fastyuv"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsTheFfmpegExecutable(string name)
    {
        if (Libs is null)
            return;
        string path = name switch { "dropped" => clips.ThreeShotsDropped, "mpegps" or "mpegps-start" => clips.ThreeShotsMpegPs, _ when name.StartsWith("deinterlace") => clips.Interlaced, _ => clips.ThreeShots };
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
            "mpegps-start" => options with { StartTime = "2.5" },
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.On },
            "deinterlace-start" => options with { Deinterlace = DeinterlaceMode.On, StartTime = "2.5" },
            "deinterlace-fastyuv" => options with { Deinterlace = DeinterlaceMode.On, Yuv420Converter = new SwscaleYuv420() },
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

    // Row-wise yadif (FastYuv's Yadif) gives FFmpeg's yadif results: per-frame stats from the whole frames and from the
    // sampled rows, from the start and after a seek, 8-bit 4:2:0 and 4:2:2, 10-bit 4:2:2, and with FastYuv.
    [Theory]
    [InlineData("420", "-pix_fmt yuv420p", "")]
    [InlineData("420-start", "-pix_fmt yuv420p", "start")]
    [InlineData("420-full", "-pix_fmt yuv420p", "full")]
    [InlineData("420-fastyuv", "-pix_fmt yuv420p", "fastyuv")]
    [InlineData("422", "-pix_fmt yuv422p", "")]
    [InlineData("422-10bit", "-pix_fmt yuv422p10le", "")]
    [InlineData("422-10bit-full", "-pix_fmt yuv422p10le", "full")]
    [InlineData("bff", "-pix_fmt yuv420p -vf setfield=bff", "")]
    [InlineData("1080i-422", "-t 1 -vf scale=1920:1080,setfield=tff -pix_fmt yuv422p", "")] // sparse rows: 1080 lines sampled to 144
    [InlineData("1080i-420", "-t 1 -vf scale=1920:1080,setfield=tff -pix_fmt yuv420p", "")]
    public void RowWiseYadifEqualsFfmpegs(string name, string args, string variant)
    {
        if (Libs is null)
            return;
        string clip = Path.Combine(Path.GetTempPath(), $"shotdetector-yadif-{name}-{Guid.NewGuid():N}.mkv");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.Interlaced, .. args.Split(' '), "-c:v", "ffv1", clip], default);
        try
        {
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Deinterlace = DeinterlaceMode.On };
            options = variant switch
            {
                "start" => options with { StartTime = "2.5" },
                "full" => options with { FullFrames = true },
                "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
                _ => options,
            };
            // 9 to 16 bits: FFmpeg 8.1's x86 SIMD yadif gives a different result on every run, so the reference is its C
            // code (-cpuflags 0) as a lossless copy, read without deinterlacing.
            bool deep = args.Contains("10le");
            string reference = Path.ChangeExtension(clip, ".ref.mkv");
            if (deep)
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-cpuflags", "0", "-i", clip, "-vf", "yadif", "-c:v", "ffv1", reference], default);
            var ffmpegs = deep ? ShotDetection.Detect(reference, options with { Deinterlace = DeinterlaceMode.Off }) : ShotDetection.Detect(clip, options);
            var ours = ShotDetection.Detect(clip, options with { Yadif = new Yadif() });
            File.Delete(reference);
            Assert.True(ours.Video.DecodesInProcess);
            Assert.Equal(ffmpegs.Stats!.Csv(ffmpegs.Video.Position), ours.Stats!.Csv(ours.Video.Position));
        }
        finally
        {
            File.Delete(clip);
        }
    }

    // Display matrices (rotations and flips) as ffmpeg's autorotate applies them: transpose, hflip, vflip.
    [Theory]
    [InlineData("90", "-pix_fmt yuv420p", "-display_rotation 90", "")]
    [InlineData("270", "-pix_fmt yuv420p", "-display_rotation -90", "")]
    [InlineData("180", "-pix_fmt yuv420p", "-display_rotation 180", "")]
    [InlineData("hflip", "-pix_fmt yuv420p", "-display_hflip", "")]
    [InlineData("vflip", "-pix_fmt yuv420p", "-display_vflip", "")]
    [InlineData("90-hflip", "-pix_fmt yuv420p", "-display_rotation 90 -display_hflip", "")]
    [InlineData("270-hflip", "-pix_fmt yuv420p", "-display_rotation -90 -display_hflip", "")]
    [InlineData("180-hflip", "-pix_fmt yuv420p", "-display_rotation 180 -display_hflip", "")]
    [InlineData("90-odd", "-vf crop=319:237:0:0 -pix_fmt yuv420p", "-display_rotation 90", "")]
    [InlineData("90-10bit", "-pix_fmt yuv420p10le", "-display_rotation 90", "")]
    [InlineData("180-422", "-pix_fmt yuv422p", "-display_rotation 180", "")]
    [InlineData("90-start", "-pix_fmt yuv420p", "-display_rotation 90", "start")]
    [InlineData("90-full", "-pix_fmt yuv420p", "-display_rotation 90", "full")]
    [InlineData("90-deinterlace", "-pix_fmt yuv420p -vf setfield=tff", "-display_rotation 90", "deinterlace-executable")] // ffmpeg rotates before yadif
    [InlineData("90-422", "-pix_fmt yuv422p", "-display_rotation 90", "executable")] // transpose converts 4:2:2 first
    public void RotatesAsTheFfmpegExecutable(string name, string format, string display, string variant)
    {
        if (Libs is null)
            return;
        string dir = Path.Combine(Path.GetTempPath(), $"shotdetector-rot-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string plain = Path.Combine(dir, "plain.mkv"), rotated = Path.Combine(dir, "rotated.mov");
        try
        {
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, .. format.Split(' '), "-c:v", "ffv1", plain], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. display.Split(' '), "-i", plain, "-c", "copy", rotated], default);
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            options = variant switch
            {
                "start" => options with { StartTime = "1.5" },
                "full" => options with { FullFrames = true },
                "deinterlace-executable" => options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() },
                _ => options,
            };
            var pipe = ShotDetection.Detect(rotated, options);
            var inProcess = ShotDetection.Detect(rotated, options with { Decoder = VideoDecoder.InProcess });
            Assert.Equal(!variant.EndsWith("executable"), inProcess.Video.DecodesInProcess);
            Assert.Equal((pipe.Video.Width, pipe.Video.Height), (inProcess.Video.Width, inProcess.Video.Height));
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
