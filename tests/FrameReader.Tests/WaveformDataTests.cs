using System.Diagnostics;
using FrameReader;

/// <summary>
/// WaveformData against BBC's audiowaveform: the same .dat bytes. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared
/// build's folder with ffmpeg); the comparison also AUDIOWAVEFORM (the audiowaveform executable; CI installs it).
/// Without them these do nothing.
/// </summary>
public class WaveformDataTests
{
    static readonly string? Libs = TestLibraries.Load();
    static readonly string? Audiowaveform = Environment.GetEnvironmentVariable("AUDIOWAVEFORM");

    static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
    }

    static string Ffmpeg => Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    [Theory]
    [InlineData("stereo16.wav", "-c:a pcm_s16le", "")]
    [InlineData("stereo16.wav", "-c:a pcm_s16le", "--split-channels -b 8 -z 300")]
    [InlineData("stereo24.flac", "-c:a flac -sample_fmt s32", "--pixels-per-second 50")]
    [InlineData("mono16.wav", "-c:a pcm_s16le -ac 1", "-z 512 -b 8")]
    [InlineData("float.wav", "-c:a pcm_f32le", "--split-channels")]
    public void DatIsAudiowaveforms(string name, string encode, string args)
    {
        if (Libs is null || Audiowaveform is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-waveform-").FullName;
        try
        {
            string path = Path.Combine(dir, name), expected = Path.Combine(dir, "expected.dat");
            // Loud enough to clip at times, two different channels, an odd length (a partial last point).
            Run(Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=1.2*sin(440*2*PI*t)|0.6*sin(97*2*PI*t)+0.3*random(0):s=44100:d=2.0137",
                .. encode.Split(' '), path]);
            string[] extra = args.Length == 0 ? [] : args.Split(' ');
            Run(Audiowaveform, ["-q", "-i", path, "-o", expected, .. extra]);

            var options = new WaveformOptions
            {
                SplitChannels = extra.Contains("--split-channels"),
                SamplesPerPixel = extra.Contains("-z") ? int.Parse(extra[Array.IndexOf(extra, "-z") + 1]) : 256,
                PixelsPerSecond = extra.Contains("--pixels-per-second") ? int.Parse(extra[Array.IndexOf(extra, "--pixels-per-second") + 1]) : null,
            };
            int bits = extra.Contains("-b") ? int.Parse(extra[Array.IndexOf(extra, "-b") + 1]) : 16;
            using var audio = new AudioReader(path, new AudioReaderOptions());
            var waveform = WaveformData.Read(audio, options);
            var actual = new MemoryStream();
            waveform.Save(actual, (WaveformBits)bits);
            Assert.Equal(File.ReadAllBytes(expected), actual.ToArray());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A 16-bit stereo WAV with chosen samples: the mixing, the partial point and the 8-bit division by hand.</summary>
    [Fact]
    public void PointsAreMinAndMaxOfTheMixedSamples()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-waveform-").FullName;
        try
        {
            short[] frames = [100, 201, -3, -4, 32767, 32767, -32768, -32768, 7, -8]; // L, R pairs: 5 frames
            string path = Path.Combine(dir, "known.wav");
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = frames.Length * 2;
                w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
                w.Write(8000); w.Write(8000 * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
                foreach (short s in frames)
                    w.Write(s);
            }
            using var audio = new AudioReader(path, new AudioReaderOptions());
            var mixed = WaveformData.Read(audio, new WaveformOptions { SamplesPerPixel = 2 });
            // Means, truncated toward zero: 150, -3, 32767, -32768, 0; points of 2 frames, the last one alone.
            Assert.Equal((1, 3, 8000, 2), (mixed.Channels, mixed.Length, mixed.SampleRate, mixed.SamplesPerPixel));
            Assert.Equal([(-3, 150), (-32768, 32767), (0, 0)], Enumerable.Range(0, 3).Select(i => ((int)mixed.Min(0, i), (int)mixed.Max(0, i))));

            var dat = new MemoryStream();
            mixed.Save(dat, WaveformBits.Eight);
            byte[] b = dat.ToArray();
            Assert.Equal([1, 0, 0, 0, 1, 0, 0, 0, 0x40, 0x1F, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0], b[..20]); // version 1, 8-bit, 8000 Hz, 2, 3 points
            Assert.Equal([0, 0, unchecked((byte)-128), 127, 0, 0], b[20..]); // -3/256 = 0 and 150/256 = 0, toward zero
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SamplesBecome16BitAsLibsndfileGivesThem()
    {
        Assert.Equal(-1, WaveformData.ToShort(-1 / 32768f, fromFloat: false));
        Assert.Equal(-257, WaveformData.ToShort(-0x1_0001 / 8388608f, fromFloat: false)); // 24-bit -65537 >> 8: toward minus infinity, not -256
        Assert.Equal(0, WaveformData.ToShort(-0.00002f, fromFloat: true));              // float: scaled by 32767, truncated
        Assert.Equal(-16386, WaveformData.ToShort(1.5f, fromFloat: true));           // 49150 wraps, as audiowaveform's cast does
        Assert.Equal(-32768, WaveformData.ToShort(-1.5f, fromFloat: false));
    }
}
