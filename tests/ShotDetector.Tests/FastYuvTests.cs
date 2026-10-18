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
                Assert.True(fast.Pipeline is FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled);
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
