using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ShotDetector;

/// <summary>An MPEG-PS clip whose packets don't all carry a pts, so reading it takes the decoding pass for timestamps.</summary>
public sealed class MpegPsClip : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-mpegps-").FullName;

    public MpegPsClip()
    {
        // Large frames in pixels, small in bytes: several frames share a PES packet (only its first gets the pts), and
        // decoding them all takes a while next to reading the packets.
        Path = System.IO.Path.Combine(_dir, "long.mpg");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=120", "-c:v", "mpeg2video", "-q:v", "31", Path], default);
        string packets = VideoReader.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "default=nw=1", Path], default);
        Assert.Contains("pts=N/A", packets);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

/// <summary>
/// Cancelling stops the decoding pass for per-frame timestamps (MPEG-PS packets without a pts), in-process and
/// through ffprobe, rather than decoding the whole file first (issue #523).
/// </summary>
public class FrameTimestampsTests(MpegPsClip clip) : IClassFixture<MpegPsClip>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    // A read the server never finishes: only the token can end it (FrameDecoder's interrupt callback).
    [Fact]
    public async Task InProcessStopsAtAStalledRead()
    {
        if (Libs is null || !InProcess.HasProtocol("http", Libs))
            return;
        using var server = new StalledServer(clip.Path);
        using var cts = new CancellationTokenSource();
        var pass = Task.Run(() => InProcessProbe.FrameTimestamps(server.Url, Libs, VideoReader.InputOptions(server.Url), cts.Token));
        try
        {
            await server.Stalled.WaitAsync(TimeSpan.FromSeconds(30));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            server.Release();
            await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    // The same through ffprobe: cancelling kills it.
    [Fact]
    public async Task FfprobeStopsAtAStalledRead()
    {
        using var server = new StalledServer(clip.Path);
        using var cts = new CancellationTokenSource();
        string ffprobe = Libs is null ? "ffprobe" : Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var pass = Task.Run(() => VideoReader.Run(ffprobe, VideoReader.FrameTimestampsArgs(server.Url, VideoReader.InputOptions(server.Url)), cts.Token));
        try
        {
            await server.Stalled.WaitAsync(TimeSpan.FromSeconds(30));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            server.Release();
            await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    // ShotDetection.Probe's token also stops the stream list, read on first use after the headers (another request for a
    // URL): the server answers the headers' probe in full, then stalls.
    [Theory]
    [InlineData(VideoDecoder.InProcess)]
    [InlineData(VideoDecoder.FfmpegProcess)]
    public async Task StreamsStopAtAStalledRead(VideoDecoder decoder)
    {
        if (decoder == VideoDecoder.InProcess && (Libs is null || !InProcess.HasProtocol("http", Libs)))
            return;
        using var server = new StalledServer(clip.Path, stall: false);
        using var cts = new CancellationTokenSource();
        var options = new DetectionOptions { Decoder = decoder, FfmpegDirectory = Libs };
        var reader = VideoReader.Headers(server.Url, options, cts.Token);
        server.StallFromNow();
        var streams = Task.Run(() => reader.Streams);
        try
        {
            await server.Stalled.WaitAsync(TimeSpan.FromSeconds(30));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streams.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            server.Release();
            await Task.WhenAny(streams, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    // A detection cancelled as its reader starts the pass stops at once, not after decoding the file: VideoReader hands
    // the pass its token.
    [Theory]
    [InlineData(VideoDecoder.InProcess)]
    [InlineData(VideoDecoder.FfmpegProcess)]
    public void CancellingDetectionStopsThePass(VideoDecoder decoder)
    {
        if (decoder == VideoDecoder.InProcess && Libs is null)
            return;

        // How long the whole pass takes here: the faster of two (the first loads the libraries).
        string[] inputOptions = VideoReader.InputOptions(clip.Path);
        string ffprobe = Libs is null ? "ffprobe" : Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var whole = TimeSpan.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            var pass = Stopwatch.StartNew();
            if (decoder == VideoDecoder.InProcess)
                InProcessProbe.FrameTimestamps(clip.Path, Libs, inputOptions);
            else
                VideoReader.Run(ffprobe, VideoReader.FrameTimestampsArgs(clip.Path, inputOptions), default);
            whole = TimeSpan.FromTicks(Math.Min(whole.Ticks, pass.Elapsed.Ticks));
        }

        using var cts = new CancellationTokenSource();
        var sinceCancel = new Stopwatch();

        // The timestamps arrive as the frames do unless something needs them first: a start time.
        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            Decoder = decoder,
            FfmpegDirectory = Libs,
            StartTime = "10",
            OnFrameTimestamps = () =>
            {
                cts.Cancel();
                sinceCancel.Start();
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => ShotDetection.Detect(clip.Path, options, cts.Token));
        sinceCancel.Stop();
        Assert.True(cts.IsCancellationRequested, "The reader never started the pass for timestamps.");
        Assert.True(sinceCancel.Elapsed < whole / 4,
            $"Stopped {sinceCancel.Elapsed.TotalMilliseconds:F0} ms after cancelling; the whole pass takes {whole.TotalMilliseconds:F0} ms.");
    }

    // Through the executable, read from the start, the pass runs alongside the read: cancelling the read stops both.
    [Fact]
    public void CancellingAReadStopsThePassAlongside()
    {
        using var cts = new CancellationTokenSource();
        var reader = new VideoReader(clip.Path, new DetectionOptions { Decoder = VideoDecoder.FfmpegProcess, FfmpegDirectory = Libs });
        Assert.True(reader.TimestampsArrive);
        int frames = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var frame in reader.Frames(cts.Token))
            {
                if (++frames == 5)
                    cts.Cancel();
            }
        });
        var pass = reader.LastPassAlongside;
        Assert.NotNull(pass);
        var waited = Stopwatch.StartNew();
        while (!pass.HasExited && waited.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(20);
        Assert.True(pass.HasExited, "ffprobe still runs after the read was cancelled.");
    }

    // ... and a read that stops early stops it too (a later frame's timestamp takes a pass then, as in-process; a read
    // from the start again starts another alongside).
    [Fact]
    public void StoppingAReadStopsThePassAlongside()
    {
        var reader = new VideoReader(clip.Path, new DetectionOptions { Decoder = VideoDecoder.FfmpegProcess, FfmpegDirectory = Libs });
        Assert.Equal(5, reader.Frames().Take(5).Count());
        Assert.True(reader.TimestampsArrive);
        var first = reader.LastPassAlongside!;
        Assert.True(first.HasExited, "ffprobe still runs after the read stopped.");
        Assert.Equal(5, reader.Frames().Take(5).Count());
        Assert.NotSame(first, reader.LastPassAlongside);
    }

    /// <summary>
    /// An http server on localhost that answers every request with the whole file's length but sends only its first
    /// 2 KB (not a whole frame: the reader waits on the read, where checking the token between frames can't help), then
    /// waits until released. No byte ranges, so FFmpeg doesn't seek. Made with <c>stall: false</c>, it sends the whole
    /// file until <see cref="StallFromNow"/>.
    /// </summary>
    sealed class StalledServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool _stall;

        public StalledServer(string path, bool stall = true)
        {
            _stall = stall;
            byte[] file = File.ReadAllBytes(path);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/long.mpg";
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception e) when (e is SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(() => Serve(client, file));
                }
            });
        }

        public string Url { get; }

        /// <summary>Completes once a response has sent all it will before stalling.</summary>
        public Task Stalled => _stalled.Task;

        public void Release() => _released.TrySetResult();

        /// <summary>Stalls every response from now on.</summary>
        public void StallFromNow() => _stall = true;

        public void Dispose()
        {
            Release();
            _listener.Stop();
        }

        async Task Serve(TcpClient client, byte[] file)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new List<byte>();
                    byte[] one = new byte[1];
                    while (!(request.Count >= 4 && request[^4] == '\r' && request[^3] == '\n' && request[^2] == '\r' && request[^1] == '\n'))
                    {
                        if (await stream.ReadAsync(one) == 0)
                            return;
                        request.Add(one[0]);
                    }

                    string headers = $"HTTP/1.1 200 OK\r\nContent-Type: video/mpeg\r\nContent-Length: {file.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                    if (!_stall)
                    {
                        await stream.WriteAsync(file);
                        return;
                    }

                    await stream.WriteAsync(file.AsMemory(0, Math.Min(file.Length, 2048)));
                    await stream.FlushAsync();
                    _stalled.TrySetResult();
                    await _released.Task;
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                {
                } // the client hung up (cancelled)
            }
        }
    }
}
