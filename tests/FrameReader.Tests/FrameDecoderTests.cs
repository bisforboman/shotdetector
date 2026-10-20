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

    // The frame's chroma location and colour tags are the decoder's (here the h264 VUI's), not the container's header,
    // which says otherwise in this clip; both as ffprobe reports them.
    [Fact]
    public void ReadsTheFramesChromaLocationAndColourTags()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-tags-").FullName;
        try
        {
            if (!Run("ffmpeg", "-hide_banner", "-encoders").Contains(" libx264 ", StringComparison.Ordinal))
                return; // this ffmpeg can't write the clip
            string path = Clip(dir,
                "tags.mkv",
                "-c:v",
                "libx264",
                "-pix_fmt",
                "yuv420p10le",
                "-chroma_sample_location",
                "left",
                "-colorspace",
                "bt709",
                "-color_range",
                "pc",
                "-x264-params",
                "chromaloc=2:colormatrix=smpte170m:fullrange=off");

            string Probe(string entries) => Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_entries", entries, "-of", "default=nw=1", path);
            Assert.Equal("chroma_location=left\ncolor_range=pc\ncolor_space=bt709\n", Sorted(Probe("stream=chroma_location,color_space,color_range")));
            Assert.Equal("chroma_location=topleft\ncolor_range=tv\ncolor_space=smpte170m\n", Sorted(Probe("frame=chroma_location,color_space,color_range")));
            var stream = MediaProbe.Probe(path).Video!;
            Assert.Equal(("left", "bt709", "pc"), (stream.ChromaLocation, stream.ColorSpace, stream.ColorRange));
            using var decoder = new FrameDecoder(path, Options);
            Assert.True(decoder.Next());
            Assert.Equal(("topleft", "smpte170m", "tv"), (decoder.ChromaLocation, decoder.ColorSpace, decoder.ColorRange));

            // Unspecified: null (ffprobe's "unknown" or "unspecified").
            string plain = Clip(dir, "plain.mkv", "-c:v", "ffv1", "-pix_fmt", "yuv420p");
            path = plain;
            string? Value(string key) => Probe("frame=" + key).Trim()[(key.Length + 1)..] is var v && v is "unknown" or "unspecified" ? null : v;
            using var untagged = new FrameDecoder(plain, Options);
            Assert.True(untagged.Next());
            Assert.Null(untagged.ChromaLocation ?? Value("chroma_location"));
            Assert.Equal((Value("color_space"), Value("color_range")), (untagged.ColorSpace, untagged.ColorRange));
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static string Sorted(string probed) =>
            string.Concat(probed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).Select(l => l + "\n"));
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

    // An InputOptions name that no demuxer, protocol or the format context takes fails, as ffmpeg's command line refuses
    // it, for every reader that opens an input (a path or a Stream); the open input is closed first.
    [Fact]
    public void UnknownInputOptionsFail()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Clip(dir, "clip.mp4", "-c:v", "mpeg4", "-t", "0.2");
            var misspelt = new Dictionary<string, string> { ["framerat"] = "10" };
            var e = Assert.Throws<ArgumentException>(() => new FrameDecoder(path, new FrameDecoderOptions { InputOptions = misspelt }).Dispose());
            Assert.Contains("\"framerat\"", e.Message, StringComparison.Ordinal);
            Assert.Contains("mov,mp4", e.Message, StringComparison.Ordinal);
            Assert.Contains("framerat", Assert.Throws<ArgumentException>(() => MediaProbe.Probe(path, new ProbeOptions { InputOptions = misspelt })).Message, StringComparison.Ordinal);
            Assert.Contains("framerat", Assert.Throws<ArgumentException>(() => MediaProbe.ProbeJson(path, new ProbeOptions { InputOptions = misspelt })).Message, StringComparison.Ordinal);
            using (var stream = File.OpenRead(path))
                Assert.Contains("framerat", Assert.Throws<ArgumentException>(() => MediaProbe.Probe(stream, new ProbeOptions { InputOptions = misspelt })).Message, StringComparison.Ordinal);
            using (var stream = File.OpenRead(path))
                Assert.Contains("framerat", Assert.Throws<ArgumentException>(() => new FrameDecoder(stream, new FrameDecoderOptions { InputOptions = misspelt }).Dispose()).Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // What an input takes still opens it: the image sequence's framerate (image2), the format context's
    // protocol_whitelist and fflags, a protocol's rw_timeout (every URLContext has it). FrameReader's own defaults
    // (scan_all_pmts, tls_verify, ca_file) are left over on a plain file and don't count, nor do they when the caller
    // passes them or the TLS options' aliases, as for an http HLS playlist with https segments.
    [Fact]
    public void InputOptionsAnInputTakesOpenIt()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=25:d=0.2", Path.Combine(dir, "img%03d.png"));
            string sequence = Path.Combine(dir, "img%03d.png");
            var info = MediaProbe.Probe(sequence, new ProbeOptions { InputOptions = new Dictionary<string, string> { ["framerate"] = "10", ["protocol_whitelist"] = "file" } });
            Assert.Equal(10, info.Video!.AverageFrameRate.Num / info.Video.AverageFrameRate.Den);

            string path = Clip(dir, "clip.mp4", "-c:v", "mpeg4", "-t", "0.2");
            var taken = new Dictionary<string, string>
            {
                ["protocol_whitelist"] = "file,crypto",
                ["fflags"] = "+genpts",
                ["rw_timeout"] = "30000000",
                ["tls_verify"] = "0",
                ["verify"] = "1",
                ["ca_file"] = "/nowhere.pem",
                ["cafile"] = "/nowhere.pem",
                ["scan_all_pmts"] = "0",
            };
            using (var decoder = new FrameDecoder(path, new FrameDecoderOptions { InputOptions = taken }))
                Assert.True(decoder.Next());
            using (var decoder = new FrameDecoder(path, Options))
                Assert.True(decoder.Next());
            using (var stream = File.OpenRead(path))
                Assert.NotNull(MediaProbe.Probe(stream, new ProbeOptions { InputOptions = new Dictionary<string, string> { ["protocol_whitelist"] = "pipe" } }).Video);
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

    // https checks the server's certificate: one nobody vouches for (self-signed) is refused, so whoever sits between
    // client and server can't hand it another video. FFmpeg 8 doesn't check by default (tls_verify 0 until libavformat
    // 63); tls_verify=0 in InputOptions still turns it off, which also shows the server serves the clip.
    [Fact]
    public void HttpsRefusesAnUntrustedCertificate()
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("https"))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        var server = new SelfSignedHttpsServer(dir);
        try
        {
            string url = $"https://localhost:{server.Port}/clip.mkv";
            var refused = Assert.Throws<FrameReaderException>(() => new FrameDecoder(url).Dispose());
            Assert.Equal(FrameReaderError.InvalidInput, refused.Reason);

            int frames = 0;
            using (var decoder = new FrameDecoder(url, new FrameDecoderOptions { InputOptions = new Dictionary<string, string> { ["tls_verify"] = "0" } }))
            {
                while (decoder.Next())
                    frames++;
            }

            Assert.Equal(25, frames);

            // ShotDetector's options for an https URL (the protocols, rw_timeout; tls_verify=0 here) are all taken there;
            // a misspelt one isn't.
            var shotDetectors = new Dictionary<string, string> { ["protocol_whitelist"] = "http,https,tcp,tls,crypto,httpproxy", ["rw_timeout"] = "30000000", ["tls_verify"] = "0" };
            using (var decoder = new FrameDecoder(url, new FrameDecoderOptions { InputOptions = shotDetectors }))
                Assert.True(decoder.Next());
            var misspelt = new Dictionary<string, string>(shotDetectors) { ["rw_timout"] = "30000000" };
            Assert.Contains("\"rw_timout\"", Assert.Throws<ArgumentException>(() => new FrameDecoder(url, new FrameDecoderOptions { InputOptions = misspelt }).Dispose()).Message, StringComparison.Ordinal);
        }
        finally
        {
            server.Dispose();
            Directory.Delete(dir, true);
        }
    }

    // The segments an HLS playlist names are checked too: FFmpeg doesn't pass tls_verify on to them, so FrameReader
    // adds it to every URL the input opens. A playlist over http naming a segment on a self-signed https server reads
    // nothing by default, and the whole segment with tls_verify=0 (which leaves FFmpeg's own opening alone).
    [Fact]
    public void HlsSegmentsOverHttpsAreChecked()
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("https"))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        var segments = new SelfSignedHttpsServer(dir, "seg.ts");
        var listener = new System.Net.HttpListener();
        try
        {
            byte[] playlist = System.Text.Encoding.ASCII.GetBytes(
                $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.0,\nhttps://localhost:{segments.Port}/seg.ts\n#EXT-X-ENDLIST\n");
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
                        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                        ctx.Response.ContentLength64 = playlist.Length;
                        await ctx.Response.OutputStream.WriteAsync(playlist);
                        ctx.Response.Close();
                    }
                    catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
            string url = $"http://localhost:{port}/play.m3u8";

            Assert.Equal(0, Frames(url, null));
            Assert.Equal(0, Frames(url, new Dictionary<string, string> { ["tls_verify"] = "1" }));   // as ShotDetector passes it
            Assert.Equal(25, Frames(url, new Dictionary<string, string> { ["tls_verify"] = "0" }));
        }
        finally
        {
            listener.Close();
            segments.Dispose();
            Directory.Delete(dir, true);
        }

        // Frames decoded; 0 when opening fails (no segment read: no stream to find).
        static int Frames(string url, Dictionary<string, string>? options)
        {
            try
            {
                using var decoder = new FrameDecoder(url, new FrameDecoderOptions { InputOptions = options });
                int n = 0;
                while (decoder.Next())
                    n++;
                return n;
            }
            catch (FrameReaderException)
            {
                return 0;
            }
        }
    }

    // An rtsp input goes through the InputOptions check like any other: ShotDetector's options for an rtsp URL (the
    // protocol_whitelist VideoReader.InputOptions passes) and rtsp's own (rtsp_transport) open it, a misspelt name
    // doesn't. The reader listens for a publisher (rtsp_flags=listen, rtsp options too, with listen_timeout: FFmpeg 8.1
    // refuses its own default, -1 s passed on to tcp as -1000 ms) and ffmpeg pushes a clip to it. The check runs once
    // the input is open, after the publisher's ANNOUNCE, so the misspelt name needs a publisher too.
    [Fact]
    public void RtspInputOptionsAreChecked()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string clip = Clip(dir, "clip.mkv", "-c:v", "mpeg4");
            var options = new Dictionary<string, string>
            {
                ["protocol_whitelist"] = "rtsp,rtsps,rtp,srtp,udp,tcp,tls",
                ["rtsp_transport"] = "tcp",
                ["rtsp_flags"] = "listen",
                ["listen_timeout"] = "30",
            };
            Assert.Equal(50, Published(clip, options, decoder =>
            {
                int n = 0;
                while (decoder.Next())
                    n++;
                return n;
            }));

            var misspelt = new Dictionary<string, string>(options) { ["rtsp_transprot"] = "tcp" };
            var e = Assert.Throws<ArgumentException>(() => Published(clip, misspelt, _ => 0));
            Assert.Contains("\"rtsp_transprot\"", e.Message, StringComparison.Ordinal);
            Assert.Contains("as rtsp", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        // What read returns from a FrameDecoder listening on a free port while ffmpeg publishes the clip there. A pusher
        // that starts before the reader listens is refused and exits at once: it starts again until the reader is done.
        static int Published(string clip, Dictionary<string, string> options, Func<FrameDecoder, int> read)
        {
            var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            free.Start();
            int port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            string url = $"rtsp://127.0.0.1:{port}/live";
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reader = Task.Run(() =>
            {
                using var decoder = new FrameDecoder(url, new FrameDecoderOptions { InputOptions = options, CancellationToken = cancel.Token });
                return read(decoder);
            });
            var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
            foreach (var a in new[] { "-v", "quiet", "-nostdin", "-i", clip, "-c", "copy", "-rtsp_transport", "tcp", "-f", "rtsp", url })
                psi.ArgumentList.Add(a);
            Process? pusher = null;
            try
            {
                while (!reader.IsCompleted && !cancel.IsCancellationRequested)
                {
                    pusher?.Dispose();
                    pusher = Process.Start(psi)!;
                    Task.WaitAny([reader, pusher.WaitForExitAsync()], TimeSpan.FromSeconds(30));
                    if (!pusher.HasExited || pusher.ExitCode == 0)
                        break;
                }

                Assert.True(Task.WaitAny([reader], TimeSpan.FromSeconds(30)) == 0, "the rtsp reader didn't finish");
                return reader.GetAwaiter().GetResult();
            }
            finally
            {
                if (pusher is { HasExited: false })
                    pusher.Kill(true);
                pusher?.Dispose();
            }
        }
    }

    /// <summary>A clip over https (TLS on a TcpListener) with a self-signed certificate for localhost; byte ranges too.</summary>
    sealed class SelfSignedHttpsServer : IDisposable
    {
        readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _cert;
        readonly byte[] _bytes;

        public SelfSignedHttpsServer(string dir, string name = "clip.mkv")
        {
            string path = Path.Combine(dir, name);
            TestMedia.Write(path, new TestMediaOptions { Video = ["testsrc2=d=1"], VideoEncoder = "mpeg4" });
            _bytes = File.ReadAllBytes(path);
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost",
                key,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Through PFX: Schannel can't use an ephemeral key.
            _cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        async Task Serve()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var tls = new System.Net.Security.SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(_cert);
                            var head = new System.Text.StringBuilder();
                            var one = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await tls.ReadAsync(one) == 1)
                                head.Append((char)one[0]);
                            var range = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Range: bytes=(\d+)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            int from = range.Success ? int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                            string status = range.Success
                                ? $"206 Partial Content\r\nContent-Range: bytes {from}-{_bytes.Length - 1}/{_bytes.Length}"
                                : "200 OK";
                            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 {status}\r\nContent-Length: {_bytes.Length - from}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n"));
                            await tls.WriteAsync(_bytes.AsMemory(from));
                        }
                        catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                        {
                            // the client refused the certificate, or went away
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
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
