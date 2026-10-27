using System.Diagnostics;
using FrameReader;

/// <summary>
/// ImageEncoder's promise: a frame from VideoFrameReader encodes to the file ffmpeg's command line writes from the same raw
/// frame with its tags (<c>-f rawvideo -pixel_format F -video_size WxH -i frame.raw -vf setparams=...,setsar=...
/// -frames:v 1 out.jpg</c>, and its options), byte for byte with the same libraries; the tags are ffmpeg's frame's after
/// the reader's filters (showinfo). Needs
/// SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg, which has mjpeg and png); without it these
/// do nothing.
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
        Libs is not null && FFmpegLibraries.Missing(format == ImageFormat.Jpg ? Capability.Jpeg : Capability.Png).Count == 0;

    /// <summary>
    /// Whether the byte comparisons can run: the libraries have the encoders and the ffmpeg executable beside them is
    /// their own build (the same configuration: CI's main job puts BtbN's static executable beside our libraries, whose
    /// zlib and encoders needn't write the same bytes). With SHOTDETECTOR_REQUIRE_SAME_BUILD=1 (CI's step with BtbN's
    /// shared build) not being able to is a failure, not a skip.
    /// </summary>
    static bool CanCompare()
    {
        bool can = Usable(ImageFormat.Jpg) && Usable(ImageFormat.Png) && SameBuild();
        if (!can && Environment.GetEnvironmentVariable("SHOTDETECTOR_REQUIRE_SAME_BUILD") == "1")
            Assert.Fail($"SHOTDETECTOR_REQUIRE_SAME_BUILD is set, but {Libs} lacks mjpeg or png, or its ffmpeg isn't the libraries' own build.");
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
    public void ASourcesOwnFrameIsPlainFfmpeg(string ext)
    {
        var imageFormat = ext == "jpg" ? ImageFormat.Jpg : ImageFormat.Png;
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            foreach (var (name, encode) in new[] { ("plain.mkv", "-c:v ffv1 -pix_fmt yuv420p"), ("bt709.mkv", "-vf setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709 -c:v ffv1 -pix_fmt yuv420p") })
            {
                string path = Make(dir, name, encode);
                string file = Path.Combine(dir, "out." + ext);
                Run("-v", "error", "-y", "-i", path, "-frames:v", "1", file);
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
    public void EachFrameOfASequenceIsAOneFrameRun(string ext, int? qscale)
    {
        var imageFormat = ext == "jpg" ? ImageFormat.Jpg : ImageFormat.Png;
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
                    Run(["-v", "error", "-y", "-i", path, "-vf", $"select=eq(n\\,{n}),{scale},format={ffmpegName}", "-frames:v", "1", .. q, file]);
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

    // With whatever libraries these are (no executable of their own build needed): a PNG decodes back to the frame's
    // pixels exactly, a JPEG to its size; one encoder through frames of several sizes and formats.
    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpg)]
    public void WritesWithTheseLibraries(ImageFormat imageFormat)
    {
        if (!Usable(imageFormat))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-img-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new() { Video = ["testsrc2=d=0.2"] });
            using var encoder = new ImageEncoder(new() { Format = imageFormat });
            foreach (var (format, maxWidth) in new[] { (FrameFormat.Rgb24, (int?)null), (FrameFormat.Yuv420p, 100), (FrameFormat.Gray8, 161) })
            {
                using var frames = new VideoFrameReader(path, new() { FrameFormat = format, MaxWidth = maxWidth });
                Assert.True(frames.TryRead(out var frame));
                byte[] file = encoder.Encode(frame);
                byte[] pixels = frame.Data.ToArray();
                using var back = new VideoFrameReader(new MemoryStream(file), new() { FrameFormat = format });
                Assert.True(back.TryRead(out var decoded));
                Assert.Equal((frame.Width, frame.Height), (decoded.Width, decoded.Height));
                if (imageFormat == ImageFormat.Png && format == FrameFormat.Rgb24)
                    Assert.True(decoded.Data.SequenceEqual(pixels), "PNG is lossless");
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
        Assert.Contains("WebP isn't available yet", Assert.Throws<ArgumentException>(() => new ImageEncoder(new() { Format = ImageFormat.WebP })).Message);
    }

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
