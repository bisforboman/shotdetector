using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// AudioWriter against ffmpeg's command line: the same float32 samples in, the same file out, byte for byte, with
/// bitexact on both sides (<c>ffmpeg -f f32le -ar R -ac C -i raw -c:a ENC [-b:a B] -fflags +bitexact -flags:a +bitexact OUT</c>).
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioWriterTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
    }

    [Theory]
    [InlineData("out.mp3", 44100, 2, null, null)]
    [InlineData("out.mp3", 22050, 1, 64000L, null)]
    [InlineData("out.m4a", 48000, 2, null, null)]
    [InlineData("out.aac", 44100, 2, 96000L, null)]
    [InlineData("out.wav", 16000, 1, null, null)]
    [InlineData("out.mka", 48000, 2, null, "aac")]
    public void FileIsFfmpegs(string name, int rate, int channels, long? bitRate, string? encoder)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-awrite-").FullName;
        try
        {
            // 2.3 s of two tones and noise, past full scale at times (clipping on the integer formats).
            int n = (int)(rate * 2.3);
            var samples = new float[n * channels];
            var random = new Random(7);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < channels; c++)
                    samples[i * channels + c] = (float)(1.1 * Math.Sin(2 * Math.PI * (440 + 220 * c) * i / rate) * (i < n / 2 ? 1 : 0.3)
                        + 0.05 * (random.NextDouble() - 0.5));
            string raw = Path.Combine(dir, "in.raw"), expected = Path.Combine(dir, "expected" + Path.GetExtension(name)), actual = Path.Combine(dir, name);
            File.WriteAllBytes(raw, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
            string defaultEncoder = Path.GetExtension(name) switch { ".mp3" => "libmp3lame", ".wav" => "pcm_s16le", _ => "aac" };
            Ffmpeg(["-v", "error", "-y", "-f", "f32le", "-ar", $"{rate}", "-ac", $"{channels}", "-i", raw, "-c:a", encoder ?? defaultEncoder,
                .. bitRate is { } b ? new[] { "-b:a", $"{b}" } : [], "-fflags", "+bitexact", "-flags:a", "+bitexact", expected]);

            using (var writer = new AudioWriter(actual, new AudioWriterOptions
                   { SampleRate = rate, Channels = channels, BitRate = bitRate, Encoder = encoder, Bitexact = true }))
            {
                // Uneven chunks, as a reader gives them.
                for (int at = 0, size = 1000; at < samples.Length; at += size * channels, size = size == 1000 ? 4096 : 1000)
                    writer.Write(samples.AsSpan(at, Math.Min(size * channels, samples.Length - at) / channels * channels));
            }
            byte[] want = File.ReadAllBytes(expected), got = File.ReadAllBytes(actual);
            if ((encoder ?? defaultEncoder) == "libmp3lame" && !SameBuild())
            {
                // LAME's floating point depends on how it was compiled: when the ffmpeg executable isn't the libraries'
                // own build (CI pairs our libraries with BtbN's static ffmpeg), the same layout, not the same bits.
                Assert.Equal(want.Length, got.Length);
                Assert.Equal(Samples(expected), Samples(actual));
                return;
            }
            int first = 0;
            while (first < Math.Min(want.Length, got.Length) && want[first] == got[first])
                first++;
            Assert.True(want.AsSpan().SequenceEqual(got), $"{name}: ffmpeg {want.Length} bytes, ours {got.Length}, first difference at {first}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Whether the ffmpeg executable is the libraries' own build (the same configure line).</summary>
    static bool SameBuild()
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "-hide_banner -version")
            { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string version = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        FFmpegLibraries.Load(Libs);
        return version.Contains("configuration: " + FFmpeg.AutoGen.ffmpeg.avcodec_configuration().Trim());
    }

    static long Samples(string path)
    {
        using var reader = new AudioReader(path, new AudioReaderOptions());
        long n = 0;
        while (reader.TryRead(out var chunk))
            n += chunk.SampleCount;
        return n;
    }

    [Fact]
    public void WithoutBitexactTheFileCarriesFfmpegsEncoderTag()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-awrite-").FullName;
        try
        {
            string path = Path.Combine(dir, "tagged.mp3");
            using (var writer = new AudioWriter(path, new AudioWriterOptions { SampleRate = 44100, Channels = 1 }))
                writer.Write(new float[44100]);
            var info = MediaProbe.Probe(path, new ProbeOptions());
            Assert.Equal("mp3", info.Streams[0].Codec);
            Assert.Contains("Lavf", File.ReadAllText(path).Substring(0, 200)); // the ID3v2 TSSE frame, as ffmpeg writes it
            Assert.Throws<FrameReaderException>(() => new AudioWriter(Path.Combine(dir, "x.nosuchformat"), new AudioWriterOptions { SampleRate = 8000, Channels = 1 }));
            Assert.Throws<ArgumentException>(() => new AudioWriter(path, new AudioWriterOptions { SampleRate = 0, Channels = 1 }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
