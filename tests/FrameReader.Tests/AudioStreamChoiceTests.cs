using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// <see cref="AudioReaderOptions.Stream"/>: the audio stream ffmpeg's <c>-map 0:a:N</c> reads, byte for byte, in a file
/// with several (a stereo mix and a 5.1 one, as deliveries carry), and a number past the last refused. Needs
/// SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioStreamChoiceTests
{
    static readonly string? Libs = TestLibraries.Load();

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

    // Video, then a stereo track at 440 Hz and a 5.1 one at 880 Hz (the best stream, by channels), then a mono one.
    const string Make = "-f lavfi -i testsrc2=s=160x120:r=25:d=2 -f lavfi -i sine=f=440:d=2:r=48000 -f lavfi -i sine=f=880:d=2:r=48000 "
        + "-f lavfi -i sine=f=220:d=2:r=44100 -map 0 -map 1 -map 2 -map 3 -c:v mpeg4 -ac:a:0 2 -ac:a:1 6 -ac:a:2 1";

    [Theory]
    [InlineData("tracks.mkv", "-c:a flac")]
    [InlineData("tracks.mov", "-c:a pcm_s16le")]
    public void EachStreamIsMapsOwn(string name, string codec)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-audio-stream-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), .. codec.Split(' '), path]);
            foreach (int stream in (int[])[0, 1, 2])
            {
                byte[] expected = Ffmpeg("-v", "error", "-i", path, "-map", $"0:a:{stream}", "-f", "f32le", "-");
                using var reader = new AudioReader(path, new AudioReaderOptions { Stream = stream });
                var actual = new MemoryStream();
                while (reader.TryRead(out var chunk))
                    actual.Write(MemoryMarshal.AsBytes(chunk.Samples));

                Assert.Equal(new[] { 2, 6, 1 }[stream], reader.Channels);
                Assert.True(expected.AsSpan().SequenceEqual(actual.ToArray()),
                    $"{name} stream {stream}: {expected.Length} bytes from ffmpeg, {actual.Length} read");
            }

            var missing = Assert.Throws<FrameReaderException>(() => new AudioReader(path, new AudioReaderOptions { Stream = 3 }));
            Assert.Equal(FrameReaderError.InvalidInput, missing.Reason);
            Assert.Contains("it has 3", missing.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => new AudioReader(path, new AudioReaderOptions { Stream = -1 }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
