using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// ShotDetector.FastYuv on 4:2:2 (IYuv422Converter) and on turned frames: the same bytes as swscale on x86, and the same
/// frames as the default path (ffmpeg's or BgrConverter's conversion) for every layout, size and turn the pipelines
/// take it for (docs/decisions/2026-10-18-fastyuv-422.md).
/// </summary>
public class FastYuvTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    // 4:2:2: each row has its own chroma (2x2 frame, U and V one per row). Row 1 takes the second U and V, where
    // 4:2:0 would take the first; expected values as in Yuv420Tests.MatchesSwscale.
    [Fact]
    public void Yuv422ReadsTheChromaOfItsOwnRow()
    {
        byte[] yuv = [81, 81, 145, 145, 90, 54, 240, 34];
        IYuv422Converter converter = new SwscaleYuv420();
        var row0 = new byte[6];
        var row1 = new byte[6];
        converter.RowToBgr(yuv, 2, 2, 0, [0, 1], row0);
        converter.RowToBgr(yuv, 2, 2, 1, [0, 1], row1);
        Assert.Equal([0, 0, 253, 0, 0, 253], row0);
        Assert.Equal([0, 254, 0, 0, 254, 0], row1);
        Assert.Equal(8, IYuv422Converter.FrameSize(2, 2));
        Assert.Equal(3 * 2 + 2 * 2 * 2, IYuv422Converter.FrameSize(3, 2));
        Assert.Null(converter.ForColor("ycgco", false));
    }

    // Noise frames converted by the ffmpeg executable (scale=flags=bicubic,format=bgr24, as VideoReader asks) on x64,
    // where it runs ff_yuv_420_bgr24_ssse3 for both layouts: every pixel. Odd widths take the same converter. (Other
    // CPUs round differently; x64 is the reference.)
    [Theory]
    [InlineData("yuv422p", 64, 48, "")]
    [InlineData("yuv422p", 63, 48, "")]
    [InlineData("yuv422p", 37, 6, "bt709")]
    [InlineData("yuvj422p", 64, 48, "")]
    [InlineData("yuv420p", 63, 48, "")]
    public void ConvertsAsSwscaleOnX64(string pixelFormat, int width, int height, string colorSpace)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        bool is422 = pixelFormat.Contains("422");
        var yuv = new byte[is422 ? IYuv422Converter.FrameSize(width, height) : IYuv420Converter.FrameSize(width, height)];
        new Random(width * height).NextBytes(yuv);
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.yuv"), output = Path.Combine(dir, "out.bgr");
            File.WriteAllBytes(input, yuv);
            string[] tags = colorSpace.Length > 0 ? ["-colorspace", colorSpace] : [];
            string[] args = ["-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", pixelFormat, "-s", $"{width}x{height}", .. tags, "-i", input];
            VideoReader.Run("ffmpeg", [.. args, "-vf", $"scale={width}:{height}:flags=bicubic,format=bgr24", "-f", "rawvideo", output], default);
            byte[] expected = File.ReadAllBytes(output), actual = new byte[width * height * 3];
            int[] all = Enumerable.Range(0, width).ToArray();
            string cs = colorSpace.Length > 0 ? colorSpace : "unknown";
            bool full = pixelFormat.StartsWith("yuvj");
            for (int y = 0; y < height; y++)
            {
                var row = actual.AsSpan(y * width * 3, width * 3);
                if (is422)
                    ((IYuv422Converter)new SwscaleYuv420()).ForColor(cs, full)!.RowToBgr(yuv, width, height, y, all, row);
                else
                    new SwscaleYuv420().ForColor(cs, full)!.RowToBgr(yuv, width, height, y, all, row);
            }

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // 10-bit 4:2:0 frames converted by the ffmpeg executable as VideoReader asks (bicubic, the chroma location given
    // with in_chroma_loc, the colour tags set on the frames) on x64, where swscale scales yuv420p10le with its MMXEXT
    // vertical scaler and output (the C output for the last two rows): every pixel. Noise, and extremes whose bicubic
    // chroma overshoots; widths of each even residue mod 8, odd heights, each chroma siting, matrices and both ranges.
    [Theory]
    [InlineData(64, 48, "left", "", "tv", "noise")]
    [InlineData(66, 34, "left", "bt709", "tv", "extreme")]
    [InlineData(70, 21, "topleft", "", "tv", "noise")]
    [InlineData(68, 17, "center", "bt709", "pc", "extreme")]
    [InlineData(62, 30, "", "bt2020nc", "tv", "extreme")]
    [InlineData(856, 10, "left", "smpte240m", "pc", "noise")]
    [InlineData(40, 26, "bottomleft", "fcc", "tv", "extreme")]
    [InlineData(40, 26, "top", "smpte170m", "tv", "noise")]
    public void Converts10BitAsSwscaleOnX64(int width, int height, string chromaLocation, string colorSpace, string range, string pattern)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var yuv = new byte[IYuv420P10Converter.FrameSize(width, height)];
        var samples = MemoryMarshal.Cast<byte, ushort>(yuv.AsSpan());
        var random = new Random(width * height);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (ushort)(pattern == "noise" ? random.Next(1024) : random.Next(2) * 1023);
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv10-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.yuv"), output = Path.Combine(dir, "out.bgr");
            File.WriteAllBytes(input, yuv);
            string cs = colorSpace.Length > 0 ? colorSpace : "unknown";
            string loc = chromaLocation.Length > 0 ? ":in_chroma_loc=" + chromaLocation : "";
            VideoReader.Run("ffmpeg",
                ["-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p10le", "-s", $"{width}x{height}", "-i", input,
                    "-vf", $"setparams=colorspace={cs}:range={range},scale={width}:{height}:flags=bicubic{loc},format=bgr24", "-f", "rawvideo", output],
                default);
            byte[] expected = File.ReadAllBytes(output), actual = new byte[width * height * 3];
            int[] all = Enumerable.Range(0, width).ToArray();
            var converter = ((IYuv420P10Converter)new SwscaleYuv420()).ForVideo(cs, range == "pc", chromaLocation.Length > 0 ? chromaLocation : null, width, height)!;
            for (int y = 0; y < height; y++)
                converter.RowToBgr(yuv, width, height, y, all, actual.AsSpan(y * width * 3, width * 3));
            Assert.Equal(expected, actual);

            // Some columns only, in two calls per row (the pipelines convert only what the resize reads): the same.
            var some = new byte[width * height * 3];
            int[] even = all.Where(x => x % 3 != 1).ToArray(), odd = all.Where(x => x % 3 == 1).ToArray();
            for (int y = height - 1; y >= 0; y--)
            {
                converter.RowToBgr(yuv, width, height, y, odd, some.AsSpan(y * width * 3, width * 3));
                converter.RowToBgr(yuv, width, height, y, even, some.AsSpan(y * width * 3, width * 3));
            }

            Assert.Equal(expected, some);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // What the 10-bit converter declines, so the default path converts: odd widths (swscale's full chroma
    // interpolation, C code), frames too small for its filters, chroma locations and matrices it doesn't know.
    [Fact]
    public void TenBitDeclinesWhatItDoesNotReproduce()
    {
        IYuv420P10Converter converter = new SwscaleYuv420();
        Assert.NotNull(converter.ForVideo("unknown", false, null, 64, 48));
        Assert.NotNull(converter.ForVideo("bt709", true, "left", 8, 10));
        Assert.Null(converter.ForVideo("unknown", false, null, 63, 48));
        Assert.Null(converter.ForVideo("unknown", false, null, 64, 8)); // a two-tap vertical filter: yuv2packed1/2
        Assert.Null(converter.ForVideo("unknown", false, null, 6, 48)); // chroma narrower than the filter
        Assert.Null(converter.ForVideo("unknown", false, "elsewhere", 64, 48));
        Assert.Null(converter.ForVideo("ycgco", false, null, 64, 48));
        Assert.Equal(2 * (6 + 2 * 2 * 1), IYuv420P10Converter.FrameSize(3, 2));
    }

    // A turned frame: converted where the pixels sit in the decoded frame, then turned, then cropped and resized.
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TurnedYuvResizeEqualsConvertingThenTurning(int turn)
    {
        const int w = 37, h = 22; // odd width
        var yuv = new byte[IYuv422Converter.FrameSize(w, h)];
        new Random(turn).NextBytes(yuv);
        IYuv422Converter converter = new SwscaleYuv420();
        var bgr = new byte[w * h * 3];
        int[] all = Enumerable.Range(0, w).ToArray();
        for (int y = 0; y < h; y++)
            converter.RowToBgr(yuv, w, h, y, all, bgr.AsSpan(y * w * 3, w * 3));

        // Turned, then the crop: 3 columns and 2 rows off its left and top edges, one off the others.
        int tw = turn == 180 ? w : h, th = turn == 180 ? h : w, cw = tw - 4, ch = th - 3;
        var cropped = new byte[cw * ch * 3];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                var (sx, sy) = CvResize.TurnedSource(turn, x + 3, y + 2, w, h);
                bgr.AsSpan((sy * w + sx) * 3, 3).CopyTo(cropped.AsSpan((y * cw + x) * 3));
            }
        }

        foreach (var (dw, dh) in new[] { (7, 9), (cw * 2, ch + 5), (400, 380) }) // the last in row chunks on several cores
        {
            byte[] expected = new byte[dw * dh * 3], actual = new byte[dw * dh * 3];
            new CvResize(cw, ch, dw, dh).Resize(cropped, expected);
            var resize = new CvResize(cw, ch, dw, dh);
            resize.ResizeYuv(yuv, actual, converter.RowToBgr, w, h, turn, 3, 2);
            Assert.Equal(expected, actual);
            Array.Clear(actual);
            resize.ResizeYuv(yuv, actual, converter.RowToBgr, w, h, turn, 3, 2); // the plan kept for the next frame
            Assert.Equal(expected, actual);
        }
    }

    // Whole videos: with FastYuv, the frames the detectors see are the default path's, byte for byte (on x64), through
    // the ffmpeg executable and (with SHOTDETECTOR_FFMPEG_LIBS) in-process; and FastYuv is used where swscale's unscaled
    // converter is (even decoded height), the BGR path elsewhere. Lossless ffv1 copies of the test clip; turned by a
    // display matrix.
    [Theory]
    [InlineData("422", "-pix_fmt yuv422p", "", "", "Yuv422Sampled")]
    [InlineData("yuvj422", "-pix_fmt yuvj422p", "", "", "Yuv422Sampled")]
    [InlineData("422-bt709", "-pix_fmt yuv422p -colorspace bt709", "", "", "Yuv422Sampled")]
    [InlineData("422-oddwidth", "-vf format=yuv444p,crop=319:240:0:0,format=yuv422p", "", "", "Yuv422Sampled")]
    [InlineData("422-oddheight", "-vf format=yuv444p,crop=320:237:0:0,format=yuv422p", "", "", "")]
    [InlineData("422-crop", "-pix_fmt yuv422p", "", "crop", "Yuv422Sampled")]
    [InlineData("422-90", "-pix_fmt yuv422p", "-display_rotation 90", "", "Yuv422Sampled")]
    [InlineData("422-270", "-pix_fmt yuv422p", "-display_rotation -90", "", "Yuv422Sampled")]
    [InlineData("422-180", "-pix_fmt yuv422p", "-display_rotation 180", "", "Yuv422Sampled")]
    [InlineData("422-90-crop", "-pix_fmt yuv422p", "-display_rotation 90", "turned-crop", "Yuv422Sampled")]
    [InlineData("422-270-oddwidth", "-vf format=yuv444p,crop=319:240:0:0,format=yuv422p", "-display_rotation -90", "", "Yuv422Sampled")]
    [InlineData("422-90-oddheight", "-vf format=yuv444p,crop=320:237:0:0,format=yuv422p", "-display_rotation 90", "", "")]
    [InlineData("420-90", "-pix_fmt yuv420p", "-display_rotation 90", "", "Yuv420Sampled")]
    [InlineData("420-90-oddwidth", "-vf format=yuv444p,crop=319:240:0:0,format=yuv420p", "-display_rotation 90", "", "Yuv420Sampled")]
    [InlineData("420-180-oddwidth", "-vf format=yuv444p,crop=319:240:0:0,format=yuv420p", "-display_rotation 180", "", "Yuv420Sampled")]
    [InlineData("420-90-oddheight", "-vf format=yuv444p,crop=320:237:0:0,format=yuv420p", "-display_rotation 90", "", "")]

    // Chroma siting tags (Matroska keeps them): the default path tells swscale where the samples sit (in_chroma_loc,
    // BgrConverter's position), which the unscaled 8-bit converter FastYuv ports doesn't read.
    [InlineData("420-left", "-pix_fmt yuv420p -chroma_sample_location left", "", "", "Yuv420Sampled")]
    [InlineData("420-topleft", "-pix_fmt yuv420p -chroma_sample_location topleft", "", "", "Yuv420Sampled")]
    [InlineData("422-left", "-pix_fmt yuv422p -chroma_sample_location left", "", "", "Yuv422Sampled")]

    // 10-bit 4:2:0: swscale scales it, interpolating chroma from where the tags say it sits; FastYuv reproduces x86's
    // scaled path for even widths (odd heights too), turned or not, the default path takes odd widths.
    [InlineData("420p10", "-pix_fmt yuv420p10le", "", "", "Yuv420P10Sampled")]
    [InlineData("420p10-left", "-pix_fmt yuv420p10le -chroma_sample_location left", "", "", "Yuv420P10Sampled")]
    [InlineData("420p10-topleft-bt709", "-pix_fmt yuv420p10le -chroma_sample_location topleft -colorspace bt709", "", "", "Yuv420P10Sampled")]
    [InlineData("420p10-pc", "-pix_fmt yuv420p10le -color_range pc -chroma_sample_location left", "", "", "Yuv420P10Sampled")]
    [InlineData("420p10-crop", "-pix_fmt yuv420p10le -chroma_sample_location left", "", "crop", "Yuv420P10Sampled")]
    [InlineData("420p10-oddheight", "-vf format=yuv444p,crop=320:237:0:0,format=yuv420p10le -chroma_sample_location left", "", "", "Yuv420P10Sampled")]
    [InlineData("420p10-oddwidth", "-vf format=yuv444p,crop=319:240:0:0,format=yuv420p10le", "", "", "")]
    [InlineData("420p10-90", "-pix_fmt yuv420p10le -chroma_sample_location left", "-display_rotation 90", "", "Yuv420P10Sampled")]
    [InlineData("420p10-180", "-pix_fmt yuv420p10le -chroma_sample_location left", "-display_rotation 180", "", "Yuv420P10Sampled")]
    [InlineData("420p10-270-crop", "-pix_fmt yuv420p10le", "-display_rotation -90", "turned-crop", "Yuv420P10Sampled")]
    public void SameFramesAsTheDefaultPath(string name, string format, string display, string variant, string pipeline)
    {
        string dir = Directory.CreateTempSubdirectory($"shotdetector-fastyuv-{name}-").FullName;
        try
        {
            string plain = Path.Combine(dir, "plain.mkv"), video = Path.Combine(dir, "video.mov");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "60", .. format.Split(' '), "-c:v", "ffv1", plain], default);
            if (display.Length > 0)
                VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. display.Split(' '), "-i", plain, "-c", "copy", video], default);
            else
                video = plain;
            var options = new DetectionOptions { FfmpegDirectory = Libs, Crop = variant switch { "crop" => (10, 21, 300, 200), "turned-crop" => (10, 21, 220, 290), _ => null } };
            foreach (var decoder in Libs is null ? [VideoDecoder.FfmpegProcess] : new[] { VideoDecoder.FfmpegProcess, VideoDecoder.InProcess })
            {
                var core = new VideoReader(video, options with { Decoder = decoder });
                var fast = new VideoReader(video, options with { Decoder = decoder, Yuv420Converter = new SwscaleYuv420() });

                // Empty: the default path's BGR pipeline (swscale's scaled path, odd decoded heights).
                Assert.Equal(pipeline.Length > 0 ? Enum.Parse<FramePipeline>(pipeline) : core.Pipeline, fast.Pipeline);
                Assert.True(core.Pipeline is FramePipeline.SampledBgr or FramePipeline.FullFrameResize);
                Assert.Equal(decoder == VideoDecoder.InProcess, fast.DecodesInProcess);

                // The bytes on x64 only: elsewhere FFmpeg's own conversion rounds differently (ARM: its NEON code), which
                // is what FastYuv is there for; ci.yml's arch-diff compares ARM's FastYuv results with x64's.
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    Assert.Equal(Hashes(core), Hashes(fast));

                // No frame left the fast path: its frames' size and pixel format are the stream's (a false "other
                // format" would read on a BGR pipeline with the same bytes, only slower).
                Assert.Equal(pipeline.Length > 0 ? Enum.Parse<FramePipeline>(pipeline) : core.Pipeline, fast.Pipeline);
                Assert.Equal(0, fast.BgrFallbackFrames);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static List<string> Hashes(VideoReader reader) => [.. reader.Frames().Select(f => Convert.ToHexString(SHA256.HashData(f)))];
    }

    // A turned clip read from a start frame (the exact seek) gives the frames a read from the start gives there, with
    // FastYuv as without, through both decoders.
    [Theory]
    [InlineData("-pix_fmt yuv422p", "-display_rotation 90")]
    [InlineData("-pix_fmt yuv420p", "-display_rotation -90")]
    [InlineData("-pix_fmt yuv420p10le", "-display_rotation 90")]
    public void TurnedYuvSeekGivesTheSameFrames(string format, string display)
    {
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-seek-").FullName;
        try
        {
            string plain = Path.Combine(dir, "plain.mkv"), video = Path.Combine(dir, "video.mov");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "80", .. format.Split(' '), "-c:v", "ffv1", "-g", "10", plain], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", .. display.Split(' '), "-i", plain, "-c", "copy", video], default);
            var options = new DetectionOptions { FfmpegDirectory = Libs };
            const int start = 47;
            foreach (var decoder in Libs is null ? [VideoDecoder.FfmpegProcess] : new[] { VideoDecoder.FfmpegProcess, VideoDecoder.InProcess })
            {
                var all = Hashes(new VideoReader(video, options with { Decoder = decoder, Yuv420Converter = new SwscaleYuv420() }).Frames());
                var fast = new VideoReader(video, options with { Decoder = decoder, Yuv420Converter = new SwscaleYuv420() });
                Assert.True(VideoReader.IsYuv(fast.Pipeline));
                Assert.Equal(all[start..], Hashes(fast.Frames(start, null)));
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    Assert.Equal(Hashes(new VideoReader(video, options with { Decoder = decoder }).Frames(start, null)), Hashes(fast.Frames(start, null)));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static List<string> Hashes(IEnumerable<byte[]> frames) => [.. frames.Select(f => Convert.ToHexString(SHA256.HashData(f)))];
    }
}
