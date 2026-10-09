using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// VideoFrameReader's seek against ffmpeg's: the frame <c>ffmpeg -ss T -i X -frames:v 1</c> gives, byte for byte.
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class SeekTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static byte[] Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return output.ToArray();
    }

    [Theory]
    [InlineData("bframes.mp4", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("bframes.mkv", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10")] // starts at 10 s
    public void SeeksToFfmpegsFrame(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seek-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", .. encode.Split(' '), path]);
            using var reader = new VideoFrameReader(path, new FrameReaderOptions
                { Width = 160, Height = 90, Format = FrameFormat.Gray8, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } });
            // From the start, mid-GOP, on frame boundaries (1.04 s is frame 26) and just before them, near and past the end;
            // backwards too, after reading on.
            foreach (double t in new[] { 0, 0.5, 1.04, 1.039, 1.0, 0.999, 2.9, 1.3, 0.1, 3.5 })
            {
                byte[] expected = Ffmpeg("-v", "error", "-ss", t.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1",
                    "-vf", "scale=160:90:flags=bicubic,format=gray", "-f", "rawvideo", "-");
                bool got = reader.TryReadAt(TimeSpan.FromSeconds(t), out var frame);
                Assert.Equal(expected.Length > 0, got);
                if (got)
                {
                    Assert.True(expected.AsSpan().SequenceEqual(frame.Data), $"{name} at {t} s: not ffmpeg's frame (got {frame.Time})");
                    Assert.Equal(0, frame.Index);
                    Assert.True(frame.Time >= TimeSpan.FromSeconds(t) - TimeSpan.FromMilliseconds(1), $"{name} at {t} s: frame at {frame.Time}");
                }
                // Reading on from there gives the next frame.
                if (got && t < 2.5)
                {
                    Assert.True(reader.TryRead(out var next));
                    Assert.Equal(1, next.Index);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("bframes.mp4", "-c:v mpeg4 -bf 2 -g 12")]        // indexed: jumps ahead over GOPs
    [InlineData("opengop.mkv", "-c:v mpeg2video -bf 2 -g 12")]   // open GOPs, indexed: jumps start a keyframe early
    [InlineData("late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10")] // no index: decodes through
    public void ReadsForwardToFfmpegsFrames(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-fwd-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", .. encode.Split(' '), path]);
            using var reader = new VideoFrameReader(path, new FrameReaderOptions
                { Width = 160, Height = 90, Format = FrameFormat.Gray8, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } });
            // One pass: each time the frame ffmpeg's output -ss gives (decoding from the start, dropping the frames before:
            // exact even in open GOPs, where an input -ss can give a B-frame decoded without its reference); the same
            // time twice, the same frame; Index counts the distinct frames given.
            foreach (var (t, index) in new[] { (0.0, 0), (0.3, 1), (1.0, 2), (1.0, 2), (1.039, 3), (1.04, 3), (1.5, 4), (2.04, 5), (2.9, 6) })
            {
                byte[] expected = Ffmpeg("-v", "error", "-i", path, "-ss", t.ToString(CultureInfo.InvariantCulture), "-frames:v", "1",
                    "-vf", "scale=160:90:flags=bicubic,format=gray", "-f", "rawvideo", "-");
                Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(t), out var frame), $"{name} at {t} s");
                Assert.True(expected.AsSpan().SequenceEqual(frame.Data), $"{name} at {t} s: not ffmpeg's frame (got {frame.Time})");
                Assert.Equal(index, frame.Index);
            }
            Assert.False(reader.TryReadForwardTo(TimeSpan.FromSeconds(3.5), out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // FrameRate: the frames of ffmpeg's -vf fps=R (its rounding, duplicates and drops), byte for byte.
    [Theory]
    [InlineData("bframes.mp4", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10")]
    public void FrameRateIsFfmpegsFpsFilter(string name, string encode)
    {
        if (Libs is null || !FFmpegLibraries.CanLoad(Libs))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-fps-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", .. encode.Split(' '), path]);
            foreach (var (rate, text) in new[] { (new Rational(1, 1), "1"), (new Rational(1, 2), "1/2"), (new Rational(10, 1), "10"), (new Rational(30, 1), "30") })
            {
                byte[] expected = Ffmpeg("-v", "error", "-i", path, "-vf", $"fps={text},scale=160:90:flags=bicubic,format=gray", "-f", "rawvideo", "-");
                using var reader = new VideoFrameReader(path, new FrameReaderOptions
                    { Width = 160, Height = 90, Format = FrameFormat.Gray8, FrameRate = rate, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } });
                var actual = new MemoryStream();
                int n = 0;
                while (reader.TryRead(out var frame))
                {
                    Assert.Equal(n++, frame.Index);
                    actual.Write(frame.Data);
                }
                Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                    $"{name} fps={text}: {expected.Length / (160 * 90)} frames from ffmpeg, {actual.Length / (160 * 90)} read");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ReadAt: several readers on contiguous shares of the times give the frames one reader gives, byte for byte.
    [Theory]
    [InlineData("bframes.mp4", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("opengop.mkv", "-c:v mpeg2video -bf 2 -g 12")]
    public void ReadsInParallelAsOneReaderDoes(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-par-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=6", .. encode.Split(' '), path]);
            var options = new FrameReaderOptions { Width = 160, Height = 90, Format = FrameFormat.Gray8, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } };
            var times = Enumerable.Range(0, 30).Select(i => TimeSpan.FromSeconds(i * 0.21)).ToList(); // the last ones past the end
            var expected = new List<byte[]>();
            using (var reader = new VideoFrameReader(path, options))
                foreach (var t in times)
                    if (reader.TryReadForwardTo(t, out var frame))
                        expected.Add(frame.Data.ToArray());
            var actual = new byte[times.Count][];
            int found = VideoFrameReader.ReadAt(path, times, options, 3, (i, frame) => actual[i] = frame.Data.ToArray());
            Assert.Equal(expected.Count, found);
            for (int i = 0; i < expected.Count; i++)
                Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"{name}: time {times[i]} differs");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
