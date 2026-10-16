using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// FrameDecoder through its public API, against ffmpeg/ffprobe on generated clips. Needs SHOTDETECTOR_FFMPEG_LIBS (an
/// FFmpeg 8.1 shared build's folder with ffmpeg and ffprobe); without it these do nothing. ShotDetector's in-process
/// tests check the frames byte for byte against the ffmpeg executable.
/// </summary>
public class FrameDecoderTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    static string Clip(string dir, string name, params string[] args)
    {
        string path = Path.Combine(dir, name);
        Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", .. args, path]);
        return path;
    }

    static FrameDecoderOptions Options => new();

    [Fact]
    public void DecodesEveryFrameWithFfmpegsTimestamps()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Clip(dir, "clip.mp4", "-c:v", "mpeg4", "-bf", "2");
            var expected = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp", "-of", "csv=p=0", path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            using var decoder = new FrameDecoder(path, Options);
            var pts = new List<string>();
            while (decoder.Next())
            {
                pts.Add(decoder.Pts.ToString(CultureInfo.InvariantCulture));
                Assert.Equal((320, 240, "yuv420p"), (decoder.Width, decoder.Height, decoder.PixelFormat));
                var y = decoder.GetPlane(0, out int ys);
                var u = decoder.GetPlane(1, out int us);
                Assert.True(ys >= 320 && y.Length == ys * 240);
                Assert.True(us >= 160 && u.Length == us * 120);
            }

            Assert.Equal(expected, pts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DeinterlacesRotatesAndReadsStreams()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            // Deinterlacing (FFmpeg's yadif, when the libraries have it): the same frame count, progressive frames.
            string interlaced = Clip(dir, "interlaced.mkv", "-c:v", "mpeg2video", "-flags", "+ilme+ildct");
            if (FFmpegLibraries.CanDeinterlace(Libs))
            {
                using var decoder = new FrameDecoder(interlaced, Options with { Deinterlace = true });
                int n = 0;
                while (decoder.Next())
                    n++;
                Assert.Equal(50, n);
            }

            // A 90-degree display matrix: frames come out turned, as ffmpeg's autorotate turns them.
            string plain = Clip(dir, "plain.mp4", "-c:v", "mpeg4"), rotated = Path.Combine(dir, "rotated.mp4");
            Run("ffmpeg", "-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated);
            using (var decoder = new FrameDecoder(rotated, Options))
            {
                Assert.True(decoder.Next());
                Assert.Equal((240, 320), (decoder.Width, decoder.Height));
            }

            // A stream (mkv: headers first): the same frames as the file.
            int FromFile()
            {
                using var d = new FrameDecoder(interlaced, Options);
                int n = 0;
                while (d.Next())
                    n++;
                return n;
            }

            using (var stream = File.OpenRead(interlaced))
            using (var decoder = new FrameDecoder(stream, Options))
            {
                int n = 0;
                while (decoder.Next())
                    n++;
                Assert.Equal(FromFile(), n);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A read that fails part way (a network reset, a Stream that throws) is an error rather than a shorter video; a
    // truncated file still just ends where its data does, as in OpenCV.
    [Fact]
    public void ReadErrorsThrowButTruncationEnds()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new TestMediaOptions { Video = ["testsrc2=d=4"], VideoEncoder = "mpeg4", VideoQuality = 3 });
            byte[] bytes = File.ReadAllBytes(path);
            using (var decoder = new FrameDecoder(new FailingStream(bytes, bytes.Length / 2), Options))
            {
                var e = Assert.Throws<FrameReaderException>(() =>
                {
                    while (decoder.Next())
                    {
                    }
                });
                Assert.Equal(FrameReaderError.DecodeFailed, e.Reason);
                Assert.IsType<IOException>(e.InnerException);
            }

            string truncated = Path.Combine(dir, "truncated.mkv");
            File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
            int frames = 0;
            using (var decoder = new FrameDecoder(truncated, Options))
            {
                while (decoder.Next())
                    frames++;
            }

            Assert.InRange(frames, 1, 99);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A server that sends the start of a clip and then stalls: cancellation stops the read FFmpeg waits in (its interrupt
    // callback), and rw_timeout makes it fail rather than wait forever.
    [Fact]
    public void StalledNetworkReadsCancelAndTimeOut()
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("http"))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        var listener = new System.Net.HttpListener();
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new TestMediaOptions { Video = ["testsrc2=d=10"], VideoEncoder = "mpeg4", VideoQuality = 3 });
            byte[] bytes = File.ReadAllBytes(path);
            int port = Random.Shared.Next(20000, 60000);
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync();
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length * 3 / 4));
                        await ctx.Response.OutputStream.FlushAsync();
                        await Task.Delay(Timeout.Infinite);   // the rest never comes
                    }
                    catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
            string url = $"http://localhost:{port}/clip.mkv";

            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                using var decoder = new FrameDecoder(url, new FrameDecoderOptions { CancellationToken = cancel.Token });
                while (decoder.Next())
                {
                }
            });
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"stopped after {watch.Elapsed}");

            // Opening seeks too (an mkv's index), so the wait can end in the open (InvalidInput) or in a read (DecodeFailed).
            watch.Restart();
            var timedOut = Assert.Throws<FrameReaderException>(() =>
            {
                using var decoder = new FrameDecoder(url, new FrameDecoderOptions { InputOptions = new Dictionary<string, string> { ["rw_timeout"] = "1000000" } });
                while (decoder.Next())
                {
                }
            });
            Assert.Contains(timedOut.Reason, new[] { FrameReaderError.InvalidInput, FrameReaderError.DecodeFailed });
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"timed out after {watch.Elapsed}");
        }
        finally
        {
            listener.Close();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A stream of these bytes whose reads throw, as a dropped connection does, from <paramref name="failAt"/> on.</summary>
    sealed class FailingStream(byte[] data, int failAt) : Stream
    {
        int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= failAt)
                throw new IOException("The connection was reset.");
            int n = Math.Min(count, failAt - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // MaxPixels (FFmpeg's max_pixels): a frame larger than the cap fails to decode; the default leaves real video alone.
    [Fact]
    public void FramesLargerThanTheCapFail()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(path, new TestMediaOptions { Video = ["testsrc2=d=1"], Size = (320, 240), VideoEncoder = "mpeg4" });
            using (var decoder = new FrameDecoder(path, Options))
                Assert.True(decoder.Next());
            Assert.Throws<FrameReaderException>(() =>
            {
                using var decoder = new FrameDecoder(path, new FrameDecoderOptions { MaxPixels = 320 * 200 });
                while (decoder.Next())
                {
                }
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoVideoIsInvalidInput()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Path.Combine(dir, "audio.wav");
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=d=1", path);
            var e = Assert.Throws<FrameReaderException>(() => new FrameDecoder(path, Options));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
