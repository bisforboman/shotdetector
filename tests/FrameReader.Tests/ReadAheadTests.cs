using System.Diagnostics;
using System.Security.Cryptography;
using FrameReader;

/// <summary>
/// <see cref="VideoFrameReaderOptions.ReadAhead"/>: decoding ahead on a thread of its own gives what decoding in the read
/// gives (frames, numbers, times, tags, the end, errors), whatever the reads, seeks and jumps; and it stops cleanly when
/// disposed with the thread mid-frame. Needs SHOTDETECTOR_FFMPEG_LIBS; without it these do nothing.
/// </summary>
public class ReadAheadTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    /// <summary>A frame as a line: its number, times, size, format, tags and the hash of its bytes.</summary>
    static string Describe(in VideoFrame f) =>
        $"{f.Index} {f.Pts} {f.Time.Ticks} {f.Width}x{f.Height} {f.FrameFormat} {f.SampleAspectRatio} {f.ColorRange} {f.ColorSpace} "
        + $"{f.ColorPrimaries} {f.ColorTransfer} {Convert.ToHexString(SHA256.HashData(f.Data))}";

    static string Read(VideoFrameReader reader, Func<VideoFrameReader, (bool Got, string Frame)> read)
    {
        try
        {
            var (got, frame) = read(reader);
            return got ? frame : "end";
        }
        catch (Exception e) when (e is FrameReaderException or OperationCanceledException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    static (bool, string) Next(VideoFrameReader r) => r.TryRead(out var f) ? (true, Describe(f)) : (false, "");

    static (bool, string) To(VideoFrameReader r, double s) => r.TryReadForwardTo(TimeSpan.FromSeconds(s), out var f) ? (true, Describe(f)) : (false, "");

    /// <summary>What the reads below give, one line per read.</summary>
    static List<string> Run(string path, VideoFrameReaderOptions options, bool canSeek)
    {
        var lines = new List<string>();
        using (var reader = new VideoFrameReader(path, options))
        {
            // Every frame, and past the end.
            for (int i = 0; i < 200; i++)
            {
                lines.Add("read " + Read(reader, Next));
                if (lines[^1] == "read end")
                    break;
            }

            lines.Add("read " + Read(reader, Next));
        }

        using (var reader = new VideoFrameReader(path, options))
        {
            // A picture every second, the same time twice, steps within a frame, jumps over GOPs (indexed inputs), reads
            // in between, and past the end.
            foreach (double t in new[] { 0, 0.3, 1.0, 1.0, 1.039, 1.04, 2, 3.3, -1, -1, 3.5, 4.1, -1, 5.9, 7, -1 })
            {
                // A long queue full before the jumps: frames past the target already read ahead.
                if (t is 3.3 or 5.9 && options.ReadAhead > 8)
                    Thread.Sleep(200);
                lines.Add($"to {t} " + Read(reader, r => t < 0 ? Next(r) : To(r, t)));
            }
        }

        if (canSeek)
        {
            using var reader = new VideoFrameReader(path, options);

            // Seeks back and forth, reading on from each, and a seek with frames read ahead.
            foreach (double t in new[] { 2.0, 0.5, 4.1, 0.0, 9.0, 1.0 })
            {
                reader.Seek(TimeSpan.FromSeconds(t));
                for (int i = 0; i < 3; i++)
                    lines.Add($"seek {t} " + Read(reader, Next));
            }

            lines.Add("at 3.1 " + Read(reader, r => r.TryReadAt(TimeSpan.FromSeconds(3.1), out var f) ? (true, Describe(f)) : (false, "")));
            lines.Add("to 4 " + Read(reader, r => To(r, 4)));
        }

        using (var reader = new VideoFrameReader(path, options with { FrameRate = new(3, 2), Width = null, Height = null, MaxWidth = 100 }))
        {
            for (int i = 0; i < 50; i++)
            {
                lines.Add("fps " + Read(reader, Next));
                if (lines[^1] == "fps end")
                    break;
            }
        }

        return lines;
    }

    public static TheoryData<string, string, string, int> Sources => new()
    {
        { "bframes.mp4", "-c:v mpeg4 -bf 2 -g 12", "", 0 },                              // indexed: TryReadForwardTo jumps
        { "opengop.mkv", "-c:v mpeg2video -bf 2 -g 12", "", 0 },                         // open GOPs: jumps from a keyframe early
        { "late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10", "", 0 },        // no index, starts at 10 s
        { "h264.mp4", "-c:v libx264 -preset veryfast -g 25 -pix_fmt yuv420p", "", 0 },   // frame threads
        { "10bit.mp4", "-c:v libx264 -preset veryfast -g 25 -pix_fmt yuv420p10le", "", 0 },
        { "422-10bit.mkv", "-c:v ffv1 -pix_fmt yuv422p10le", "", 0 },
        { "rotated.mov", "-c:v mpeg4 -g 12", "-display_rotation 90", 0 },                // autorotate
        { "interlaced.mkv", "-c:v mpeg2video -flags +ilme+ildct -g 12", "", 1 },          // FFmpeg's yadif
        { "interlaced-rows.mkv", "-c:v mpeg2video -flags +ilme+ildct -g 12", "", 2 },     // row-wise yadif
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void FramesAreThoseWithout(string name, string encode, string mux, int deinterlace)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-ahead-").FullName;
        try
        {
            string path = Path.Combine(dir, name), plain = Path.Combine(dir, "plain" + Path.GetExtension(name));
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=6", .. encode.Split(' '), mux == "" ? path : plain]);
            if (mux != "")
                Ffmpeg(["-v", "error", "-y", .. mux.Split(' '), "-i", plain, "-c", "copy", path]);
            foreach (var format in new[] { FrameFormat.Bgr24, FrameFormat.Yuv420p })
            {
                var options = new VideoFrameReaderOptions
                {
                    MaxWidth = 200,
                    FrameFormat = format,
                    Decoder = new FrameDecoderOptions
                    {
                        Deinterlace = deinterlace > 0,
                        RowDeinterlacer = deinterlace == 2 ? new FrameDecoderTests.Averaging() : null,
                    },
                };
                var expected = Run(path, options, deinterlace == 0);
                foreach (int ahead in new[] { 1, 2, 8, 60 })
                {
                    var actual = Run(path, options with { ReadAhead = ahead }, deinterlace == 0);
                    Assert.Equal(expected.Count, actual.Count);
                    for (int i = 0; i < expected.Count; i++)
                        Assert.True(expected[i] == actual[i], $"{name} {format} ReadAhead {ahead}, read {i}:\n{expected[i]}\n{actual[i]}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A picture every second (the issue's loop): within the second the reader needn't look at the index, so the
    // decoding thread runs on through every read; a jump further pauses it once.
    [Fact]
    public void ReadingEverySecondDoesntStopTheThread()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-ahead-").FullName;
        try
        {
            string path = Path.Combine(dir, "h264.mp4");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=25:d=10", "-c:v", "libx264", "-g", "25", path);
            using var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = 2 });
            for (int i = 0; i < 7; i++)
                Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(i), out _));
            Assert.Equal(0, reader.ReadAheadPauses);
            Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(9), out var far));
            Assert.Equal(1, reader.ReadAheadPauses);
            Assert.Equal(TimeSpan.FromSeconds(9), far.Time);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A Stream input that fails part way: the frames before, then the same error, at the same read; and reading on after
    // it as the decoder does without the thread.
    [Fact]
    public void ErrorsComeAtTheSameRead()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-ahead-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=4", "-c:v", "mpeg4", "-g", "12", path);
            byte[] bytes = File.ReadAllBytes(path);
            List<string> ReadAll(int ahead)
            {
                var lines = new List<string>();
                using var stream = new Failing(bytes, bytes.Length / 2);
                using var reader = new VideoFrameReader(stream, new VideoFrameReaderOptions { Width = 64, Height = 48, ReadAhead = ahead });
                for (int i = 0; i < 150 && lines.LastOrDefault() != "end"; i++)
                    lines.Add(Read(reader, Next));
                return lines;
            }

            var expected = ReadAll(0);
            Assert.Contains(expected, l => l.StartsWith("FrameReaderException", StringComparison.Ordinal));
            Assert.Equal(expected, ReadAll(1));
            Assert.Equal(expected, ReadAll(3));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Disposed while the thread decodes (or waits with a full queue, or hasn't started), at any point: no hang, no crash,
    // every frame taken let go (a use after free would show up over the rounds as a crash).
    [Fact]
    public void DisposeStopsTheThreadMidFrame()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-ahead-").FullName;
        try
        {
            string path = Path.Combine(dir, "h264.mp4");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=25:d=2", "-c:v", "libx264", "-preset", "ultrafast", path);
            for (int round = 0; round < 60; round++)
            {
                using var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = 1 + (round % 4), FrameFormat = FrameFormat.Gray8 });
                for (int i = 0; i < round % 5; i++)
                    Assert.True(reader.TryRead(out _));
                if (round % 3 == 0)
                    Thread.Sleep(round % 2); // the thread mid-frame or waiting
            }

            Assert.Equal(0, ReadAhead.Held); // every frame taken let go

            // Reading after Dispose is an error, not a hang.
            var disposed = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = 2 });
            Assert.True(disposed.TryRead(out _));
            disposed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposed.TryRead(out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadAheadIsZeroOrMore()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrameReader("none.mp4", new VideoFrameReaderOptions { ReadAhead = -1 }));
    }

    /// <summary>A stream of the bytes that throws once, when <paramref name="failAt"/> bytes have been read.</summary>
    sealed class Failing(byte[] bytes, int failAt) : MemoryStream(bytes)
    {
        bool _failed;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAt && !_failed)
            {
                _failed = true;
                throw new IOException("The connection dropped.");
            }

            if (_failed)
                return base.Read(buffer, offset, count);
            return base.Read(buffer, offset, (int)Math.Min(count, failAt - Position));
        }
    }
}
