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

            // A frame at a time (IYuv420P10FrameConverter): every pixel, and some rows at some columns, packed.
            var frameConverter = (IYuv420P10FrameConverter)converter;
            var whole = new byte[width * height * 3];
            frameConverter.FrameToBgr(yuv, width, height, Enumerable.Range(0, height).ToArray(), all, whole);
            Assert.Equal(expected, whole);
            int[] rows = Enumerable.Range(0, height).Where(y => y % 3 != 1).ToArray();
            var packed = new byte[rows.Length * even.Length * 3];
            frameConverter.FrameToBgr(yuv, width, height, rows, even, packed);
            for (int i = 0; i < rows.Length; i++)
            {
                for (int j = 0; j < even.Length; j++)
                    Assert.Equal(expected.AsSpan((rows[i] * width + even[j]) * 3, 3), packed.AsSpan((i * even.Length + j) * 3, 3));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The 10-bit resize a frame at a time (each chroma row filtered once) against row by row: the same bytes, on any
    // CPU, for each chroma siting, odd heights, turned or not, cropped, in row chunks on several cores (400 x 380), and
    // again with the plan kept for the next frame.
    [Theory]
    [InlineData(64, 47, null)]
    [InlineData(70, 21, "left")]
    [InlineData(40, 26, "center")]
    [InlineData(66, 34, "topleft")]
    [InlineData(62, 31, "top")]
    [InlineData(48, 30, "bottomleft")]
    [InlineData(856, 11, "bottom")]
    public void TenBitFrameAtATimeEqualsRowByRow(int width, int height, string? chromaLocation)
    {
        var yuv = new byte[IYuv420P10Converter.FrameSize(width, height)];
        var samples = MemoryMarshal.Cast<byte, ushort>(yuv.AsSpan());
        var random = new Random(width * height);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (ushort)(i % 7 == 0 ? random.Next(2) * 1023 : random.Next(1024));
        var converter = (IYuv420P10FrameConverter)((IYuv420P10Converter)new SwscaleYuv420()).ForVideo("bt709", false, chromaLocation, width, height)!;
        var rowByRow = new YuvConversion(converter.RowToBgr);
        var frameAtATime = new YuvConversion(converter.RowToBgr, converter);
        foreach (int turn in new[] { 0, 90, 180, 270 })
        {
            int tw = turn is 90 or 270 ? height : width, th = turn is 90 or 270 ? width : height;
            foreach (var (cropX, cropY, cw, ch) in new[] { (0, 0, tw, th), (3, 2, tw - 4, th - 3) })
            {
                foreach (var (dw, dh) in new[] { (7, 9), (Math.Max(cw / 3, 1), Math.Max(ch / 3, 1)), (cw * 2, ch + 5), (400, 380) })
                {
                    byte[] expected = new byte[dw * dh * 3], actual = new byte[dw * dh * 3];
                    new CvResize(cw, ch, dw, dh).ResizeYuv(yuv, expected, rowByRow, width, height, turn, cropX, cropY);
                    var resize = new CvResize(cw, ch, dw, dh);
                    resize.ResizeYuv(yuv, actual, frameAtATime, width, height, turn, cropX, cropY);
                    Assert.Equal(expected, actual);
                    Array.Clear(actual);
                    resize.ResizeYuv(yuv, actual, frameAtATime, width, height, turn, cropX, cropY);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    // The pipelines take a 10-bit converter's frame-at-a-time conversion when it has one (one call per frame), through
    // both decoders, with the same frames.
    [Fact]
    public void PipelinesConvertTenBitAFrameAtATime()
    {
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-frame-").FullName;
        try
        {
            string video = Path.Combine(dir, "video.mkv");
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "12", "-pix_fmt", "yuv420p10le", "-c:v", "ffv1", video], default);
            var options = new DetectionOptions { FfmpegDirectory = Libs };
            foreach (var decoder in Libs is null ? [VideoDecoder.FfmpegProcess] : new[] { VideoDecoder.FfmpegProcess, VideoDecoder.InProcess })
            {
                var counting = new CountingTenBit();
                var reader = new VideoReader(video, options with { Decoder = decoder, Yuv420Converter = counting });
                Assert.Equal(FramePipeline.Yuv420P10Sampled, reader.Pipeline);
                var frames = reader.Frames().Select(f => Convert.ToHexString(SHA256.HashData(f))).ToList();
                Assert.Equal(12, frames.Count);
                Assert.Equal(12, counting.Frames);
                Assert.Equal(0, counting.Rows);
                var plain = new VideoReader(video, options with { Decoder = decoder, Yuv420Converter = new SwscaleYuv420() });
                Assert.Equal(frames, plain.Frames().Select(f => Convert.ToHexString(SHA256.HashData(f))).ToList());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>FastYuv's converter, counting its 10-bit converters' calls: frames, and single rows.</summary>
    sealed class CountingTenBit : IYuv420Converter, IYuv420P10Converter
    {
        readonly SwscaleYuv420 _fast = new();
        public int Frames, Rows;

        public IYuv420Converter? ForColor(string colorSpace, bool fullRange) => _fast.ForColor(colorSpace, fullRange);

        public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow) =>
            _fast.RowToBgr(yuv, width, height, y, cols, bgrRow);

        public IYuv420P10Converter? ForVideo(string colorSpace, bool fullRange, string? chromaLocation, int width, int height) =>
            ((IYuv420P10Converter)_fast).ForVideo(colorSpace, fullRange, chromaLocation, width, height) is IYuv420P10FrameConverter c
                ? new Counted(c, this)
                : null;

        sealed class Counted(IYuv420P10FrameConverter converter, CountingTenBit counts) : IYuv420P10FrameConverter
        {
            public IYuv420P10Converter? ForVideo(string colorSpace, bool fullRange, string? chromaLocation, int width, int height) =>
                counts.ForVideo(colorSpace, fullRange, chromaLocation, width, height);

            public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow)
            {
                Interlocked.Increment(ref counts.Rows);
                converter.RowToBgr(yuv, width, height, y, cols, bgrRow);
            }

            public void FrameToBgr(ReadOnlySpan<byte> yuv, int width, int height, ReadOnlySpan<int> rows, ReadOnlySpan<int> cols, Span<byte> bgr)
            {
                Interlocked.Increment(ref counts.Frames);
                converter.FrameToBgr(yuv, width, height, rows, cols, bgr);
            }
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

    // In-process, only the rows the YUV pipelines read are taken from the decoder (FrameDecoder.CopyRows): the luma
    // rows the resize converts and the chroma rows the converter says those read (IYuvChromaRows). Every other row of
    // the frame overwritten with noise (in the pipelines: an earlier frame's rows) changes nothing, for each layout,
    // size, turn, crop and output size; and those rows are fewer than the frame's where the resize skips some.
    [Theory]
    [InlineData("420", 64, 48, null)]
    [InlineData("420", 37, 22, null)]
    [InlineData("422", 64, 48, null)]
    [InlineData("422", 37, 23, null)]
    [InlineData("420p10", 64, 48, null)]
    [InlineData("420p10", 70, 21, "left")]
    [InlineData("420p10", 66, 34, "topleft")]
    [InlineData("420p10", 62, 31, "top")]
    [InlineData("420p10", 48, 30, "bottomleft")]
    public void RowsOutsideTheRowsReadChangeNothing(string layout, int width, int height, string? chromaLocation)
    {
        bool chroma422 = layout == "422", tenBit = layout == "420p10";
        int chromaHeight = chroma422 ? height : (height + 1) / 2, chromaWidth = (width + 1) / 2, bytes = tenBit ? 2 : 1;
        var yuv = new byte[tenBit ? IYuv420P10Converter.FrameSize(width, height) : chroma422 ? IYuv422Converter.FrameSize(width, height) : IYuv420Converter.FrameSize(width, height)];
        var random = new Random(width * height + layout.Length);
        random.NextBytes(yuv);
        if (tenBit)
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(yuv.AsSpan());
            for (int i = 0; i < samples.Length; i++)
                samples[i] &= 1023;
        }

        var fast = new SwscaleYuv420();
        YuvConversion conversion;
        IYuvChromaRows chromaRows;
        if (tenBit)
        {
            var c10 = (IYuv420P10FrameConverter)((IYuv420P10Converter)fast).ForVideo("bt709", false, chromaLocation, width, height)!;
            (conversion, chromaRows) = (new YuvConversion(c10.RowToBgr, c10), (IYuvChromaRows)c10);
        }
        else if (chroma422)
        {
            var c422 = ((IYuv422Converter)fast).ForColor("bt709", false)!;
            (conversion, chromaRows) = (new YuvConversion(c422.RowToBgr), (IYuvChromaRows)c422);
        }
        else
        {
            var c420 = fast.ForColor("bt709", false)!;
            (conversion, chromaRows) = (new YuvConversion(c420.RowToBgr), (IYuvChromaRows)c420);
        }

        bool fewer = false;
        foreach (int turn in new[] { 0, 90, 180, 270 })
        {
            int tw = turn is 90 or 270 ? height : width, th = turn is 90 or 270 ? width : height;
            foreach (var (cropX, cropY, cw, ch) in new[] { (0, 0, tw, th), (3, 2, tw - 4, th - 3) })
            {
                foreach (var (dw, dh) in new[] { (7, 9), (Math.Max(cw / 3, 1), Math.Max(ch / 3, 1)), (cw * 2, ch + 5), (400, 380) })
                {
                    byte[] expected = new byte[dw * dh * 3], actual = new byte[dw * dh * 3];
                    new CvResize(cw, ch, dw, dh).ResizeYuv(yuv, expected, conversion, width, height, turn, cropX, cropY);
                    var resize = new CvResize(cw, ch, dw, dh);
                    var (luma, chroma) = VideoReader.YuvRowsRead(resize, chromaRows, width, height, chroma422, turn, cropX, cropY);
                    fewer |= luma.Length < height || chroma.Length < chromaHeight;

                    // Noise everywhere but the rows read (the planes packed: Y, then U, then V).
                    var poisoned = new byte[yuv.Length];
                    random.NextBytes(poisoned);
                    int lumaRow = width * bytes, chromaRow = chromaWidth * bytes, u = width * height * bytes, v = u + chromaWidth * chromaHeight * bytes;
                    foreach (int y in luma)
                        yuv.AsSpan(y * lumaRow, lumaRow).CopyTo(poisoned.AsSpan(y * lumaRow));
                    foreach (int y in chroma)
                    {
                        yuv.AsSpan(u + y * chromaRow, chromaRow).CopyTo(poisoned.AsSpan(u + y * chromaRow));
                        yuv.AsSpan(v + y * chromaRow, chromaRow).CopyTo(poisoned.AsSpan(v + y * chromaRow));
                    }

                    resize.ResizeYuv(poisoned, actual, conversion, width, height, turn, cropX, cropY);
                    Assert.True(expected.AsSpan().SequenceEqual(actual), $"turn {turn}, crop ({cropX}, {cropY}), {dw}x{dh}");
                }
            }
        }

        Assert.True(fewer);
    }

    // A converter that doesn't say which chroma rows it reads (IYuvChromaRows) gets whole planes.
    [Fact]
    public void ConvertersThatDoNotSayWhichRowsGetWholePlanes()
    {
        var resize = new CvResize(320, 240, 32, 24);
        var said = new VideoReader(clips.ThreeShots, new DetectionOptions { Yuv420Converter = new SwscaleYuv420() });
        var unsaid = new VideoReader(clips.ThreeShots, new DetectionOptions { Yuv420Converter = new Unsaid() });
        Assert.Equal(FramePipeline.Yuv420Sampled, unsaid.Pipeline);
        Assert.NotNull(said.YuvRowsRead(resize));
        Assert.Null(unsaid.YuvRowsRead(resize));
    }

    /// <summary>FastYuv's 4:2:0 converter, without saying which rows it reads.</summary>
    sealed class Unsaid : IYuv420Converter
    {
        readonly SwscaleYuv420 _fast = new();

        public IYuv420Converter? ForColor(string colorSpace, bool fullRange) => _fast.ForColor(colorSpace, fullRange) is { } c ? new Wrapped(c) : null;

        public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow) =>
            _fast.RowToBgr(yuv, width, height, y, cols, bgrRow);

        sealed class Wrapped(IYuv420Converter converter) : IYuv420Converter
        {
            public IYuv420Converter? ForColor(string colorSpace, bool fullRange) => converter.ForColor(colorSpace, fullRange);

            public void RowToBgr(ReadOnlySpan<byte> yuv, int width, int height, int y, ReadOnlySpan<int> cols, Span<byte> bgrRow) =>
                converter.RowToBgr(yuv, width, height, y, cols, bgrRow);
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

    // The codec's tags (h264's VUI) against Matroska's header: the frames carry the codec's, which the default path and
    // OpenCV convert with, so FastYuv takes the first frame's, not the stream's (libx264; skipped without it).
    [InlineData("420p10-vui-topleft", "-pix_fmt yuv420p10le -chroma_sample_location left -c:v libx264 -x264-params chromaloc=2", "", "", "Yuv420P10Sampled")]
    [InlineData("420-vui-matrix", "-pix_fmt yuv420p -colorspace bt709 -c:v libx264 -x264-params colormatrix=smpte170m", "", "", "Yuv420Sampled")]
    [InlineData("420-vui-range", "-pix_fmt yuv420p -color_range pc -c:v libx264 -bsf:v h264_metadata=video_full_range_flag=0", "", "", "Yuv420Sampled")]
    public void SameFramesAsTheDefaultPath(string name, string format, string display, string variant, string pipeline)
    {
        string dir = Directory.CreateTempSubdirectory($"shotdetector-fastyuv-{name}-").FullName;
        try
        {
            string plain = Path.Combine(dir, "plain.mkv"), video = Path.Combine(dir, "video.mov");
            bool encoder = format.Contains("-c:v", StringComparison.Ordinal);
            if (encoder && !VideoReader.Run("ffmpeg", ["-hide_banner", "-encoders"], default).Contains(" libx264 ", StringComparison.Ordinal))
                return;
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "60", .. format.Split(' '), .. encoder ? [] : new[] { "-c:v", "ffv1" }, plain], default);
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

                // The executable's scale filter was told the first frame's siting (the codec's, not the container's), so
                // it never had to read again from a change.
                Assert.Equal(0, core.SitingRestarts);

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

    // A colour matrix, range or (10-bit) chroma siting that changes midway (two h264 streams joined), through the
    // executable: its raw-YUV pipe passes the second half's samples through untouched (ffmpeg would convert them to the
    // first half's tags), and FastYuv converts each frame with its own tags, read from showinfo (the siting from the
    // frame's line, the matrix and range from the line after it), as OpenCV does: the halves give the frames each half
    // gives alone (and, on x64, the default path's; a siting change not yet there, ffmpeg's scale filter takes one
    // siting). libx264; skipped without it.
    [Theory]
    [InlineData("yuv420p", "colormatrix=bt470bg", "colormatrix=bt709", "", "Yuv420Sampled")]
    [InlineData("yuv420p", "colormatrix=bt470bg", "colormatrix=bt470bg:fullrange=on", "scale=out_range=full", "Yuv420Sampled")]
    [InlineData("yuv420p10le", "colormatrix=bt470bg:chromaloc=0", "colormatrix=bt470bg:chromaloc=2", "", "Yuv420P10Sampled")]
    [InlineData("yuv420p10le", "colormatrix=bt470bg", "colormatrix=bt709", "", "Yuv420P10Sampled")]
    [InlineData("yuv422p", "colormatrix=bt470bg", "colormatrix=bt709", "", "Yuv422Sampled")]
    public void ExecutableFollowsEachFramesTags(string format, string firstTags, string secondTags, string secondFilter, string pipeline)
    {
        if (!VideoReader.Run("ffmpeg", ["-hide_banner", "-encoders"], default).Contains(" libx264 ", StringComparison.Ordinal))
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-tags-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            string[] encode = ["-pix_fmt", format, "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "2", .. encode, "-x264-params", firstTags, a], default);
            string[] filter = secondFilter.Length > 0 ? ["-vf", secondFilter] : [];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "2", "-i", clips.ThreeShots, "-t", "2", .. filter, .. encode, "-x264-params", secondTags, "-output_ts_offset", "2", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

            var options = new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess, Yuv420Converter = new SwscaleYuv420() };
            var halves = Hashes(new VideoReader(a, options)).Concat(Hashes(new VideoReader(b, options))).ToList();
            var reader = new VideoReader(joined, options);
            var frames = Hashes(reader);
            Assert.Equal(halves, frames);
            Assert.Equal(Enum.Parse<FramePipeline>(pipeline), reader.Pipeline); // not read again on a BGR pipeline
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && !secondTags.Contains("chromaloc", StringComparison.Ordinal))
                Assert.Equal(Hashes(new VideoReader(joined, options with { Yuv420Converter = null })), frames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static List<string> Hashes(VideoReader reader) => [.. reader.Frames().Select(f => Convert.ToHexString(SHA256.HashData(f)))];
    }

    // showinfo's per-frame tags, read whole: ffmpeg's other threads can write into the middle of a line (the muxer's dts
    // warning on variable frame rate), and a tag cut short there is the previous frame's, not a name FastYuv declines.
    [Fact]
    public void ShowinfoTagsCutShortAreThePreviousFramesTags()
    {
        const string prefix = "[Parsed_showinfo_0 @ 0x7f2e74001ac0] ";
        const string warning = "Application provided invalid, non monotonically increasing dts to muxer in stream 0: 695 >= 695";
        var frame = new VideoReader.FrameColourTags("bt709", "tv", "left");
        Assert.Equal(frame with { ColorSpace = "bt470bg", ColorRange = "pc" },
            VideoReader.ColourTags(prefix + "color_range:pc color_space:bt470bg color_primaries:unknown color_trc:unknown", frame));
        Assert.Equal(frame with { ColorRange = "pc" }, VideoReader.ColourTags(prefix + "color_range:pc color_space:unknown" + warning, frame));
        Assert.Equal(frame with { ColorRange = "pc" }, VideoReader.ColourTags(prefix + "color_range:pc color_space:bt470" + warning, frame));
        Assert.Null(VideoReader.ColourTags(prefix + "color_range:unknown" + warning, frame));
        Assert.Null(VideoReader.ColourTags(prefix + "n:   1 pts:     42 pts_time:0.042   duration:     41 duration_time:0.041   fmt:yuv420p cl:left sar:0/1 s:854x480 i:P iskey:0 type:B ", frame));

        Assert.Equal("topleft", VideoReader.SitingField(prefix + "n:   2 pts:   9917 fmt:yuv420p10le cl:topleft sar:0/1 s:854x480 i:P "));
        Assert.Null(VideoReader.SitingField(prefix + "n:   2 pts:   9917 fmt:yuv420p10le cl:top" + warning));
    }

    // Through the executable, a frame whose colours FastYuv declines (the GBR matrix) sends the video to the BGR pipelines
    // from there, as a new size does: the default path's frames from that frame on, on any platform (before it,
    // FastYuv's, which are the default path's on x64). libx264; skipped without it.
    [Fact]
    public void ExecutableTakesTheDefaultPathForColoursFastYuvDeclines()
    {
        if (!VideoReader.Run("ffmpeg", ["-hide_banner", "-encoders"], default).Contains(" libx264 ", StringComparison.Ordinal))
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-declined-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            string[] encode = ["-pix_fmt", "yuv420p", "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "2", .. encode, "-x264-params", "colormatrix=bt470bg", a], default);
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "2", "-i", clips.ThreeShots, "-t", "2", .. encode, "-x264-params", "colormatrix=GBR", "-output_ts_offset", "2", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

            var options = new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.FfmpegProcess };
            var reader = new VideoReader(joined, options with { Yuv420Converter = new SwscaleYuv420() });
            Assert.True(VideoReader.IsYuv(reader.Pipeline));
            var frames = Hashes(reader);
            Assert.False(VideoReader.IsYuv(reader.Pipeline));
            var expected = Hashes(new VideoReader(joined, options));
            int before = Hashes(new VideoReader(a, options)).Count;
            Assert.Equal(expected[before..], frames[before..]);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                Assert.Equal(expected, frames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static List<string> Hashes(VideoReader reader) => [.. reader.Frames().Select(f => Convert.ToHexString(SHA256.HashData(f)))];
    }

    // In-process, FastYuv converts each frame with its own colour matrix, range and (10-bit) chroma siting, as OpenCV
    // does, not the first frame's: the joined clip's halves give the frames each half gives alone (and, on x64, the
    // default path's), converting on the decoding thread and on a thread of its own.
    [Theory]
    [InlineData("yuv420p", "colormatrix=bt470bg", "colormatrix=bt709", "")]
    [InlineData("yuv420p", "colormatrix=bt470bg", "colormatrix=bt470bg:fullrange=on", "scale=out_range=full")]
    [InlineData("yuv420p10le", "colormatrix=bt470bg:chromaloc=0", "colormatrix=bt470bg:chromaloc=2", "")]
    [InlineData("yuv420p10le", "colormatrix=bt470bg", "colormatrix=bt709", "")]
    [InlineData("yuv422p", "colormatrix=bt470bg", "colormatrix=bt709", "")]
    public void InProcessFollowsEachFramesTags(string format, string firstTags, string secondTags, string secondFilter)
    {
        if (Libs is null || !VideoReader.Run("ffmpeg", ["-hide_banner", "-encoders"], default).Contains(" libx264 ", StringComparison.Ordinal))
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-fastyuv-frame-tags-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), joined = Path.Combine(dir, "joined.ts");
            string[] encode = ["-pix_fmt", format, "-c:v", "libx264", "-bsf:v", "h264_mp4toannexb"];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-t", "2", .. encode, "-x264-params", firstTags, a], default);
            string[] filter = secondFilter.Length > 0 ? ["-vf", secondFilter] : [];
            VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-ss", "2", "-i", clips.ThreeShots, "-t", "2", .. filter, .. encode, "-x264-params", secondTags, "-output_ts_offset", "2", b], default);
            File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

            var options = new DetectionOptions { FfmpegDirectory = Libs, Decoder = VideoDecoder.InProcess, Yuv420Converter = new SwscaleYuv420() };
            var halves = Hashes(new VideoReader(a, options)).Concat(Hashes(new VideoReader(b, options))).ToList();
            foreach (int threads in new[] { 0, 1 })
            {
                var reader = new VideoReader(joined, options with { ConvertThreads = threads });
                Assert.True(VideoReader.IsYuv(reader.Pipeline));
                var frames = Hashes(reader);
                Assert.Equal(halves, frames);
                Assert.Equal(0, reader.BgrFallbackFrames);
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    Assert.Equal(Hashes(new VideoReader(joined, options with { Yuv420Converter = null, ConvertThreads = threads })), frames);
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
