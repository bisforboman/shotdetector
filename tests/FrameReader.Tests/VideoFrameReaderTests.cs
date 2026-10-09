using System.Diagnostics;
using FrameReader;

/// <summary>
/// VideoFrameReader's promise: the bytes ffmpeg's command line gives for <c>-vf scale=W:H:flags=bicubic,format=F -f
/// rawvideo</c>, frame for frame. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public class VideoFrameReaderTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

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
                         (FrameFormat.Rgba32, "rgba"), (FrameFormat.Gray8, "gray"), (FrameFormat.Yuv420p, "yuv420p") })
            {
                foreach (var size in new (int W, int H)?[] { null, (161, 91) })
                {
                    string scale = size is { } s ? $"scale={s.W}:{s.H}:flags=bicubic" : "scale=iw:ih:flags=bicubic";
                    byte[] expected = Run("-v", "error", "-i", path, "-vf", $"{scale},format={ffmpegName}", "-f", "rawvideo", "-");
                    var actual = new MemoryStream();
                    int frames = 0;
                    using (var reader = new VideoFrameReader(path, new FrameReaderOptions
                           { Width = size?.W, Height = size?.H, Format = format, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } }))
                        while (reader.TryRead(out var frame))
                        {
                            Assert.Equal(frames++, frame.Index);
                            Assert.Equal((size?.W ?? 320, size?.H ?? 240), (frame.Width, frame.Height));
                            actual.Write(frame.Data);
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
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions { LibraryDirectory = Libs });
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

    static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return i;
        return n;
    }

    [Fact]
    public void FrameTimesStartAtZeroAndSizesMustComeInPairs()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrameReader("x.mp4", new FrameReaderOptions { Width = 100 }));
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-px-").FullName;
        try
        {
            // An MPEG-TS that starts at 10 s: times count from its start, as ffmpeg's do.
            string path = Path.Combine(dir, "late.ts");
            Run("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=25:d=1", "-c:v", "mpeg2video", "-output_ts_offset", "10", path);
            using var reader = new VideoFrameReader(path, new FrameReaderOptions { Format = FrameFormat.Gray8, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } });
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
