using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// VideoWriter against ffmpeg's command line: the same raw frames (and samples) in, the same file out, byte for byte,
/// bitexact on both sides. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 build with libx264 and its ffmpeg, the same
/// build: x264's output depends on how it was compiled); without libx264 there, these do nothing.
/// </summary>
public class VideoWriterTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static string Exe => Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
    }

    /// <summary>Whether libx264 is there and the ffmpeg executable is the libraries' own build.</summary>
    static bool CanCompare()
    {
        if (Libs is null || !File.Exists(Exe))
            return false;
        string probe = Path.Combine(Path.GetTempPath(), $"framereader-x264-{Guid.NewGuid():N}.mp4");
        try
        {
            new VideoWriter(probe, new VideoWriterOptions { Width = 16, Height = 16, FrameRate = new(25, 1), LibraryDirectory = Libs }).Dispose();
        }
        catch (FrameReaderException)
        {
            return false; // no libx264 in these libraries
        }
        finally
        {
            File.Delete(probe);
        }
        var psi = new ProcessStartInfo(Exe, "-hide_banner -version") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string version = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return version.Contains("configuration: " + FFmpeg.AutoGen.ffmpeg.avcodec_configuration().Trim());
    }

    /// <summary>Frames that change: moving bars, a cut at 1.5 s, noise.</summary>
    static byte[] Frames(int w, int h, int count, int bytesPerPixel)
    {
        var data = new byte[w * h * bytesPerPixel * count];
        var random = new Random(3);
        for (int f = 0; f < count; f++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < bytesPerPixel; c++)
                        data[((f * h + y) * w + x) * bytesPerPixel + c] = (byte)(f < count / 2
                            ? ((x + f * 4) / 16 * 40 + c * 70 + random.Next(8))
                            : ((y + f * 3) / 12 * 50 + c * 30 + random.Next(8)));
        return data;
    }

    public static TheoryData<string, string, string> Cases => new()
    {
        { "plain.mp4", "", "" },
        { "yuv420p.mp4", "-pix_fmt yuv420p -crf 28 -preset veryfast", "" },
        { "keys.mp4", "-pix_fmt yuv420p -g 48 -keyint_min 48 -force_key_frames expr:gte(t,n_forced*1) -forced-idr 1 -x264-params scenecut=0", "keys" },
        { "bitrate.mkv", "-pix_fmt yuv420p -b:v 500k", "" },
        { "audio.mp4", "-pix_fmt yuv420p -crf 30", "audio" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FileIsFfmpegs(string name, string args, string extra)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-vwrite-").FullName;
        try
        {
            const int W = 160, H = 96, N = 75; // 3 s at 25 fps
            byte[] frames = Frames(W, H, N, 3);
            string raw = Path.Combine(dir, "in.raw"), wav = Path.Combine(dir, "in.f32"), expected = Path.Combine(dir, "expected-" + name), actual = Path.Combine(dir, name);
            File.WriteAllBytes(raw, frames);
            var audio = new float[44100 * 3 * 2];
            for (int i = 0; i < audio.Length / 2; i++)
                audio[2 * i] = audio[2 * i + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            File.WriteAllBytes(wav, MemoryMarshal.AsBytes(audio.AsSpan()).ToArray());
            bool withAudio = extra == "audio";
            string[] a = args.Length == 0 ? [] : args.Split(' ');
            Ffmpeg(["-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{W}x{H}", "-r", "25", "-i", raw,
                .. withAudio ? new[] { "-f", "f32le", "-ar", "44100", "-ac", "2", "-i", wav } : [],
                "-c:v", "libx264", .. a, .. withAudio ? new[] { "-c:a", "aac" } : [],
                "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", expected]);

            string? Arg(string key) => Array.IndexOf(a, key) is >= 0 and var i ? a[i + 1] : null;
            var options = new VideoWriterOptions
            {
                Width = W, Height = H, FrameRate = new(25, 1), PixelFormat = Arg("-pix_fmt"),
                Crf = Arg("-crf") is { } crf ? double.Parse(crf, CultureInfo.InvariantCulture) : null,
                Preset = Arg("-preset"), BitRate = Arg("-b:v") is { } b ? long.Parse(b.TrimEnd('k')) * 1000 : null,
                Gop = Arg("-g") is { } g ? int.Parse(g) : null, KeyintMin = Arg("-keyint_min") is { } k ? int.Parse(k) : null,
                KeyframeEvery = extra == "keys" ? TimeSpan.FromSeconds(1) : null, X264Params = Arg("-x264-params"),
                Audio = withAudio ? new AudioTrackOptions { SampleRate = 44100, Channels = 2 } : null,
                Bitexact = true, LibraryDirectory = Libs,
            };
            using (var writer = new VideoWriter(actual, options))
            {
                for (int f = 0; f < N; f++)
                {
                    writer.Write(frames.AsSpan(f * writer.FrameSize, writer.FrameSize));
                    if (withAudio && f % 25 == 0) // a second of audio at a time, interleaved with the frames
                        writer.WriteAudio(audio.AsSpan(f / 25 * 44100 * 2, 44100 * 2));
                }
            }
            byte[] want = File.ReadAllBytes(expected), got = File.ReadAllBytes(actual);
            int first = 0;
            while (first < Math.Min(want.Length, got.Length) && want[first] == got[first])
                first++;
            Assert.True(want.AsSpan().SequenceEqual(got), $"{name}: ffmpeg {want.Length} bytes, ours {got.Length}, first difference at {first}");
            if (extra == "keys")
            {
                // A keyframe every second exactly: frames 0, 25, 50.
                using var reader = new FrameDecoder(actual, new FrameDecoderOptions { LibraryDirectory = Libs });
                var keys = new List<int>();
                for (int i = 0; reader.Next(); i++)
                    if (reader.IsKeyframe)
                        keys.Add(i);
                Assert.Equal([0, 25, 50], keys);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ErrorsSayWhatsMissing()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-vwrite-").FullName;
        try
        {
            string path = Path.Combine(dir, "x.mp4");
            Assert.Throws<ArgumentException>(() => new VideoWriter(path, new VideoWriterOptions { Width = 0, Height = 10, FrameRate = new(25, 1) }));
            if (CanCompare())
            {
                var e = Assert.Throws<FrameReaderException>(() => new VideoWriter(path, new VideoWriterOptions
                    { Width = 16, Height = 16, FrameRate = new(25, 1), EncoderOptions = new Dictionary<string, string> { ["nosuchoption"] = "1" }, LibraryDirectory = Libs }));
                Assert.Contains("nosuchoption", e.Message);
                using var w = new VideoWriter(path, new VideoWriterOptions { Width = 16, Height = 16, FrameRate = new(25, 1), LibraryDirectory = Libs });
                Assert.Throws<ArgumentException>(() => w.Write(new byte[10]));
                Assert.Throws<InvalidOperationException>(() => w.WriteAudio(new float[2]));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
