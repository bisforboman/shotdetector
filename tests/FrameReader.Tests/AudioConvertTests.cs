using System.Diagnostics;
using FrameReader;

/// <summary>
/// Audio.Convert against ffmpeg's command line (issue #192): <c>ffmpeg -i IN -vn [-af G] [-c:a E] [-b:a B] -fflags
/// +bitexact -flags:a +bitexact OUT</c>, byte for byte, including the conversion ffmpeg inserts to what the encoder
/// takes. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioConvertTests
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

    // Sources: 5.1 at 48 kHz (six sines), as integer PCM in WAV and as float AAC in MKV, with a title, an artist and the
    // stream's language; stereo at 96 kHz. With video too, which -vn (and Convert) leaves out.
    const string Sines = "-f lavfi -i sine=f=220:d=3 -f lavfi -i sine=f=330:d=3 -f lavfi -i sine=f=440:d=3 "
        + "-f lavfi -i sine=f=550:d=3 -f lavfi -i sine=f=660:d=3 -f lavfi -i sine=f=770:d=3";

    const string Join6 = "[0][1][2][3][4][5]join=inputs=6:channel_layout=5.1,volume=6[a]";

    [Theory]
    [InlineData("pcm51.wav", "-c:a pcm_s16le", "out.mp3", null, null, null)]        // integer samples: a normalized downmix
    [InlineData("aac51.mkv", "-c:a aac", "out.mp3", null, null, 128000L)]           // float samples: not normalized
    [InlineData("aac51.mkv", "-c:a aac", "out.m4a", "equalizer=f=1000:t=q:w=8:g=-20", null, null)]
    [InlineData("pcm51.wav", "-c:a pcm_s16le", "out.wav", "volume=0.5", null, null)]
    [InlineData("hi96.mkv", "-c:a flac", "out.mp3", null, "libmp3lame", null)]       // 96 kHz: the nearest rate MP3 takes
    [InlineData("ac351.mkv", "-c:a ac3", "out.mp3", null, null, null)]                // AC-3: its own downmix levels (issue #211)
    [InlineData("ac351.mkv", "-c:a ac3", "out.wav", "aformat=channel_layouts=stereo", null, null)] // the same without LAME: bits on every build
    public void FileIsFfmpegs(string source, string codec, string output, string? filter, string? encoder, long? bitRate)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-aconvert-").FullName;
        try
        {
            string input = Path.Combine(dir, source), expected = Path.Combine(dir, "expected-" + output), actual = Path.Combine(dir, output);
            string[] make = source == "hi96.mkv"
                ? ["-f", "lavfi", "-i", "sine=f=440:sample_rate=96000:d=3", "-ac", "2"]
                : source.EndsWith(".wav", StringComparison.Ordinal)
                    ? [.. Sines.Split(' '), "-filter_complex", Join6, "-map", "[a]", "-metadata", "title=Convert test"]
                    : [.. Sines.Split(' '), "-f", "lavfi", "-i", "testsrc2=s=160x120:r=25:d=3", "-filter_complex", Join6, "-map", "[a]", "-map", "6",
                       "-c:v", "mpeg4", "-metadata", "title=Convert test", "-metadata", "artist=Someone", "-metadata:s:a:0", "language=swe"];
            Ffmpeg(["-v", "error", "-y", .. make, .. codec.Split(' '), input]);

            Ffmpeg(["-v", "error", "-y", "-i", input, "-vn",
                .. filter is null ? Array.Empty<string>() : ["-af", filter],
                .. encoder is null ? Array.Empty<string>() : ["-c:a", encoder],
                .. bitRate is null ? Array.Empty<string>() : ["-b:a", bitRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                "-fflags", "+bitexact", "-flags:a", "+bitexact", expected]);
            Audio.Convert(input, actual, new AudioConvertOptions { Filter = filter, Encoder = encoder, BitRate = bitRate, Bitexact = true });

            byte[] want = File.ReadAllBytes(expected), got = File.ReadAllBytes(actual);
            if (output.EndsWith(".mp3", StringComparison.Ordinal) && !AudioWriterTests.SameBuild())
            {
                // LAME's bits depend on how it was compiled (AudioWriterTests): the same layout, not the same bits.
                Assert.Equal(want.Length, got.Length);
                Assert.Equal(AudioWriterTests.Samples(expected), AudioWriterTests.Samples(actual));
                return;
            }

            int first = 0;
            while (first < Math.Min(want.Length, got.Length) && want[first] == got[first])
                first++;
            Assert.True(want.AsSpan().SequenceEqual(got), $"{source} -> {output}: ffmpeg {want.Length} bytes, ours {got.Length}, first difference at {first}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoAudioIsAnError()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-aconvert-").FullName;
        try
        {
            string input = Path.Combine(dir, "video.mp4");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x120:r=25:d=1", "-c:v", "mpeg4", input);
            var e = Assert.Throws<FrameReaderException>(() => Audio.Convert(input, Path.Combine(dir, "out.mp3")));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
            Assert.False(File.Exists(Path.Combine(dir, "out.mp3")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
