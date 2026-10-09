using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// AudioReader against ffmpeg's command line: the samples of <c>-vn [-ss T] [-ar R -ac C] -f f32le</c>, byte for byte.
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioReaderTests
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
    [InlineData("aac.m4a", "-f lavfi -i sine=f=440:d=3:r=44100 -ac 2 -c:a aac")]
    [InlineData("mp3.mp3", "-f lavfi -i sine=f=330:d=3:r=44100 -c:a libmp3lame")]
    [InlineData("opus.webm", "-f lavfi -i sine=f=550:d=3:r=48000 -ac 2 -c:a libopus")]
    [InlineData("flac51.flac", "-f lavfi -i sine=f=220:d=3:r=48000 -af pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0 -c:a flac")]
    [InlineData("pcm.wav", "-f lavfi -i sine=f=660:d=3:r=22050 -c:a pcm_s16le")]
    public void SamplesAreFfmpegs(string name, string make)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-audio-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. make.Split(' '), path]);
            foreach (var (rate, channels) in new (int?, int?)[] { (null, null), (16000, 1), (48000, 2) })
            {
                foreach (double? seek in new double?[] { null, 0.5, 1.37 })
                {
                    string[] ss = seek is { } t ? ["-ss", t.ToString(CultureInfo.InvariantCulture)] : [];
                    string[] conversion = [.. rate is { } r ? new[] { "-ar", $"{r}" } : [], .. channels is { } c ? new[] { "-ac", $"{c}" } : []];
                    byte[] expected = Ffmpeg(["-v", "error", .. ss, "-i", path, "-vn", .. conversion, "-f", "f32le", "-"]);
                    using var reader = new AudioReader(path, new AudioReaderOptions { SampleRate = rate, Channels = channels, LibraryDirectory = Libs });
                    if (seek is { } s)
                        reader.Seek(TimeSpan.FromSeconds(s));
                    var actual = new MemoryStream();
                    while (reader.TryRead(out var chunk))
                    {
                        Assert.Equal(reader.Channels, chunk.Channels);
                        actual.Write(MemoryMarshal.AsBytes(chunk.Samples));
                    }
                    Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                        $"{name} rate {rate} channels {channels} seek {seek}: {expected.Length} bytes from ffmpeg, {actual.Length} read");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoAudioIsInvalidInput()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-audio-").FullName;
        try
        {
            string path = Path.Combine(dir, "video.mp4");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=d=1", "-c:v", "mpeg4", path);
            var e = Assert.Throws<FrameReaderException>(() => new AudioReader(path, new AudioReaderOptions { LibraryDirectory = Libs }));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
