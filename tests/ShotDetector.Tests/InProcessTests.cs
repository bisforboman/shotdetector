using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ShotDetector;
using ShotDetector.FastYuv;
using FieldOrder = FrameReader.FieldOrder;
using StreamType = FrameReader.StreamType;

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
        Assert.True(inProcess.Video.DecodedInProcess);
        Assert.False(pipe.Video.DecodedInProcess);
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

    // Chroma siting (Matroska keeps it): 10-bit conversion interpolates chroma, from where the frame says it sits, as
    // OpenCV's sws_scale_frame does; the executable path is told with in_chroma_loc (ffmpeg's command line drops it).
    [InlineData("10bit-left", "-pix_fmt yuv420p10le -chroma_sample_location left")]
    [InlineData("10bit-topleft", "-pix_fmt yuv420p10le -chroma_sample_location topleft")]
    [InlineData("422-10bit-left", "-pix_fmt yuv422p10le -chroma_sample_location left")]

    // ... and where h264's VUI (the frames') says otherwise than Matroska's header (the stream's): the frames' (libx264).
    [InlineData("10bit-h264-vui-topleft", "-pix_fmt yuv420p10le -chroma_sample_location left -c:v libx264 -x264-params chromaloc=2")]
    [InlineData("oddheight", "-vf crop=320:237:0:0 -pix_fmt yuv420p")]
    public void SameAsTheFfmpegExecutableForEachFormat(string name, string args)
    {
        if (Libs is null)
            return;
        string copy = Path.Combine(Path.GetTempPath(), $"shotdetector-{name}-{Guid.NewGuid():N}.mkv");
        bool encoder = args.Contains("-c:v", StringComparison.Ordinal);
        if (encoder && !VideoReader.Run("ffmpeg", ["-hide_banner", "-encoders"], default).Contains(" libx264 ", StringComparison.Ordinal))
            return;
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, .. args.Split(' '), .. encoder ? [] : new[] { "-c:v", "ffv1" }, copy], default);
        try
        {
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var pipe = ShotDetection.Detect(copy, options);
            var inProcess = ShotDetection.Detect(copy, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodedInProcess);
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

    // The first frame's chroma location and colour tags are read only where something uses them: FastYuv, and the
    // executable's scale filter (10-bit, where swscale interpolates the chroma), not the in-process default path, which
    // converts each frame with its own. A chunk's reader shares the probe's read. (Their values on each path:
    // SameAsTheFfmpegExecutableForEachFormat's 10bit-h264-vui-topleft, FastYuvTests' 420p10-vui-topleft.)
    [Fact]
    public void ReadsTheFirstFramesTagsOnlyWhereTheyreUsed()
    {
        if (Libs is null)
            return;
        string clip = Path.Combine(Path.GetTempPath(), $"shotdetector-tags-{Guid.NewGuid():N}.mkv");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "30", "-pix_fmt", "yuv420p10le", "-chroma_sample_location", "left", "-c:v", "ffv1", clip], default);
        try
        {
            var options = new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess };
            var inProcess = new VideoReader(clip, options);
            Assert.Equal(30, inProcess.Frames().Count());
            Assert.True(inProcess.DecodesInProcess);
            Assert.False(inProcess.FrameTagsRead);

            var executable = new VideoReader(clip, options with { Decoder = VideoDecoder.FfmpegProcess });
            Assert.False(executable.FrameTagsRead);
            Assert.Equal(30, executable.Frames().Count());
            Assert.True(executable.FrameTagsRead);

            var fast = new VideoReader(clip, options with { Yuv420Converter = new SwscaleYuv420() });
            Assert.False(fast.FrameTagsRead);
            Assert.Equal(FramePipeline.Yuv420P10Sampled, fast.Pipeline);
            Assert.True(fast.FrameTagsRead);
            Assert.Equal(30, fast.Frames().Count());
            Assert.True(new VideoReader(clip, options with { Yuv420Converter = new SwscaleYuv420() }, fast.Probed!, default).FrameTagsRead);
            Assert.False(new VideoReader(clip, options, inProcess.Probed!, default).FrameTagsRead);
        }
        finally
        {
            File.Delete(clip);
        }
    }

    [Fact]
    public void AutoFindsTheFramesTheLibrariesCannotDecode()
    {
        // AV1: libraries without dav1d (ShotDetector.Native before 0.10, many system builds) have only the native av1
        // decoder, which needs a GPU and gives no frames; Auto must fall back to the executable rather than give an
        // empty scene list. With libraries that do decode it, it stays in-process.
        if (Libs is null)
            return;
        string av1 = Path.Combine(Path.GetTempPath(), $"shotdetector-av1-{Guid.NewGuid():N}.mkv");
        try
        {
            try
            {
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "40", av1], default);
            }
            catch (ShotDetectionException)
            {
                return;
            } // this ffmpeg can't encode AV1
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
            "rotated" => clips.Rotated,
            "mkv" => clips.ThreeShotsMkv,
            "faststart" => clips.ThreeShotsFaststart,
            "mpegps" => clips.ThreeShotsMpegPs,
            "dropped" => clips.ThreeShotsDropped,
            "long" => clips.Long,
            "interlaced" => clips.Interlaced,
            _ => clips.ThreeShots,
        };
        string ffprobe = Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var expected = Parse(VideoReader.Run(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries",
                VideoReader.ProbeEntries + ":packet=pts", "-of", "default=nw=1", path],
            default));
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
                if (line[..eq] == "pts")
                    pts.Add(line[(eq + 1)..]);
                else
                    values[line[..eq]] = line[(eq + 1)..];
            }

            return (values, pts);
        }

        static List<long> FrameTimes(string text)
        {
            var result = new List<long>();
            long? framePts = null;
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("pts="))
                {
                    framePts = long.TryParse(line[4..], out long p) && p != 0 ? p : null;
                }
                else if (line.StartsWith("pkt_dts="))
                {
                    result.Add(framePts ?? (long.TryParse(line[8..], out long d) ? d : 0));
                    framePts = null;
                }
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
    // sampled rows, from the start and after a seek, 8-bit 4:2:0 and 4:2:2, 10-bit 4:2:2, and with FastYuv (only the rows
    // its conversion reads deinterlaced: 4:2:0, 4:2:2, 10-bit 4:2:0, cropped).
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
    [InlineData("422-fastyuv", "-pix_fmt yuv422p", "fastyuv")]
    [InlineData("420-10bit-fastyuv", "-pix_fmt yuv420p10le", "fastyuv")]
    [InlineData("1080i-422-fastyuv", "-t 1 -vf scale=1920:1080,setfield=tff -pix_fmt yuv422p", "fastyuv")]
    [InlineData("1080i-420-fastyuv-crop", "-t 1 -vf scale=1920:1080,setfield=tff -pix_fmt yuv420p", "fastyuv-crop")]
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
                "fastyuv-crop" => options with { Yuv420Converter = new SwscaleYuv420(), Crop = (101, 37, 1501, 899) },
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
            Assert.True(ours.Video.DecodedInProcess);
            Assert.Equal(ffmpegs.Stats!.Csv(ffmpegs.Video.Position), ours.Stats!.Csv(ours.Video.Position));
        }
        finally
        {
            File.Delete(clip);
        }
    }

    // Image sequences: PNG (zlib in our build), JPEG (yuvj420p), at -framerate (FrameRate) or ffmpeg's default 25 fps;
    // AV1 (dav1d in our build).
    [Theory]
    [InlineData("png", "%04d.png", "", 50.0)]
    [InlineData("png-start", "%04d.png", "start", 50.0)]
    [InlineData("png-default-rate", "%04d.png", "", 0.0)]
    [InlineData("jpg", "%04d.jpg", "", 30.0)]
    [InlineData("av1", "av1.mkv", "", 0.0)]
    [InlineData("av1-start", "av1.mkv", "start", 0.0)]
    public void DecodesImageSequencesAndAv1AsTheFfmpegExecutable(string name, string file, string variant, double rate)
    {
        if (Libs is null)
            return;
        string dir = Path.Combine(Path.GetTempPath(), $"shotdetector-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        try
        {
            if (file.EndsWith(".mkv"))
            {
                try
                {
                    VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "40", path], default);
                }
                catch (ShotDetectionException)
                {
                    return;
                } // this ffmpeg can't encode AV1
            }
            else
            {
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, .. file.EndsWith(".jpg") ? new[] { "-q:v", "3" } : [], path], default);
            }

            var options = new DetectionOptions
            {
                Detector = DetectorKind.Content,
                CollectStats = true,
                FfmpegDirectory = Libs,
                Decoder = VideoDecoder.FfmpegProcess,
                FrameRate = rate > 0 ? rate : null,
                StartTime = variant == "start" ? "1.5" : null,
            };
            var pipe = ShotDetection.Detect(path, options);
            var inProcess = ShotDetection.Detect(path, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodedInProcess);
            Assert.Equal(pipe.FrameCount, inProcess.FrameCount);
            Assert.Equal((pipe.Video.FrameRate, pipe.Video.Codec), (inProcess.Video.FrameRate, inProcess.Video.Codec));
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
            Assert.Equal(Shots.Csv(pipe.Shots), Shots.Csv(inProcess.Shots));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A Stream read in-process (a custom AVIOContext): the same frames, times and stats as the executable reading it
    // from a pipe, which match the file's.
    [Theory]
    [InlineData("mkv", "")]
    [InlineData("faststart", "")]
    [InlineData("fragmented", "-movflags frag_keyframe+empty_moov")]
    [InlineData("ts", "-bsf:v dump_extra -f mpegts")]
    [InlineData("ts-offset", "-bsf:v dump_extra -output_ts_offset 10 -f mpegts")] // a stream that starts at 10 s
    [InlineData("small-probe", "")]
    public void StreamsAsTheFfmpegExecutable(string name, string remux)
    {
        if (Libs is null)
            return;
        string path = name switch
        {
            "mkv" => clips.ThreeShotsMkv,
            "faststart" or "small-probe" => clips.ThreeShotsFaststart,
            _ => Path.Combine(Path.GetTempPath(), $"shotdetector-stream-{name}-{Guid.NewGuid():N}" + (remux.Contains("mpegts") ? ".ts" : ".mp4")),
        };
        try
        {
            if (remux.Length > 0)
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-c", "copy", .. remux.Split(' '), path], default);
            var options = new DetectionOptions
            {
                Detector = DetectorKind.Content,
                CollectStats = true,
                FfmpegDirectory = Libs,
                Decoder = VideoDecoder.FfmpegProcess,
                ProbeBytes = name == "small-probe" ? 4096 : 16 << 20,
            };
            DetectionResult Piped(DetectionOptions o)
            {
                using var stream = File.OpenRead(path);
                return ShotDetection.Detect(stream, o);
            }

            var pipe = Piped(options);
            var inProcess = Piped(options with { Decoder = VideoDecoder.InProcess });
            Assert.False(pipe.Video.DecodedInProcess);
            Assert.True(inProcess.Video.DecodedInProcess);
            Assert.True(inProcess.Video.Streamed);
            Assert.Equal(pipe.FrameCount, inProcess.FrameCount);
            Assert.Equal((pipe.Video.ProcessedWidth, pipe.Video.ProcessedHeight, pipe.Video.FrameRate), (inProcess.Video.ProcessedWidth, inProcess.Video.ProcessedHeight, inProcess.Video.FrameRate));
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
            Assert.Equal(Shots.Csv(pipe.Shots), Shots.Csv(inProcess.Shots));
        }
        finally
        {
            if (path.Contains("shotdetector-stream-"))
                File.Delete(path);
        }
    }

    // Two encodes joined, the second in another pixel format (4:2:2 then 4:2:0, and the reverse), timestamps continuing:
    // OpenCV converts each frame from its own format, so FastYuv (set up for the stream's) leaves the second half's
    // frames to the exact conversion, in-process frame by frame and through the executable by reading again on a BGR
    // pipeline. The first half's frames still take FastYuv's.
    [Theory]
    [InlineData("yuv422p", "yuv420p")]
    [InlineData("yuv420p", "yuv422p")]
    public void PixelFormatChangeTakesTheExactConversion(string first, string second)
    {
        if (Libs is null)
            return;
        string dir = Path.Combine(Path.GetTempPath(), $"shotdetector-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "2", "-pix_fmt", first, "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "2", "-i", clips.ThreeShots, "-t", "2", "-pix_fmt", second, "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", "-output_ts_offset", "2", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, Downscale = 2, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var pipe = ShotDetection.Detect(joined, options);
            Assert.Equal(first, pipe.Video.PixelFormat);
            string expected = pipe.Stats!.Csv(pipe.Video.Position);
            var inProcess = ShotDetection.Detect(joined, options with { Decoder = VideoDecoder.InProcess });
            Assert.Equal(expected, inProcess.Stats!.Csv(inProcess.Video.Position));

            // On ARM FastYuv gives x86's frames and the default path FFmpeg's own (ARM's rounding), so there the two FastYuv
            // runs are compared with each other.
            var fastPipe = ShotDetection.Detect(joined, options with { Yuv420Converter = new SwscaleYuv420() });
            string fastExpected = fastPipe.Stats!.Csv(fastPipe.Video.Position);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                Assert.Equal(expected, fastExpected);
            Assert.True(fastPipe.Video.Reader.Pipeline is FramePipeline.SampledBgr or FramePipeline.FullFrameResize); // read again on a BGR pipeline

            var fast = ShotDetection.Detect(joined, options with { Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
            Assert.True(fast.Video.DecodedInProcess);
            Assert.Equal(fastExpected, fast.Stats!.Csv(fast.Video.Position));
            Assert.Equal(first == "yuv422p" ? FramePipeline.Yuv422Sampled : FramePipeline.Yuv420Sampled, fast.Video.Reader.Pipeline);
            Assert.Equal(50, fast.Video.Reader.BgrFallbackFrames); // the second half's 2 s at 25 fps
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Two encodes joined (tools/make-broken.py's res_change.ts in small): the second half at another size, its
    // timestamps starting over. Frames are labelled by time as scenedetect labels them, so the second half's cuts come
    // out of order (sorted at the end) and its stats rows replace the first half's; its frames are scaled to the first
    // size in one conversion. Both paths give the same.
    [Fact]
    public void JoinedEncodesWithTimestampsStartingOver()
    {
        if (Libs is null)
            return;
        string dir = Path.Combine(Path.GetTempPath(), $"shotdetector-joined-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");

            // The halves as separate encodes, each starting at the same timestamp; the second's shots cut at other times.
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "4", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "1", "-i", clips.ThreeShots, "-vf", "scale=240:180", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var pipe = ShotDetection.Detect(joined, options);
            var inProcess = ShotDetection.Detect(joined, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodedInProcess);
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
            Assert.Equal(Shots.Csv(pipe.Shots), Shots.Csv(inProcess.Shots));

            // FastYuv: the second half's frames (another size) take the exact conversion, as OpenCV scales them in it;
            // in-process, and through the executable (which reads again on a BGR pipeline), from the start and from
            // inside the second half.
            var fast = ShotDetection.Detect(joined, options with { Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
            Assert.Equal(pipe.Stats.Csv(pipe.Video.Position), fast.Stats!.Csv(fast.Video.Position));
            var fastPipe = ShotDetection.Detect(joined, options with { Yuv420Converter = new SwscaleYuv420() });
            Assert.Equal(pipe.Stats.Csv(pipe.Video.Position), fastPipe.Stats!.Csv(fastPipe.Video.Position));
            var late = options with { StartTime = "3s" };
            var lateCore = ShotDetection.Detect(joined, late);
            var lateFast = ShotDetection.Detect(joined, late with { Yuv420Converter = new SwscaleYuv420() });
            Assert.Equal(lateCore.Stats!.Csv(lateCore.Video.Position), lateFast.Stats!.Csv(lateFast.Video.Position));

            // In-process from there too: the first frame read is the second half's (another size), and still scaled to
            // the stream's size, as the executable's scale= does (it once sized the buffer by that frame and crashed).
            var lateInProcess = ShotDetection.Detect(joined, late with { Decoder = VideoDecoder.InProcess });
            Assert.True(lateInProcess.Video.DecodedInProcess);
            Assert.Equal(lateCore.Stats.Csv(lateCore.Video.Position), lateInProcess.Stats!.Csv(lateInProcess.Video.Position));
            var lateFastInProcess = ShotDetection.Detect(joined, late with { Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
            Assert.Equal(lateCore.Stats.Csv(lateCore.Video.Position), lateFastInProcess.Stats!.Csv(lateFastInProcess.Video.Position));
            var halfCore = ShotDetection.Detect(joined, options with { Downscale = 2 });
            var halfFast = ShotDetection.Detect(joined, options with { Downscale = 2, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() });
            Assert.Equal(halfCore.Stats!.Csv(halfCore.Video.Position), halfFast.Stats!.Csv(halfFast.Video.Position));

            // Cuts from both halves by their time labels, sorted (scenedetect's res_change.ts result is checked in CI).
            Assert.Equal([50L, 75L, 100L], pipe.Cuts.Select(c => c.FrameNum));

            // One stats row per frame number: the second half (5 s, numbers 0-124 by time) reuses all of the first half's
            // (0-99); frame 0 has no score; plus the header.
            Assert.Equal(125, pipe.Stats.Csv(pipe.Video.Position).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Display matrices as OpenCV applies them (VideoReader.Turn): the angle alone, turning the converted image, so flips
    // only count as far as they change the angle; deinterlaced first, then turned; every pixel format.
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
    [InlineData("270-10bit", "-pix_fmt yuv420p10le", "-display_rotation -90", "")]
    [InlineData("90-422-10bit", "-pix_fmt yuv422p10le", "-display_rotation 90", "")]
    [InlineData("180-422", "-pix_fmt yuv422p", "-display_rotation 180", "")]
    [InlineData("90-start", "-pix_fmt yuv420p", "-display_rotation 90", "start")]
    [InlineData("90-full", "-pix_fmt yuv420p", "-display_rotation 90", "full")]
    [InlineData("90-deinterlace", "-pix_fmt yuv420p -vf setfield=tff", "-display_rotation 90", "deinterlace")]
    [InlineData("90-422", "-pix_fmt yuv422p", "-display_rotation 90", "")]
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
                "deinterlace" => options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() },
                _ => options,
            };
            var pipe = ShotDetection.Detect(rotated, options);
            var inProcess = ShotDetection.Detect(rotated, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodedInProcess);
            Assert.Equal((pipe.Video.ProcessedWidth, pipe.Video.ProcessedHeight), (inProcess.Video.ProcessedWidth, inProcess.Video.ProcessedHeight));
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A rotated video whose frames grow mid-stream (a larger second encode joined on): every frame is turned at the
    // first frame's size, which the converter keeps, as through the executable. Turning at the current frame's size read
    // past the converter's buffer.
    [Fact]
    public void RotatedVideoGrowingMidStream()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-rot-grow-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts"), rotated = Path.Combine(dir, "rotated.mov");
            string[] h264 = ["-c:v", "libx264", "-bf", "0", "-bsf:v", "h264_mp4toannexb", "-muxdelay", "0", "-muxpreload", "0"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "100", .. h264, a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "4", "-i", clips.ThreeShots, "-vf", "scale=640:480", .. h264, "-output_ts_offset", "4", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-display_rotation", "90", "-i", joined, "-c", "copy", rotated], default);
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var pipe = ShotDetection.Detect(rotated, options);
            var inProcess = ShotDetection.Detect(rotated, options with { Decoder = VideoDecoder.InProcess });
            Assert.True(inProcess.Video.DecodedInProcess);
            Assert.Equal(pipe.Stats!.Csv(pipe.Video.Position), inProcess.Stats!.Csv(inProcess.Video.Position));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Row-wise deinterlacing (Yadif) reads each frame at the first frame's size: an interlaced stream that shrinks
    // mid-way is an error, not a read past the smaller frame.
    [Fact]
    public void RowDeinterlacingRefusesAShrinkingStream()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-yadif-shrink-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            string[] mpeg2 = ["-c:v", "mpeg2video", "-flags", "+ilme+ildct", "-muxdelay", "0", "-muxpreload", "0"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.Interlaced, "-t", "1", .. mpeg2, a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "1", "-i", clips.Interlaced, "-t", "1", "-vf", "scale=iw:ih/2,setfield=tff", .. mpeg2, "-output_ts_offset", "1", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            var options = new DetectionOptions { Detector = DetectorKind.Content, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() };
            Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(joined, options));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A stream claiming frames over the cap (8192 x 8192) fails at the probe, before any buffer is sized from it.
    [Theory]
    [InlineData(VideoDecoder.InProcess)]
    [InlineData(VideoDecoder.FfmpegProcess)]
    public void FramesOverTheSizeCapFailAtTheProbe(VideoDecoder decoder)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-huge-").FullName;
        try
        {
            string huge = Path.Combine(dir, "huge.mkv");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=s=8200x8200:d=0.04", "-pix_fmt", "gray", "-c:v", "png", huge], default);
            var e = Assert.Throws<ShotDetectionException>(() => ShotDetection.Detect(huge, new DetectionOptions { FfmpegDirectory = Libs, Decoder = decoder }));
            Assert.Equal(ShotDetectionError.InvalidInput, e.Reason);
            Assert.Contains("8192", e.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // FrameReader's DecodedFrame with row-wise deinterlacing (Yadif): a taken frame has every row computed, and keeps
    // them after later frames reuse the decoder's output buffer, converted by a converter of its own as the decoder's
    // own frame was.
    [Fact]
    public void TakenFramesKeepTheirDeinterlacedRows()
    {
        if (Libs is null)
            return;
        var expected = new List<byte[]>();
        var taken = new List<FrameReader.DecodedFrame>();
        int width = 0, height = 0;
        using (var decoder = new FrameReader.FrameDecoder(clips.Interlaced, new FrameReader.FrameDecoderOptions { Deinterlace = true, RowDeinterlacer = new Yadif() }))
        {
            using var own = new FrameReader.BgrConverter(decoder);
            for (int n = 0; n < 30 && decoder.Next(); n++)
            {
                var bgr = own.Convert(out int stride);
                (width, height) = (decoder.Width, decoder.Height);
                var packed = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                    bgr.Slice(y * stride, width * 3).CopyTo(packed.AsSpan(y * width * 3));
                expected.Add(packed);
                taken.Add(decoder.TakeFrame());
            }
        }

        Assert.Equal(30, taken.Count);
        using var converter = new FrameReader.BgrConverter();
        for (int i = 0; i < taken.Count; i++)
        {
            using var frame = taken[i];
            var bgr = converter.Convert(frame, out int stride);
            for (int y = 0; y < height; y++)
                Assert.True(bgr.Slice(y * stride, width * 3).SequenceEqual(expected[i].AsSpan(y * width * 3, width * 3)), $"frame {i}, row {y}");
        }
    }

    // FrameReader refuses an input option no demuxer or protocol takes, as ffmpeg's command line does; the options
    // ShotDetector passes for a file and for an image sequence are all taken (http(s) and HLS: VideoTests'
    // UrlInputIsStreamed, FrameReader's tests).
    [Fact]
    public void UnknownInputOptionsFailAndShotDetectorsAreTaken()
    {
        if (Libs is null)
            return;
        FrameReader.FFmpegLibraries.Load(Libs);
        var misspelt = new Dictionary<string, string> { ["framerat"] = "25" };
        var e = Assert.Throws<ArgumentException>(() => new FrameReader.FrameDecoder(clips.ThreeShots, new FrameReader.FrameDecoderOptions { InputOptions = misspelt }).Dispose());
        Assert.Contains("\"framerat\"", e.Message, StringComparison.Ordinal);
        using var decoder = new FrameReader.FrameDecoder(clips.ThreeShots, new FrameReader.FrameDecoderOptions { InputOptions = InProcessProbe.Options(VideoReader.InputOptions(clips.ThreeShots)) });
        Assert.True(decoder.Next());
    }

    // VideoInfo.Streams: every stream with type, codec, tag, language, title, flags, bit rate, duration and audio details,
    // the same in-process as from ffprobe (issue #81).
    [Theory]
    [InlineData("tracks.mkv")]
    [InlineData("tracks.mp4")]
    public void StreamsAreFfprobes(string name)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-streams-").FullName;
        try
        {
            string srt = Path.Combine(dir, "subs.srt"), path = Path.Combine(dir, name);
            File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nHej\n");
            string[] args = name.EndsWith(".mkv")
                ? ["-f", "lavfi", "-i", "testsrc2=s=320x240:d=2", "-f", "lavfi", "-i", "sine=d=2", "-f", "lavfi", "-i", "sine=f=300:d=2", "-i", srt,
                   "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "mpeg4", "-c:a:0", "aac", "-ac:a:0", "2", "-c:a:1", "ac3", "-ac:a:1", "6",
                   "-c:s", "srt", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=swe", "-metadata:s:a:1", "title=Director's commentary",
                   "-metadata:s:s:0", "language=swe", "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "forced"]
                : ["-f", "lavfi", "-i", "testsrc2=s=320x240:d=2", "-f", "lavfi", "-i", "sine=d=2", "-c:v", "mpeg4", "-c:a", "aac", "-ac", "2"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. args, path], default);
            var inProcess = ShotDetection.Probe(path, new DetectionOptions { FfmpegDirectory = Libs }).Streams!;
            var ffprobe = ShotDetection.Probe(path, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess }).Streams!;
            Assert.Equal(ffprobe, inProcess);
            if (name.EndsWith(".mkv"))
            {
                Assert.Equal([StreamType.Video, StreamType.Audio, StreamType.Audio, StreamType.Subtitle], inProcess.Select(s => s.Type));
                Assert.Equal(("eng", true, 2), (inProcess[1].Language, inProcess[1].IsDefault, inProcess[1].Channels));
                Assert.Equal(("swe", "Director's commentary", false, 6, "5.1(side)"),
                    (inProcess[2].Language, inProcess[2].Title, inProcess[2].IsDefault, inProcess[2].Channels, inProcess[2].ChannelLayout));
                Assert.Equal(("subrip", true), (inProcess[3].Codec, inProcess[3].IsForced));
            }
            else
            {
                Assert.Equal(("mp4v", "mp4a", 44100), (inProcess[0].CodecTag, inProcess[1].CodecTag, inProcess[1].SampleRate));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Converting on a thread of its own (ConvertThreads 1: the decoding thread takes each frame, one conversion thread
    // writes it) gives the frames, stats and shots of converting on the decoding thread, on every pipeline: the sampled
    // BGR rows, full frames, ffmpeg's scale, FastYuv's 4:2:0, 4:2:2 and 10-bit planes, crop, a seek, rotation, FFmpeg's
    // yadif, MPEG-PS, a stream, chunks, and frames of another size (the exact conversion on a YUV pipeline). Row-wise
    // yadif keeps converting on the decoding thread: taking a frame would deinterlace all its rows.
    [Theory]
    [InlineData("plain")]
    [InlineData("full")]
    [InlineData("scale")]
    [InlineData("crop")]
    [InlineData("start")]
    [InlineData("fastyuv")]
    [InlineData("422-fastyuv")]
    [InlineData("10bit")]
    [InlineData("10bit-fastyuv")]
    [InlineData("rotate")]
    [InlineData("rotate-fastyuv")]
    [InlineData("deinterlace")]
    [InlineData("deinterlace-422-fastyuv")]
    [InlineData("row-yadif")]
    [InlineData("mpegps")]
    [InlineData("stream")]
    [InlineData("chunks")]
    [InlineData("res-change")]
    [InlineData("res-change-fastyuv")]
    public void ConvertThreadGivesTheSameFrames(string name)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-convert-").FullName;
        try
        {
            string path = name switch
            {
                "mpegps" => clips.ThreeShotsMpegPs,
                "stream" => clips.ThreeShotsMkv,
                "deinterlace" or "row-yadif" => clips.Interlaced,
                _ => clips.ThreeShots,
            };
            string Copy(string source, params string[] args)
            {
                string copy = Path.Combine(dir, $"copy{Directory.GetFiles(dir).Length}.mkv");
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", source, .. args, "-c:v", "ffv1", copy], default);
                return copy;
            }

            if (name.StartsWith("422") || name == "deinterlace-422-fastyuv")
                path = Copy(name.StartsWith("deinterlace") ? clips.Interlaced : path, "-pix_fmt", "yuv422p");
            if (name.StartsWith("10bit"))
                path = Copy(path, "-pix_fmt", "yuv420p10le");
            if (name.StartsWith("rotate"))
            {
                string plain = Copy(path, "-pix_fmt", "yuv420p"), rotated = Path.Combine(dir, "rotated.mov");
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated], default);
                path = rotated;
            }

            if (name.StartsWith("res-change"))
            {
                string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts");
                path = Path.Combine(dir, "joined.ts");
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "4", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", a], default);
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "1", "-i", clips.ThreeShots, "-vf", "scale=240:180", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb", b], default);
                File.WriteAllBytes(path, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            }

            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Mode = DetectionMode.Sequential };
            if (name.Contains("fastyuv"))
                options = options with { Yuv420Converter = new SwscaleYuv420() };
            options = name switch
            {
                "full" => options with { FullFrames = true },
                "scale" => options with { Downscale = 1 },
                "crop" => options with { Crop = (10, 20, 300, 200) },
                "start" => options with { StartTime = "1.5" },
                "deinterlace" or "deinterlace-422-fastyuv" => options with { Deinterlace = DeinterlaceMode.On },
                "row-yadif" => options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() },
                "chunks" => options with { Mode = DetectionMode.Parallel, Chunks = 2 },
                _ => options,
            };
            var inline = options with { ConvertThreads = 0 };
            var apart = options with { ConvertThreads = 1 };
            bool threaded = name != "row-yadif";

            DetectionResult Run(DetectionOptions o)
            {
                if (name != "stream")
                    return ShotDetection.Detect(path, o);
                using var stream = File.OpenRead(path);
                return ShotDetection.Detect(stream, o);
            }

            // Stats and shots, through Detect.
            var expected = Run(inline);
            var actual = Run(apart);
            Assert.True(actual.Video.DecodedInProcess);
            Assert.Equal(expected.Stats!.Csv(expected.Video.Position), actual.Stats!.Csv(actual.Video.Position));
            Assert.Equal(Shots.Csv(expected.Shots), Shots.Csv(actual.Shots));
            if (name == "chunks")
            {
                Assert.Equal(2, actual.Video.Chunks);
                return;
            }

            Assert.False(expected.Video.Reader.ConvertedOnThread);
            Assert.Equal(threaded, actual.Video.Reader.ConvertedOnThread);
            Assert.Equal(expected.Video.Reader.BgrFallbackFrames, actual.Video.Reader.BgrFallbackFrames);
            Assert.Equal(expected.Video.Reader.FirstFrameSize, actual.Video.Reader.FirstFrameSize);
            if (name == "res-change-fastyuv")
                Assert.True(actual.Video.Reader.BgrFallbackFrames > 0);
            if (name == "stream")
            {
                Assert.True(actual.Video.Streamed);
                return;
            }

            // Every frame's bytes, through the reader (from frame 37 for the seek).
            List<string> Hashes(DetectionOptions o)
            {
                var reader = new VideoReader(path, o);
                var frames = name == "start" ? reader.Frames(37, null) : reader.Frames();
                List<string> hashes = [.. frames.Select(f => Convert.ToHexString(SHA256.HashData(f)))];
                Assert.Equal(o.ConvertThreads > 0 && threaded, reader.ConvertedOnThread);
                return hashes;
            }

            var inlineHashes = Hashes(inline);
            Assert.NotEmpty(inlineHashes);
            Assert.Equal(inlineHashes, Hashes(apart));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ConvertThreads unset, video smaller than HD converts on the decoding thread (a conversion thread gained 1.00-1.03x
    // at 360p); set to 1, it converts apart (ConvertThreadStopsCleanly's first read).
    [Fact]
    public void SmallVideoConvertsOnTheDecodingThreadByDefault()
    {
        if (Libs is null)
            return;
        var reader = new VideoReader(clips.ThreeShots, new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess });
        Assert.NotEmpty(reader.Frames());
        Assert.False(reader.ConvertedOnThread);
    }

    // The default's size rule: at least 1280 x 720's pixels, on the coded size, so portrait HD (or HD stored sideways
    // with a rotation tag) counts and 1280 x 534 scope doesn't.
    [Theory]
    [InlineData(1280, 720, 1)]
    [InlineData(720, 1280, 1)]
    [InlineData(1920, 1080, 1)]
    [InlineData(3840, 2160, 1)]
    [InlineData(1279, 720, 0)]
    [InlineData(1280, 534, 0)]
    [InlineData(640, 360, 0)]
    public void DefaultConvertThreadsFromHd(int width, int height, int expected) =>
        Assert.Equal(expected, VideoReader.DefaultConvertThreads(width, height));

    // A parallel run's chunks convert on their decoding threads unless ConvertThreads is set (the chunks keep the cores
    // busy already); the options it doesn't touch are the run's.
    [Fact]
    public void ChunksConvertOnTheirDecodingThreadsByDefault()
    {
        var o = new DetectionOptions { Mode = DetectionMode.Parallel };
        Assert.Null(o.ConvertThreads);
        var chunk = ShotDetection.ChunkOptions(o, 8, 2, 0);
        Assert.Equal(0, chunk.ConvertThreads);
        Assert.Equal(4, chunk.DecodeThreads);
        Assert.Equal(ShotDetection.ChunkBuffers(2), chunk.FrameBuffers);
        Assert.Equal(1, ShotDetection.ChunkOptions(o with { ConvertThreads = 1 }, 8, 2, 1).ConvertThreads);
        Assert.Equal(0, ShotDetection.ChunkOptions(o with { ConvertThreads = 0 }, 8, 2, 1).ConvertThreads);
    }

    // HD read sequentially converts on a thread of its own by default (ConvertThreads unset), a file and a stream alike,
    // with the shots and stats of converting on the decoding thread; explicit 0 keeps it there, and row-wise yadif too.
    [Fact]
    public void HdConvertsOnAThreadOfItsOwnByDefault()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-convert-hd-").FullName;
        try
        {
            string path = Path.Combine(dir, "hd.mkv");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "3", "-vf", "scale=1280:720", "-c:v", "ffv1", path], default);
            var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Mode = DetectionMode.Sequential };
            Assert.Null(options.ConvertThreads);

            var inline = ShotDetection.Detect(path, options with { ConvertThreads = 0 });
            var byDefault = ShotDetection.Detect(path, options);
            Assert.True(byDefault.Video.DecodedInProcess);
            Assert.Equal(1280, byDefault.Video.Width);
            Assert.False(inline.Video.Reader.ConvertedOnThread);
            Assert.True(byDefault.Video.Reader.ConvertedOnThread);
            Assert.NotEmpty(byDefault.Shots.Skip(1));
            Assert.Equal(Shots.Csv(inline.Shots), Shots.Csv(byDefault.Shots));
            Assert.Equal(inline.Stats!.Csv(inline.Video.Position), byDefault.Stats!.Csv(byDefault.Video.Position));

            using (var stream = File.OpenRead(path))
            {
                var streamed = ShotDetection.Detect(stream, options);
                Assert.True(streamed.Video.Streamed);
                Assert.True(streamed.Video.Reader.ConvertedOnThread);
                Assert.Equal(Shots.Csv(inline.Shots), Shots.Csv(streamed.Shots));
            }

            var rowYadif = new VideoReader(path, options with { Deinterlace = DeinterlaceMode.On, Yadif = new Yadif() });
            Assert.NotEmpty(rowYadif.Frames());
            Assert.False(rowYadif.ConvertedOnThread);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Converting on its own thread, the reader stops cleanly: the caller cancelling midway, the caller stopping early,
    // and a stream failing midway (the decoding error, as when converting on the decoding thread). Every taken frame
    // is let go (a leak would show as the run never ending, not here; the native frames are FFmpeg's).
    [Fact]
    public void ConvertThreadStopsCleanly()
    {
        if (Libs is null)
            return;
        var options = new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, ConvertThreads = 1 };
        var reader = new VideoReader(clips.ThreeShots, options);
        using (var cancel = new CancellationTokenSource())
        {
            int n = 0;
            var run = Task.Run(() =>
            {
                foreach (var frame in reader.Frames(cancel.Token))
                {
                    if (++n == 10)
                        cancel.Cancel();
                }
            });
            Assert.True(((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60)), "hung after cancelling");
            Assert.ThrowsAny<OperationCanceledException>(() => run.GetAwaiter().GetResult());
            Assert.True(reader.ConvertedOnThread);
        }

        var early = Task.Run(() => reader.Frames().Take(5).Count());
        Assert.True(((IAsyncResult)early).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60)), "hung after stopping early");
        Assert.Equal(5, early.Result);

        // A stream that fails two thirds of the way in: the same error, after the same frames.
        (int Frames, Exception? Error) Failing(int convertThreads)
        {
            var bytes = File.ReadAllBytes(clips.ThreeShotsMkv);
            using var stream = new FailingStream(bytes, bytes.Length * 2 / 3);
            int frames = 0;
            try
            {
                var failing = new VideoReader(stream, options with { ConvertThreads = convertThreads, ProbeBytes = 4096 });
                foreach (var frame in failing.Frames())
                    frames++;
                return (frames, null);
            }
            catch (ShotDetectionException e)
            {
                return (frames, e);
            }
        }

        var (inlineFrames, inlineError) = Failing(0);
        var (apartFrames, apartError) = Failing(1);
        Assert.NotNull(inlineError);
        Assert.True(inlineFrames > 0);
        Assert.Equal(inlineFrames, apartFrames);
        Assert.Equal(inlineError.Message, apartError?.Message);
    }

    sealed class FailingStream(byte[] bytes, int failAt) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAt)
                throw new IOException("The connection dropped.");
            return base.Read(buffer, offset, (int)Math.Min(count, failAt - Position));
        }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
