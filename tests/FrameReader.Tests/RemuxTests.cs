using System.Diagnostics;
using FrameReader;

/// <summary>
/// Remux.Copy against ffmpeg's command line: <c>ffmpeg -i IN -map ... -c copy -fflags +bitexact OUT</c>, byte for byte.
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class RemuxTests
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

    // Sources: H.264 (x264 in the test's ffmpeg) or MPEG-4 Part 2 video with AAC or MP3 audio, a second audio stream with
    // a language, subtitles, chapters and a start time that isn't 0 (MPEG-TS).
    const string Make = "-f lavfi -i testsrc2=s=320x240:r=25:d=3 -f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=660:d=3";

    [Theory]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "video.mp4", StreamSelection.Video, "-map 0:v")]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "audio.m4a", StreamSelection.Audio, "-map 0:a")]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "all.mkv", StreamSelection.All, "-map 0")]
    [InlineData("in.mkv", "-map 0 -map 1 -map 2 -c:v mpeg4 -g 12 -c:a libmp3lame -metadata:s:a:1 language=swe", "both.mp4", StreamSelection.Video | StreamSelection.Audio, "-map 0:v -map 0:a")]
    [InlineData("in.mkv", "-map 0 -map 1 -map 2 -c:v mpeg4 -g 12 -c:a libmp3lame -metadata:s:a:1 language=swe", "audio.mp3", StreamSelection.Audio, "-map 0:a:0")]
    [InlineData("in.ts", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "fromts.mp4", StreamSelection.All, "-map 0")]
    public void FileIsFfmpegs(string source, string make, string output, StreamSelection streams, string map)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string input = Path.Combine(dir, source), expected = Path.Combine(dir, "expected-" + output), actual = Path.Combine(dir, output);
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), .. make.Split(' '), input]);
            Ffmpeg(["-v", "error", "-y", "-i", input, .. map.Split(' '), "-c", "copy", "-fflags", "+bitexact", expected]);
            // "-map 0:a:0" is the first audio stream: by index there.
            var options = map == "-map 0:a:0"
                ? new RemuxOptions { StreamIndices = [1], Bitexact = true }
                : new RemuxOptions { Streams = streams, Bitexact = true };
            Remux.Copy(input, actual, options);
            byte[] want = File.ReadAllBytes(expected), got = File.ReadAllBytes(actual);
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
    public void ChaptersAndMetadataCome()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string meta = Path.Combine(dir, "meta.txt"), input = Path.Combine(dir, "in.mkv"), expected = Path.Combine(dir, "expected.mp4"), actual = Path.Combine(dir, "out.mp4");
            File.WriteAllText(meta, ";FFMETADATA1\ntitle=Remux test\nartist=Someone\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Two\n");
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), "-i", meta, "-map", "0", "-map", "1", "-map_metadata", "3", "-map_chapters", "3", "-c:v", "mpeg4", "-c:a", "aac", input]);
            Ffmpeg("-v", "error", "-y", "-i", input, "-map", "0", "-c", "copy", "-fflags", "+bitexact", expected);
            Remux.Copy(input, actual, new RemuxOptions { Bitexact = true });
            Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
            Assert.Throws<FrameReaderException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { Streams = StreamSelection.Subtitle }));
            Assert.Throws<FrameReaderException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { StreamIndices = [7] }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
