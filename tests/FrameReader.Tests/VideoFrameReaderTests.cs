using System.Diagnostics;
using FrameReader;

/// <summary>
/// VideoFrameReader's promise: the bytes ffmpeg's command line gives for <c>-vf scale=W:H:flags=bicubic,format=F -f
/// rawvideo</c>, frame for frame. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public class VideoFrameReaderTests
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

    public static TheoryData<string, string> Sources => new()
    {
        { "plain.mp4", "-c:v mpeg4 -pix_fmt yuv420p" },
        { "bt709.mkv", "-c:v ffv1 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709" },
        { "fullrange.mkv", "-c:v mjpeg -pix_fmt yuvj420p" },
        { "422-10bit.mkv", "-c:v ffv1 -pix_fmt yuv422p10le" },
        { "interlaced.mkv", "-c:v mpeg2video -flags +ilme+ildct" },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void BytesAreFfmpegsScaleAndFormat(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-px-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", .. encode.Split(' '), path]);
            foreach (var (format, ffmpegName) in new[] { (FrameFormat.Bgr24, "bgr24"), (FrameFormat.Rgb24, "rgb24"), (FrameFormat.Bgra32, "bgra"),
                         (FrameFormat.Rgba32, "rgba"), (FrameFormat.Gray8, "gray"), (FrameFormat.Yuv420p, "yuv420p"), })
            {
                foreach (var size in new (int W, int H)?[] { null, (161, 91) })
                {
                    string scale = size is { } s ? $"scale={s.W}:{s.H}:flags=bicubic" : "scale=iw:ih:flags=bicubic";
                    byte[] expected = Run("-v", "error", "-i", path, "-vf", $"{scale},format={ffmpegName}", "-f", "rawvideo", "-");
                    var actual = new MemoryStream();
                    int frames = 0;
                    using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions
                    { Width = size?.W, Height = size?.H, FrameFormat = format, Decoder = new FrameDecoderOptions() }))
                    {
                        while (reader.TryRead(out var frame))
                        {
                            Assert.Equal(frames++, frame.Index);
                            Assert.Equal((size?.W ?? 320, size?.H ?? 240), (frame.Width, frame.Height));
                            actual.Write(frame.Data);
                        }
                    }

                    Assert.Equal(25, frames);
                    Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                        $"{name} {format} {scale}: {expected.Length} bytes from ffmpeg, {actual.Length} read; first difference at {FirstDifference(expected, actual.ToArray())}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Odd sizes (issue #656): a frame whose width or height is odd, at its own size (swscale's unscaled copy for Yuv420p
    // from a 4:2:0 source) and scaled to another odd size, from 4:2:0 and 4:2:2 sources, is ffmpeg's rawvideo byte for
    // byte, the last half-size chroma row and column included.
    [Theory]
    [InlineData(321, 241, "yuv420p")]
    [InlineData(320, 241, "yuv420p")]
    [InlineData(321, 240, "yuv420p")]
    [InlineData(321, 241, "yuvj420p")]
    [InlineData(321, 241, "yuv422p")]
    [InlineData(321, 241, "yuv420p10le")]
    public void OddSizesAreFfmpegs(int w, int h, string pixelFormat)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-odd-").FullName;
        try
        {
            string path = Path.Combine(dir, "odd.nut");
            Run("-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s=320x240:r=25:d=0.4,scale={w}:{h}", "-c:v", "rawvideo", "-pix_fmt", pixelFormat, path);
            foreach (var (format, ffmpegName) in new[] { (FrameFormat.Yuv420p, "yuv420p"), (FrameFormat.Bgr24, "bgr24"), (FrameFormat.Gray8, "gray") })
            {
                foreach (var size in new (int W, int H)?[] { null, (161, 91) })
                {
                    string scale = size is { } s ? $"scale={s.W}:{s.H}:flags=bicubic" : "scale=iw:ih:flags=bicubic";
                    byte[] expected = Run("-v", "error", "-i", path, "-vf", $"{scale},format={ffmpegName}", "-f", "rawvideo", "-");
                    var actual = new MemoryStream();
                    int frames = 0;
                    using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions
                    { Width = size?.W, Height = size?.H, FrameFormat = format, Decoder = new FrameDecoderOptions() }))
                    {
                        while (reader.TryRead(out var frame))
                        {
                            frames++;
                            Assert.Equal((size?.W ?? w, size?.H ?? h), (frame.Width, frame.Height));
                            actual.Write(frame.Data);
                        }
                    }

                    Assert.Equal(10, frames);
                    Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                        $"{w}x{h} {pixelFormat} {format} {scale}: {expected.Length} bytes from ffmpeg, {actual.Length} read; first difference at {FirstDifference(expected, actual.ToArray())}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // MaxWidth: ffmpeg's scale='min(N,iw)':-2, on the frame after autorotate (a 320x240 stream shown at 90 degrees is
    // 240 wide), an odd size, and a height whose -2 rounding is a tie (320x241: 120.5 rounds up); never upscaled, and
    // a height that rounds to 0 (a width of 1) is the input's, as libavfilter fills it in.
    [Theory]
    [InlineData("rotated.mov", 320, 240, "-display_rotation 90", new[] { 101, 160, 240, 1280 })]
    [InlineData("odd.mkv", 321, 241, "", new[] { 1, 200, 321, 1280 })]
    [InlineData("tie.mkv", 320, 241, "", new[] { 99, 320, 1280 })]
    public void MaxWidthIsFfmpegsMinWidthAndEvenHeight(string name, int w, int h, string mux, int[] maxWidths)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-max-").FullName;
        try
        {
            string path = Path.Combine(dir, name), plain = Path.Combine(dir, "plain.mkv");
            Run("-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s=320x240:r=25:d=0.4,scale={w}:{h}", "-c:v", "ffv1", "-pix_fmt", "yuv420p", plain);
            Run(["-v", "error", "-y", .. mux.Split(' ', StringSplitOptions.RemoveEmptyEntries), "-i", plain, "-c", "copy", path]);
            foreach (int max in maxWidths)
            {
                foreach (var (format, ffmpegName) in new[] { (FrameFormat.Bgr24, "bgr24"), (FrameFormat.Yuv420p, "yuv420p") })
                {
                    string vf = $"scale='min({max},iw)':-2:flags=bicubic,format={ffmpegName}";
                    byte[] expected = Run("-v", "error", "-i", path, "-vf", vf, "-f", "rawvideo", "-");
                    string size = OutputSize(path, vf);
                    var actual = new MemoryStream();
                    int frames = 0;
                    using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { MaxWidth = max, FrameFormat = format, Decoder = new FrameDecoderOptions() }))
                    {
                        while (reader.TryRead(out var frame))
                        {
                            frames++;
                            Assert.Equal(size, $"{frame.Width}x{frame.Height}");
                            actual.Write(frame.Data);
                        }
                    }

                    Assert.Equal(10, frames);
                    Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                        $"{name} {format} {vf}: {expected.Length} bytes from ffmpeg, {actual.Length} read; first difference at {FirstDifference(expected, actual.ToArray())}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The size ffmpeg's filters give the first frame (WxH), from ffprobe's view of their output.</summary>
    static string OutputSize(string path, string vf)
    {
        string out1 = Path.Combine(Path.GetDirectoryName(path)!, "size.nut");
        Run("-v", "error", "-y", "-i", path, "-vf", vf, "-frames:v", "1", "-c:v", "rawvideo", out1);
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"))
        { RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", out1 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void BgrConverterIsFfmpegsBgr24WholeOrRows(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-bgr-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", .. encode.Split(' '), path]);
            byte[] expected = Run("-v", "error", "-i", path, "-vf", "scale=iw:ih:flags=bicubic,format=bgr24", "-f", "rawvideo", "-");
            int[] rows = [0, 1, 7, 100, 101, 102, 200, 239];
            const int Row = 320 * 3, Frame = Row * 240;
            int frames = 0;
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions());
            using var whole = new BgrConverter(decoder);
            using var some = new BgrConverter(decoder);
            for (; decoder.Next(); frames++)
            {
                var bgr = whole.Convert(out int stride);
                for (int y = 0; y < 240; y++)
                    Assert.True(bgr.Slice(y * stride, Row).SequenceEqual(expected.AsSpan(frames * Frame + y * Row, Row)), $"{name}: frame {frames}, row {y}");
                var sampled = some.Convert(rows, out stride);
                foreach (int y in rows)
                    Assert.True(sampled.Slice(y * stride, Row).SequenceEqual(expected.AsSpan(frames * Frame + y * Row, Row)), $"{name}: frame {frames}, row {y} alone");
            }

            Assert.Equal(25, frames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // DecodedFrame: every frame taken (a reference, not a copy) and kept past later Next calls and the decoder's
    // Dispose, then converted by a converter of its own: still ffmpeg's bgr24, whole and row by row. Rotated (the
    // decoder's own buffer, which a taken frame must not see rewritten) and a stream growing mid-way (KeepFirstSize)
    // too.
    [Theory]
    [MemberData(nameof(Sources))]
    [InlineData("rotated.mov", "-c:v ffv1 -pix_fmt yuv420p|-display_rotation 90")]
    [InlineData("grows.ts", "-c:v mpeg2video -pix_fmt yuv420p|grow")]
    public void TakenFramesConvertAsTheDecodersOwn(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-taken-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            string[] parts = encode.Split('|');
            string[] codec = parts[0].Split(' ');
            if (parts.Length > 1 && parts[1] == "grow")
            {
                // 320x240, then 480x360: OpenCV (and KeepFirstSize) scale the larger frames to the first size.
                string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts");
                Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.4", .. codec, a]);
                Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=480x360:r=25:d=0.4", .. codec, "-output_ts_offset", "0.4", b]);
                File.WriteAllBytes(path, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            }
            else if (parts.Length > 1)
            {
                string plain = Path.Combine(dir, "plain.mkv");
                Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", .. codec, plain]);
                Run(["-v", "error", "-y", .. parts[1].Split(' '), "-i", plain, "-c", "copy", path]);
            }
            else
            {
                Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", .. codec, path]);
            }

            // The reference: the decoder's own frame converted as it comes.
            var expected = new List<byte[]>();
            var taken = new List<DecodedFrame>();
            int width = 0, height = 0;
            using (var decoder = new FrameDecoder(path, new FrameDecoderOptions()))
            {
                using var own = new BgrConverter(decoder) { KeepFirstSize = true };
                while (decoder.Next())
                {
                    var bgr = own.Convert(out int stride);
                    if (width == 0)
                        (width, height) = (decoder.Width, decoder.Height);
                    var packed = new byte[width * height * 3];
                    for (int y = 0; y < height; y++)
                        bgr.Slice(y * stride, width * 3).CopyTo(packed.AsSpan(y * width * 3));
                    expected.Add(packed);
                    taken.Add(decoder.TakeFrame());
                }
            }

            Assert.True(expected.Count >= 10, $"{name}: {expected.Count} frames");
            int[] rows = [0, 1, 7, height / 2, height - 1];
            using var whole = new BgrConverter { KeepFirstSize = true };
            using var some = new BgrConverter { KeepFirstSize = true };
            for (int i = 0; i < taken.Count; i++)
            {
                using var frame = taken[i];
                var bgr = whole.Convert(frame, out int stride);
                for (int y = 0; y < height; y++)
                    Assert.True(bgr.Slice(y * stride, width * 3).SequenceEqual(expected[i].AsSpan(y * width * 3, width * 3)), $"{name}: frame {i}, row {y}");
                var sampled = some.Convert(frame, rows, out stride);
                foreach (int y in rows)
                    Assert.True(sampled.Slice(y * stride, width * 3).SequenceEqual(expected[i].AsSpan(y * width * 3, width * 3)), $"{name}: frame {i}, row {y} alone");
            }

            Assert.Throws<ObjectDisposedException>(() => taken[0].Width);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Size: every frame at the given size, whichever comes first. From the larger second half on (as after a seek into
    // it), the frames come out as KeepFirstSize gives them when the first size came first.
    [Fact]
    public void SizeScalesEveryFrameToIt()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-size-").FullName;
        try
        {
            string a = Path.Combine(dir, "a.ts"), b = Path.Combine(dir, "b.ts"), path = Path.Combine(dir, "grows.ts");
            Run("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.4", "-c:v", "mpeg2video", a);
            Run("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=480x360:r=25:d=0.4", "-c:v", "mpeg2video", "-output_ts_offset", "0.4", b);
            File.WriteAllBytes(path, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions());
            using var first = new BgrConverter(decoder) { KeepFirstSize = true };
            using var sized = new BgrConverter(decoder) { Size = (320, 240) };
            int larger = 0;
            while (decoder.Next())
            {
                var expected = first.Convert(out int expectedStride).ToArray();
                if (decoder.Width == 320)
                    continue;
                larger++;
                var bgr = sized.Convert(out int stride);
                for (int y = 0; y < 240; y++)
                    Assert.True(bgr.Slice(y * stride, 320 * 3).SequenceEqual(expected.AsSpan(y * expectedStride, 320 * 3)), $"row {y}");
            }

            Assert.True(larger >= 5, $"{larger} larger frames");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BgrConverter { Size = (0, 240) });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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

    // A frame without a timestamp (the last one MPEG-PS and AVI with B-frames flush out): the time ffmpeg's command line
    // makes up for it (the previous frame's plus its duration), for every read, rather than an overflow.
    [Theory]
    [InlineData("ps.mpg", "-c:v mpeg2video -bf 2 -g 12")]
    [InlineData("bframes.avi", "-c:v mpeg4 -bf 2")]
    [InlineData("gap.mpg", "-vf settb=1/100,setpts=N*4+gte(N\\,45)*20 -fps_mode passthrough -enc_time_base 1/100 -c:v mpeg2video -bf 2 -g 12")] // the duration, not the step
    public void AFrameWithoutATimestampGetsFfmpegsTime(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-nopts-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=25:d=2", .. encode.Split(' '), path]);
            var expected = ShowinfoTimes(path);
            var options = new VideoFrameReaderOptions { FrameFormat = FrameFormat.Gray8, Decoder = new FrameDecoderOptions() };
            foreach (int ahead in new[] { 0, 2 })
            {
                var times = new List<double>();
                using (var reader = new VideoFrameReader(path, options with { ReadAhead = ahead }))
                {
                    while (reader.TryRead(out var frame))
                        times.Add(frame.Time.TotalSeconds);
                }

                Assert.Equal(expected.Count, times.Count);
                for (int i = 0; i < times.Count; i++)
                    Assert.True(Math.Abs(expected[i] - times[i]) < 1e-6, $"{name} frame {i}, ReadAhead {ahead}: ffmpeg {expected[i]}, read {times[i]}");
            }

            // Forward (in steps that don't jump) to the last frame's time, and with the frame rate's filter: that frame.
            using (var reader = new VideoFrameReader(path, options))
            {
                for (double t = 0; t < expected[^1] - 0.5; t += 0.5)
                    Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(t), out _));
                Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(expected[^1] - 0.001), out var last));
                Assert.Equal(expected[^1], last.Time.TotalSeconds, 6);
                Assert.False(reader.TryReadForwardTo(TimeSpan.FromSeconds(expected[^1] + 0.02), out _));
            }

            var fps = ShowinfoTimes(path, "fps=25,");
            using (var reader = new VideoFrameReader(path, options with { FrameRate = new Rational(25, 1) }))
            {
                int n = 0;
                while (reader.TryRead(out var frame))
                    Assert.Equal(fps[n++], frame.Time.TotalSeconds, 6);
                Assert.Equal(fps.Count, n);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Each frame's pts_time as ffmpeg's showinfo prints it (after the <paramref name="filters"/>, with a comma).</summary>
    static List<double> ShowinfoTimes(string path, string filters = "")
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-i", path, "-vf", filters + "showinfo", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string log = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, log);
        return [.. System.Text.RegularExpressions.Regex.Matches(log, "pts_time:(-?[0-9.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
    }

    [Fact]
    public void FrameTimesStartAtZeroAndSizesMustComeInPairs()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrameReader("x.mp4", new VideoFrameReaderOptions { Width = 100 }));
        Assert.Throws<ArgumentException>(() => new VideoFrameReader("x.mp4", new VideoFrameReaderOptions { MaxWidth = 100, Width = 100, Height = 50 }));
        Assert.Throws<ArgumentException>(() => new VideoFrameReader("x.mp4", new VideoFrameReaderOptions { MaxWidth = 0 }));
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-px-").FullName;
        try
        {
            // An MPEG-TS that starts at 10 s: times count from its start, as ffmpeg's do.
            string path = Path.Combine(dir, "late.ts");
            Run("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=25:d=1", "-c:v", "mpeg2video", "-output_ts_offset", "10", path);
            using var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { FrameFormat = FrameFormat.Gray8, Decoder = new FrameDecoderOptions() });
            Assert.True(reader.TryRead(out var first));
            Assert.InRange(first.Time.TotalSeconds, 0, 0.1);
            Assert.Equal(160 * 120, first.Data.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
