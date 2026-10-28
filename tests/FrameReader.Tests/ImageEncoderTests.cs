using System.Diagnostics;
using FrameReader;

/// <summary>
/// ImageEncoder's promise: a frame from VideoFrameReader encodes to the file ffmpeg's command line writes from the same raw
/// frame with its tags (<c>-f rawvideo -pixel_format F -video_size WxH -i frame.raw -vf setparams=...,setsar=...
/// -frames:v 1 out.jpg</c>, and its options; WebP with <c>-c:v libwebp</c>, as a build with libwebp_anim picks that one),
/// byte for byte with the same libraries; the tags are ffmpeg's frame's after the reader's filters (showinfo). Needs
/// SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg, which has mjpeg, png and libwebp); without
/// it these do nothing.
/// </summary>
public class ImageEncoderTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Ffmpeg => Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    static byte[] Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return output.ToArray();
    }

    static bool Usable(ImageFormat format) =>
        Libs is not null && FFmpegLibraries.Missing(format switch
        {
            ImageFormat.Jpg => Capability.Jpeg,
            ImageFormat.Png => Capability.Png,
            _ => Capability.WebP,
        }).Count == 0;

    // ffmpeg's encoder for each format: WebP's named, as libwebp_anim (where it's built) comes first for .webp.
    static string[] Codec(ImageFormat format) => format == ImageFormat.WebP ? ["-c:v", "libwebp"] : [];

    /// <summary>
    /// Whether the byte comparisons can run: the libraries have the encoders and the ffmpeg executable beside them is
    /// their own build (the same configuration: CI's main job puts BtbN's static executable beside our libraries, whose
    /// zlib and encoders needn't write the same bytes). With SHOTDETECTOR_REQUIRE_SAME_BUILD=1 (CI's step with BtbN's
    /// shared build) not being able to is a failure, not a skip.
    /// </summary>
    static bool CanCompare()
    {
        bool can = Usable(ImageFormat.Jpg) && Usable(ImageFormat.Png) && Usable(ImageFormat.WebP) && SameBuild();
        if (!can && Environment.GetEnvironmentVariable("SHOTDETECTOR_REQUIRE_SAME_BUILD") == "1")
            Assert.Fail($"SHOTDETECTOR_REQUIRE_SAME_BUILD is set, but {Libs} lacks mjpeg, png or libwebp, or its ffmpeg isn't the libraries' own build.");
        return can;
    }

    static bool SameBuild()
    {
        if (!File.Exists(Ffmpeg))
            return false;
        var psi = new ProcessStartInfo(Ffmpeg, "-hide_banner -version") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string version = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return version.Contains("configuration: " + FFmpeg.AutoGen.ffmpeg.avcodec_configuration().Trim());
    }

    static readonly (FrameFormat Format, string Name)[] Formats =
    [
        (FrameFormat.Bgr24, "bgr24"), (FrameFormat.Rgb24, "rgb24"), (FrameFormat.Bgra32, "bgra"), (FrameFormat.Rgba32, "rgba"),
        (FrameFormat.Gray8, "gray"), (FrameFormat.Yuv420p, "yuv420p"),
    ];

    // Sources whose tags reach the bytes: colour range (the conversion to the encoder's full-range YUV), primaries and
    // transfer (PNG's cHRM and gAMA), the aspect ratio (JFIF's density, PNG's pHYs: the container's over the codec's,
    // swapped by a rotation, changed by scaling to another shape), and 4:2:2 and 10-bit sources.
    public static TheoryData<string, string> Sources => new()
    {
        { "plain.mkv", "-c:v ffv1 -pix_fmt yuv420p" },
        { "bt709.mkv", "-vf setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709 -c:v ffv1 -pix_fmt yuv420p" },
        { "fullrange.mkv", "-c:v mjpeg -pix_fmt yuvj420p" },
        { "422-10bit.mkv", "-c:v ffv1 -pix_fmt yuv422p10le" },
        { "anamorphic.mkv", "-c:v ffv1 -pix_fmt yuv420p -aspect 16:9" },
        { "nosar.mp4", "-c:v mpeg4 -pix_fmt yuv420p -vf setsar=0" },
        { "rotated.mov", "-c:v ffv1 -pix_fmt yuv420p -vf setsar=4/3|-display_rotation 90|" },
        { "remuxed-aspect.mkv", "-c:v mpeg4 -pix_fmt yuv420p||-aspect 16:9" },
    };

    static string Make(string dir, string name, string encode)
    {
        string path = Path.Combine(dir, name);
        string[] parts = encode.Split('|');
        if (parts.Length == 1)
        {
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.2", .. parts[0].Split(' '), path]);
            return path;
        }

        string plain = Path.Combine(dir, "plain-" + Path.GetFileNameWithoutExtension(name) + ".mkv");
        Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.2", .. parts[0].Split(' '), plain]);
        Run(["-v", "error", "-y", .. parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), "-i", plain, "-c", "copy",
            .. parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries), path]);
        return path;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void BytesAreFfmpegsFromTheRawFrame(string name, string encode)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var cases = new (ImageEncoderOptions Options, string Ext, string[] Args)[]
            {
                (new() { Format = ImageFormat.Jpg }, "jpg", []),
                (new() { Format = ImageFormat.Jpg, JpegQscale = 2 }, "jpg", ["-q:v", "2"]),
                (new() { Format = ImageFormat.Png }, "png", []),
                (new() { Format = ImageFormat.Png, PngCompressionLevel = 1 }, "png", ["-compression_level", "1"]),
                (new() { Format = ImageFormat.WebP }, "webp", ["-c:v", "libwebp"]),
                (new() { Format = ImageFormat.WebP, WebPQuality = 90.5f, WebPCompressionLevel = 2 }, "webp", ["-c:v", "libwebp", "-quality", "90.5", "-compression_level", "2"]),
                (new() { Format = ImageFormat.WebP, WebPLossless = true }, "webp", ["-c:v", "libwebp", "-lossless", "1"]),
            };
            var failures = new List<string>();
            foreach (var (format, ffmpegName) in Formats)
            {
                foreach (var size in new[] { "", "w=161:h=91", "w='min(200,iw)':h=-2" })
                {
                    var reader = new VideoFrameReaderOptions
                    {
                        FrameFormat = format,
                        Width = size.StartsWith("w=161", StringComparison.Ordinal) ? 161 : null,
                        Height = size.StartsWith("w=161", StringComparison.Ordinal) ? 91 : null,
                        MaxWidth = size.StartsWith("w='min", StringComparison.Ordinal) ? 200 : null,
                        Decoder = new FrameDecoderOptions(),
                    };
                    string vf = $"scale={(size == "" ? "iw:ih" : size)}:flags=bicubic,format={ffmpegName}";
                    foreach (var (options, ext, args) in cases)
                    {
                        string file = Path.Combine(dir, "out." + ext);
                        using var frames = new VideoFrameReader(path, reader);
                        Assert.True(frames.TryRead(out var frame));
                        string raw = Path.Combine(dir, "frame.raw");
                        File.WriteAllBytes(raw, frame.Data.ToArray());
                        var tags = new List<string>();
                        if (frame.ColorRange is { } r)
                            tags.Add("range=" + r);
                        if (frame.ColorSpace is { } cs)
                            tags.Add("colorspace=" + cs);
                        if (frame.ColorPrimaries is { } cp)
                            tags.Add("color_primaries=" + cp);
                        if (frame.ColorTransfer is { } ct)
                            tags.Add("color_trc=" + ct);
                        string set = (tags.Count > 0 ? "setparams=" + string.Join(":", tags) + "," : "") + $"setsar=r={frame.SampleAspectRatio.Num}/{Math.Max(1, frame.SampleAspectRatio.Den)}:max=2147483647";
                        Run(["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", ffmpegName, "-video_size", $"{frame.Width}x{frame.Height}", "-i", raw, "-vf", set, "-frames:v", "1", .. args, file]);
                        byte[] expected = File.ReadAllBytes(file);
                        using var encoder = new ImageEncoder(options);
                        byte[] actual = encoder.Encode(frame);
                        if (!expected.AsSpan().SequenceEqual(actual))
                            failures.Add($"{name} {format} {vf} {ext} {string.Join(' ', args)}: {expected.Length} bytes from ffmpeg, {actual.Length} encoded; first difference at {FirstDifference(expected, actual)}");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Decoded frames go through ffmpeg's own graph: the file is `ffmpeg -i X [-vf select=eq(n\,K),F] -frames:v 1 out.jpg`'s,
    // F being the filter options' -vf (none, a size, a cap, ShotDetector's save-images chain with two scales), for each
    // format and its options, on sources whose tags, siting, depth, field order and rotation reach the bytes; one encoder
    // through the frames and filters, so its graph and encoder follow each change.
    public static TheoryData<string, string> DecodedSources
    {
        get
        {
            var sources = new TheoryData<string, string>();
            foreach (var row in Sources)
                sources.Add((string)row[0], (string)row[1]);
            sources.Add("420-10bit.mp4", "-c:v libx264 -pix_fmt yuv420p10le");
            sources.Add("left-sited.mp4", "-c:v libx264 -pix_fmt yuv420p -vf scale=out_color_matrix=bt709:out_range=tv,setparams=colorspace=bt709");
            sources.Add("interlaced.mpg", "-c:v mpeg2video -flags +ilme+ildct -top 1 -pix_fmt yuv420p");
            sources.Add("rotated180.mov", "-c:v ffv1 -pix_fmt yuv420p|-display_rotation 180|");
            sources.Add("rotated270.mov", "-c:v ffv1 -pix_fmt yuv420p -aspect 16:9|-display_rotation 270|");
            sources.Add("gray.mkv", "-c:v ffv1 -pix_fmt gray");
            sources.Add("gray10.mkv", "-c:v ffv1 -pix_fmt gray10le");
            sources.Add("rgb48.mkv", "-c:v ffv1 -pix_fmt gbrp12le");
            sources.Add("rgba.mov", "-c:v png -pix_fmt rgba");
            return sources;
        }
    }

    [Theory]
    [MemberData(nameof(DecodedSources))]
    public void DecodedFramesAreFfmpegsOneCommand(string name, string encode)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var cases = new (ImageEncoderOptions Options, string Ext, string[] Args)[]
            {
                (new() { Format = ImageFormat.Jpg }, "jpg", []),
                (new() { Format = ImageFormat.Jpg, JpegQscale = 2 }, "jpg", ["-q:v", "2"]),
                (new() { Format = ImageFormat.Png }, "png", []),
                (new() { Format = ImageFormat.Png, PngCompressionLevel = 3 }, "png", ["-compression_level", "3"]),
                (new() { Format = ImageFormat.WebP, WebPQuality = 100 }, "webp", ["-c:v", "libwebp", "-quality", "100"]),
                (new() { Format = ImageFormat.WebP, WebPLossless = true }, "webp", ["-c:v", "libwebp", "-lossless", "1"]),
            };
            var filters = new (ImageFilterOptions? Options, string? Vf)[]
            {
                (null, null),
                (new() { Width = 161, Height = 91 }, "scale=161:91"),
                (new() { MaxWidth = 200 }, "scale='min(200,iw)':-2"),
                (new() { Filter = SaveImagesChain }, SaveImagesChain),
            };
            var failures = new List<string>();
            foreach (var (options, ext, args) in cases)
            {
                using var encoder = new ImageEncoder(options);
                foreach (var (filter, vf) in filters)
                {
                    using var decoder = new FrameDecoder(path);
                    for (int n = 0; n <= 3; n++)
                    {
                        Assert.True(decoder.Next());
                        if (n is not (0 or 3))
                            continue;

                        // The first frame without select, which isn't metadata-only: a graph that leaves the pixels alone
                        // passes the decoder's bits per raw sample to the encoder (PNG's sBIT).
                        string file = Path.Combine(dir, "out." + ext);
                        string? chain = n == 0 ? vf : $"select=eq(n\\,{n})" + (vf is null ? "" : "," + vf);
                        Run(["-v", "error", "-y", "-i", path, .. chain is null ? Array.Empty<string>() : ["-vf", chain], "-frames:v", "1", .. args, file]);
                        byte[] expected = File.ReadAllBytes(file);
                        using var frame = decoder.TakeFrame();
                        byte[] actual = encoder.Encode(frame, filter);
                        if (!expected.AsSpan().SequenceEqual(actual))
                            failures.Add($"{name} frame {n} {vf ?? "(no -vf)"} {ext} {string.Join(' ', args)}: {expected.Length} bytes from ffmpeg, {actual.Length} encoded; first difference at {FirstDifference(expected, actual)}");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // VideoFrameReader's takes (issue #574) through the encoder's graph: each file is ffmpeg's one command for that frame,
    // byte for byte: TryTakeForwardTo's with the output -ss, TryTakeAt's with the input -ss; read ahead or not. A picture
    // every second (the issue's loop: the frame at or after each second) is -vf fps=1:round=up's where frames fall on the
    // seconds, as here; fps=1's default rounding keeps the last frame of each slot instead (0.48 s, 1.48 s, ...: that is
    // FrameRate's, for reads). A source wider than the cap (scaled) and one narrower (kept).
    [Theory]
    [InlineData("wide.mp4", "-c:v mpeg4 -bf 2 -g 12 -q:v 4", "1600x900")]   // indexed: the forward takes jump
    [InlineData("opengop.mkv", "-c:v mpeg2video -bf 2 -g 12", "320x240")]      // open GOPs
    public void ReaderTakesAreFfmpegsOneCommand(string name, string encode, string size)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s={size}:r=25:d=3", .. encode.Split(' '), path]);
            const string Vf = "scale='min(1280,iw)':-2";
            var filter = new ImageFilterOptions { MaxWidth = 1280 };
            using var jpeg = new ImageEncoder(new() { Format = ImageFormat.Jpg, JpegQscale = 2 });
            string file = Path.Combine(dir, "out.jpg");
            byte[] Ffmpeg(string[] seek, string[] input) =>
                (Run(["-v", "error", "-y", .. seek, "-i", path, .. input, "-vf", Vf, "-frames:v", "1", "-q:v", "2", file]), File.ReadAllBytes(file)).Item2;

            // fps=1:round=up's pictures, one file each.
            Run("-v", "error", "-y", "-i", path, "-vf", "fps=1:round=up," + Vf, "-q:v", "2", Path.Combine(dir, "fps_%03d.jpg"));
            var seconds = Directory.GetFiles(dir, "fps_*.jpg").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
            Assert.Equal(3, seconds.Count);

            var failures = new List<string>();
            void Check(string what, byte[] expected, byte[] actual)
            {
                if (!expected.AsSpan().SequenceEqual(actual))
                    failures.Add($"{name} {what}: {expected.Length} bytes from ffmpeg, {actual.Length} encoded; first difference at {FirstDifference(expected, actual)}");
            }

            foreach (int ahead in new[] { 0, 2 })
            {
                using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = ahead }))
                {
                    for (int i = 0; i <= seconds.Count; i++)
                    {
                        bool got = reader.TryTakeForwardTo(TimeSpan.FromSeconds(i), out var frame);
                        Assert.Equal(i < seconds.Count, got);
                        using (frame)
                        {
                            if (got)
                                Check($"ReadAhead {ahead}, fps=1 picture {i}", seconds[i], jpeg.Encode(frame!, filter));
                        }
                    }
                }

                using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = ahead }))
                {
                    foreach (double t in new[] { 0.3, 1.04, 1.5, 2.9 })
                    {
                        string ss = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Assert.True(reader.TryTakeForwardTo(TimeSpan.FromSeconds(t), out var frame));
                        using (frame)
                            Check($"ReadAhead {ahead}, forward to {ss}", Ffmpeg([], ["-ss", ss]), jpeg.Encode(frame, filter));
                    }
                }

                using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = ahead }))
                {
                    foreach (double t in new[] { 2.0, 0.5, 1.04, 0.0 })
                    {
                        string ss = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Assert.True(reader.TryTakeAt(TimeSpan.FromSeconds(t), out var frame));
                        using (frame)
                            Check($"ReadAhead {ahead}, at {ss}", Ffmpeg(["-ss", ss], []), jpeg.Encode(frame, filter));
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // FrameRate's takes (the fps filter's frames, unconverted) through the encoder's graph: the files ffmpeg's one run
    // writes with -vf fps=R[,...] out_%03d.ext, byte for byte, read ahead or not. JPEG at a fixed qscale (ffmpeg's rate
    // control carries over from frame to frame in one run otherwise) with a scale, and PNG without a filter (10-bit too).
    // A source starting at 10 s and an interlaced one deinterlaced (yadif,fps=R).
    [Theory]
    [InlineData("wide.mp4", "-c:v mpeg4 -bf 2 -g 12 -q:v 4", "1600x900", false)]
    [InlineData("late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10", "320x240", false)]
    [InlineData("10bit.mkv", "-c:v ffv1 -pix_fmt yuv420p10le", "320x240", false)]
    [InlineData("interlaced.ts", "-c:v mpeg2video -flags +ilme+ildct -top 1", "320x240", true)]
    public void FrameRateTakesAreFfmpegsFpsFiles(string name, string encode, string size, bool deinterlace)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s={size}:r=25:d=3", .. encode.Split(' '), path]);
            var cases = new (ImageEncoderOptions Options, string Ext, string[] Args, ImageFilterOptions? Filter, string? Vf)[]
            {
                (new() { Format = ImageFormat.Jpg, JpegQscale = 2 }, "jpg", ["-q:v", "2"], new() { MaxWidth = 1280 }, "scale='min(1280,iw)':-2"),
                (new() { Format = ImageFormat.Png }, "png", [], null, null),
            };
            var failures = new List<string>();
            foreach (var rate in new Rational[] { new(1, 1), new(5, 2) })
            {
                foreach (var (options, ext, args, filter, vf) in cases)
                {
                    string prefix = $"fps{rate.Num}_{rate.Den}_{ext}_";
                    string chain = (deinterlace ? "yadif," : "") + $"fps={rate.Num}/{rate.Den}" + (vf is null ? "" : "," + vf);
                    Run(["-v", "error", "-y", "-i", path, "-vf", chain, .. args, Path.Combine(dir, prefix + "%03d." + ext)]);
                    var expected = Directory.GetFiles(dir, prefix + "*").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
                    Assert.NotEmpty(expected);
                    using var encoder = new ImageEncoder(options);
                    foreach (int ahead in new[] { 0, 2 })
                    {
                        using var reader = new VideoFrameReader(path, new VideoFrameReaderOptions
                        {
                            FrameRate = rate,
                            ReadAhead = ahead,
                            Decoder = new FrameDecoderOptions { Deinterlace = deinterlace },
                        });
                        int n = 0;
                        while (reader.TryTake(out var frame))
                        {
                            using (frame)
                            {
                                if (n < expected.Count && !expected[n].AsSpan().SequenceEqual(encoder.Encode(frame, filter)))
                                    failures.Add($"{name} {chain} {ext} ReadAhead {ahead}, file {n + 1}: first difference at {FirstDifference(expected[n], encoder.Encode(frame, filter))}");
                            }

                            n++;
                        }

                        if (n != expected.Count)
                            failures.Add($"{name} {chain} {ext} ReadAhead {ahead}: {n} frames, ffmpeg wrote {expected.Count} files");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ShotDetector's save-images chain: square pixels at ffmpeg's default flags, then a bilinear scale to a width.
    const string SaveImagesChain = "scale='round(iw*sar)':ih,setsar=1,scale=160:'trunc(ih*160/iw)':flags=bilinear";

    [Fact]
    public void RefusesFiltersThatDontGoTogether()
    {
        if (!Usable(ImageFormat.Png))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new() { Video = ["testsrc2=d=0.2"] });
            using var decoder = new FrameDecoder(path);
            Assert.True(decoder.Next());
            using var frame = decoder.TakeFrame();
            using var encoder = new ImageEncoder(new() { Format = ImageFormat.Png });
            foreach (var bad in new ImageFilterOptions[]
            {
                new() { Width = 100 },
                new() { Height = 100 },
                new() { Width = 0, Height = 10 },
                new() { MaxWidth = 0 },
                new() { MaxWidth = 100, Width = 10, Height = 10 },
                new() { Filter = "" },
                new() { Filter = "scale=10:10", MaxWidth = 100 },
                new() { Filter = "scale=10:10", Width = 10, Height = 10 },
                new() { Filter = "nosuchfilter" },
            })
            {
                Assert.Throws<ArgumentException>(() => encoder.Encode(frame, bad));
            }

            // A filter that holds its frame (yadif waits for the next one) gives none for it; the encoder still works afterwards.
            Assert.Throws<FrameReaderException>(() => encoder.Encode(frame, new ImageFilterOptions { Filter = "yadif" }));
            using var back = new VideoFrameReader(new MemoryStream(encoder.Encode(frame, new ImageFilterOptions { MaxWidth = 100 })));
            Assert.True(back.TryRead(out var image));
            Assert.Equal(100, image.Width);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The tags a frame carries are those ffmpeg's frame has after the same filters (showinfo): the aspect ratio (the
    // container's over the codec's, swapped by a rotation, changed by scaling to another shape) and the colour tags.
    [Theory]
    [MemberData(nameof(Sources))]
    public void TagsAreFfmpegs(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var failures = new List<string>();
            foreach (var (format, ffmpegName) in Formats)
            {
                foreach (var (w, h, max, scale) in new (int?, int?, int?, string)[] { (null, null, null, "iw:ih"), (161, 91, null, "161:91"), (null, null, 200, "'min(200,iw)':-2") })
                {
                    var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true };
                    foreach (var a in new[] { "-v", "info", "-i", path, "-vf", $"scale={scale}:flags=bicubic,format={ffmpegName},showinfo", "-frames:v", "1", "-f", "null", "-" })
                        psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    string log = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    string sar = System.Text.RegularExpressions.Regex.Match(log, @" sar:(\d+/\d+) ").Groups[1].Value;
                    string colours = System.Text.RegularExpressions.Regex.Match(log, @"color_range:\S+ color_space:\S+ color_primaries:\S+ color_trc:\S+").Value;
                    using var frames = new VideoFrameReader(path, new() { FrameFormat = format, Width = w, Height = h, MaxWidth = max, Decoder = new FrameDecoderOptions() });
                    Assert.True(frames.TryRead(out var frame));
                    string ours = $"{frame.SampleAspectRatio.Num}/{frame.SampleAspectRatio.Den} color_range:{frame.ColorRange ?? "unknown"} color_space:{frame.ColorSpace ?? "unknown"} color_primaries:{frame.ColorPrimaries ?? "unknown"} color_trc:{frame.ColorTransfer ?? "unknown"}";
                    if (ours != $"{sar} {colours}")
                        failures.Add($"{name} {format} {scale}: ffmpeg {sar} {colours}, ours {ours}");
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The issue's case: the source's own frame (Yuv420p at its size from a 4:2:0 source) is plain `ffmpeg -i X out.jpg`.
    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void ASourcesOwnFrameIsPlainFfmpeg(string ext)
    {
        var imageFormat = Format(ext);
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            foreach (var (name, encode) in new[] { ("plain.mkv", "-c:v ffv1 -pix_fmt yuv420p"), ("bt709.mkv", "-vf setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709 -c:v ffv1 -pix_fmt yuv420p") })
            {
                string path = Make(dir, name, encode);
                string file = Path.Combine(dir, "out." + ext);
                Run(["-v", "error", "-y", "-i", path, "-frames:v", "1", .. Codec(imageFormat), file]);
                using var frames = new VideoFrameReader(path, new() { FrameFormat = FrameFormat.Yuv420p, Decoder = new FrameDecoderOptions() });
                Assert.True(frames.TryRead(out var frame));
                using var encoder = new ImageEncoder(new() { Format = imageFormat });
                var stream = new MemoryStream();
                encoder.Encode(frame, stream);
                Assert.Equal(File.ReadAllBytes(file), stream.ToArray());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // One encoder, many frames: each is the file a one-frame ffmpeg run writes for it (`-vf select=eq(n\,K),scale=...,format=F
    // -frames:v 1 out.jpg`), also at the default JPEG quality, whose rate control would otherwise carry its state over:
    // one ffmpeg run writing the four frames (`out%d.jpg`) differs from its second frame on, which is checked too. Across
    // a change of size and format, the graph and the encoder follow.
    [Theory]
    [InlineData("jpg", null)]
    [InlineData("jpg", 5)]
    [InlineData("png", null)]
    [InlineData("webp", null)]
    public void EachFrameOfASequenceIsAOneFrameRun(string ext, int? qscale)
    {
        var imageFormat = Format(ext);
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Make(dir, "plain.mkv", "-c:v ffv1 -pix_fmt yuv420p");
            string[] q = qscale is { } v ? ["-q:v", v.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
            using var encoder = new ImageEncoder(new() { Format = imageFormat, JpegQscale = qscale });
            foreach (var (format, ffmpegName, maxWidth) in new[] { (FrameFormat.Yuv420p, "yuv420p", (int?)null), (FrameFormat.Bgr24, "bgr24", 100) })
            {
                using var frames = new VideoFrameReader(path, new() { FrameFormat = format, MaxWidth = maxWidth, Decoder = new FrameDecoderOptions() });
                for (int n = 0; n < 4; n++)
                {
                    Assert.True(frames.TryRead(out var frame));
                    string file = Path.Combine(dir, "out." + ext);
                    string scale = maxWidth is { } m ? $"scale='min({m},iw)':-2:flags=bicubic" : "scale=iw:ih:flags=bicubic";
                    Run(["-v", "error", "-y", "-i", path, "-vf", $"select=eq(n\\,{n}),{scale},format={ffmpegName}", "-frames:v", "1", .. q, .. Codec(imageFormat), file]);
                    byte[] ours = encoder.Encode(frame);
                    Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(ours), $"{format} frame {n}");
                    if (format == FrameFormat.Yuv420p && n > 0 && ext == "jpg" && qscale is null)
                    {
                        // One run, four frames: its rate control carried over, unlike a one-frame run's.
                        Run("-v", "error", "-y", "-i", path, "-frames:v", "4", Path.Combine(dir, "run%d.jpg"));
                        Assert.False(File.ReadAllBytes(Path.Combine(dir, $"run{n + 1}.jpg")).AsSpan().SequenceEqual(ours), $"frame {n} of one run");
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // With whatever libraries these are (no executable of their own build needed): a PNG or lossless WebP decodes back
    // to the frame's pixels exactly, a JPEG or lossy WebP to its size; one encoder through frames of several sizes and
    // formats.
    [Theory]
    [InlineData(ImageFormat.Png, false)]
    [InlineData(ImageFormat.Jpg, false)]
    [InlineData(ImageFormat.WebP, false)]
    [InlineData(ImageFormat.WebP, true)]
    public void WritesWithTheseLibraries(ImageFormat imageFormat, bool lossless)
    {
        if (!Usable(imageFormat))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new() { Video = ["testsrc2=d=0.2"] });
            using var encoder = new ImageEncoder(new() { Format = imageFormat, WebPLossless = lossless });
            foreach (var (format, maxWidth) in new[] { (FrameFormat.Rgb24, (int?)null), (FrameFormat.Yuv420p, 100), (FrameFormat.Gray8, 161) })
            {
                using var frames = new VideoFrameReader(path, new() { FrameFormat = format, MaxWidth = maxWidth });
                Assert.True(frames.TryRead(out var frame));
                byte[] file = encoder.Encode(frame);
                byte[] pixels = frame.Data.ToArray();
                using var back = new VideoFrameReader(new MemoryStream(file), new() { FrameFormat = format });
                Assert.True(back.TryRead(out var decoded));
                Assert.Equal((frame.Width, frame.Height), (decoded.Width, decoded.Height));
                if ((imageFormat == ImageFormat.Png || lossless) && format == FrameFormat.Rgb24)
                    Assert.True(decoded.Data.SequenceEqual(pixels), $"{imageFormat} is lossless");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RefusesWhatItCantWrite()
    {
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = (ImageFormat)99 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { JpegQscale = 1 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { JpegQscale = 32 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.Png, JpegQscale = 2 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.Png, PngCompressionLevel = -1 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.Png, PngCompressionLevel = 10 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { PngCompressionLevel = 5 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, JpegQscale = 2 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, PngCompressionLevel = 5 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, WebPQuality = -0.5f }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, WebPQuality = 100.5f }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, WebPQuality = float.NaN }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, WebPCompressionLevel = -1 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP, WebPCompressionLevel = 7 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { WebPQuality = 50 }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.Png, WebPLossless = true }));
        Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { WebPCompressionLevel = 4 }));
    }

    // WebP's edges are accepted (when the libraries have libwebp: the constructor finds the encoder).
    [Fact]
    public void TakesWebPsWholeRanges()
    {
        if (!Usable(ImageFormat.WebP))
            return;
        foreach (var options in new ImageEncoderOptions[]
        {
            new() { Format = ImageFormat.WebP, WebPQuality = 0, WebPCompressionLevel = 0 },
            new() { Format = ImageFormat.WebP, WebPQuality = 100, WebPCompressionLevel = 6, WebPLossless = true },
        })
        {
            using var encoder = new ImageEncoder(options);
        }
    }

    static ImageFormat Format(string ext) => ext switch
    {
        "jpg" => ImageFormat.Jpg,
        "png" => ImageFormat.Png,
        _ => ImageFormat.WebP,
    };

    static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
                return i;
        }

        return n;
    }
}
