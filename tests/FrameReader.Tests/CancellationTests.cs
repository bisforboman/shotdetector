using System.Diagnostics;
using FrameReader;

/// <summary>
/// The long one-call operations stop when asked: <see cref="Remux.Copy"/>, <see cref="WaveformData.Read"/> and
/// <see cref="VideoFrameReader.ReadAt"/> with a cancelled token, and <see cref="Audio.Convert"/>, <see cref="Video.Transcode"/> and
/// <see cref="MediaProbe.ProbeJsonAsync"/> while FFmpeg waits for a URL's next bytes. Needs SHOTDETECTOR_FFMPEG_LIBS; without
/// it, nothing.
/// </summary>
public class CancellationTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Fact]
    public void LongOperationsStopWhenCancelled()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-cancel-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            var psi = new ProcessStartInfo(Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            { RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x90:d=2", "-f", "lavfi", "-i", "sine=d=2",
                         "-c:v", "mpeg4", "-c:a", "flac", path, })
            {
                psi.ArgumentList.Add(a);
            }

            using (var p = Process.Start(psi)!)
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }

            var cancelled = new CancellationToken(canceled: true);

            Assert.ThrowsAny<OperationCanceledException>(() => Remux.Copy(path, Path.Combine(dir, "out.mkv"), null, cancelled));
            using (var audio = new AudioReader(path))
                Assert.ThrowsAny<OperationCanceledException>(() => WaveformData.Read(audio, null, cancelled));
            Assert.ThrowsAny<OperationCanceledException>(() =>
                VideoFrameReader.ReadAt(path, [TimeSpan.Zero, TimeSpan.FromSeconds(1)], null, 1, (_, _) => { }, cancelled));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A whole-file job reading a URL that stops sending part way waits in FFmpeg's network read, where only the
    // interrupt callback (the readers' CancellationToken) reaches it: the job's own token has to get there (issue #522),
    // not only be checked between frames. Each job is cancelled once it is waiting, and must stop within a bound.
    [Fact]
    public async Task AudioConvertStopsWhileWaitingForANetworkRead()
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("http"))
            return;
        await InTempFolder(async dir =>
        {
            string wav = Path.Combine(dir, "tone.wav");
            TestMedia.Write(wav, new TestMediaOptions { Audio = "sine=frequency=1000:duration=20" });

            // Cancelled once it has converted some of the audio, i.e. in the read that waits for the rest.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new AudioConvertOptions { Progress = new Inline<MediaProgress>(_ => started.TrySetResult()) };
            await StopsWhenCancelled(wav, 2, (url, token) => Audio.ConvertAsync(url, Path.Combine(dir, "out.wav"), options, token), _ => started.Task);
        });
    }

    // The probe, the video and the audio each open the URL, in that order: the first `whole` get the whole file, and
    // the job is cancelled while the next one waits.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VideoTranscodeStopsWhileWaitingForANetworkRead(int whole)
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("http"))
            return;
        await InTempFolder(async dir =>
        {
            string ts = Path.Combine(dir, "clip.ts");
            TestMedia.Write(ts, new TestMediaOptions { Video = ["testsrc2=d=10"], Audio = "sine=duration=10", AudioEncoder = "aac", VideoEncoder = "mpeg4", VideoQuality = 3 });

            var options = new VideoTranscodeOptions { Encoder = "mpeg4" };

            // The probe finds the streams in half the file: it waits only when it gets less.
            await StopsWhenCancelled(ts,
                whole == 0 ? 16 : 2,
                (url, token) => Video.TranscodeAsync(url, Path.Combine(dir, "out.mkv"), options, token),
                server => server.Requested(whole + 1),
                whole);
        });
    }

    [Fact]
    public async Task ProbeJsonStopsWhileWaitingForANetworkRead()
    {
        if (Libs is null || !FFmpegLibraries.HasProtocol("http"))
            return;
        await InTempFolder(async dir =>
        {
            string ts = Path.Combine(dir, "clip.ts");
            TestMedia.Write(ts, new TestMediaOptions { Video = ["testsrc2=d=10"], VideoEncoder = "mpeg4", VideoQuality = 3 });

            // Too little of the file to find the streams' parameters: opening waits for more.
            await StopsWhenCancelled(ts, 16, (url, token) => MediaProbe.ProbeJsonAsync(url, null, token), server => server.Requested(1));
        });
    }

    static async Task InTempFolder(Func<string, Task> test)
    {
        string dir = Directory.CreateTempSubdirectory("framereader-cancel-").FullName;
        try
        {
            await test(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Serves <paramref name="path"/> with only 1/<paramref name="part"/> of its bytes sent (all of them to the first
    /// <paramref name="whole"/> requests), starts the job on its URL,
    /// waits for <paramref name="waiting"/> (bounded) and a moment more, cancels, and expects the job to stop with an
    /// <see cref="OperationCanceledException"/> in a few seconds (it would wait for the rest of the file otherwise).
    /// </summary>
    static async Task StopsWhenCancelled(string path, int part, Func<string, CancellationToken, Task> job, Func<StallingServer, Task> waiting, int whole = 0)
    {
        using var server = new StallingServer(File.ReadAllBytes(path), part, whole);
        using var cancel = new CancellationTokenSource();
        var run = job($"{server.Url}{Path.GetFileName(path)}", cancel.Token);
        await waiting(server).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(500);
        Assert.False(run.IsCompleted, $"{Path.GetFileName(path)}: the job ended before the server stalled ({run.Exception?.InnerException?.Message})");
        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        var e = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(e is OperationCanceledException, $"{Path.GetFileName(path)}: {e?.GetType().Name ?? "no exception"} after {watch.Elapsed}");
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread.</summary>
    sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// An HTTP server on localhost that answers the first <c>whole</c> requests with all the bytes and the rest with the
    /// first 1/part of them (announcing all), then sends nothing more until disposed.
    /// </summary>
    sealed class StallingServer : IDisposable
    {
        readonly System.Net.HttpListener _listener = new();
        readonly CancellationTokenSource _release = new();
        readonly Task _serving;
        int _requests;

        public StallingServer(byte[] bytes, int part, int whole)
        {
            int port = Random.Shared.Next(20000, 60000);
            Url = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                var responses = new List<Task>();
                int served = 0;
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        break;
                    }

                    responses.Add(Respond(ctx, bytes, ++served <= whole ? 1 : part));
                }

                await Task.WhenAll(responses);
            });
        }

        public string Url { get; }

        /// <summary>Completes once <paramref name="count"/> requests have had their bytes.</summary>
        public async Task Requested(int count)
        {
            while (Volatile.Read(ref _requests) < count)
                await Task.Delay(20);
        }

        public void Dispose()
        {
            _release.Cancel();
            _listener.Close();
            _serving.Wait(TimeSpan.FromSeconds(10));
            _release.Dispose();
        }

        async Task Respond(System.Net.HttpListenerContext ctx, byte[] bytes, int part)
        {
            bool closed = false;
            try
            {
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length / part));
                await ctx.Response.OutputStream.FlushAsync();
                Interlocked.Increment(ref _requests);
                if (part == 1)
                {
                    ctx.Response.Close();
                    closed = true;
                    return;
                }

                await Task.Delay(Timeout.Infinite, _release.Token);   // the rest never comes
            }
            catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException
                                          or OperationCanceledException or IOException)
            {
            }
            finally
            {
                if (!closed)
                    ctx.Response.Abort();
            }
        }
    }
}
