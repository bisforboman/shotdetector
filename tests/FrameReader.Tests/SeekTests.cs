using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// VideoFrameReader's seek against ffmpeg's: the frame <c>ffmpeg -ss T -i X -frames:v 1</c> gives, byte for byte.
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class SeekTests
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
    [InlineData("bframes.mp4", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("bframes.mkv", "-c:v mpeg4 -bf 2 -g 12")]
    [InlineData("late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10")] // starts at 10 s
    public void SeeksToFfmpegsFrame(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seek-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", .. encode.Split(' '), path]);
            using var reader = new VideoFrameReader(path, new FrameReaderOptions
                { Width = 160, Height = 90, Format = FrameFormat.Gray8, Decoder = new FrameDecoderOptions { LibraryDirectory = Libs } });
            // From the start, mid-GOP, on frame boundaries (1.04 s is frame 26) and just before them, near and past the end;
            // backwards too, after reading on.
            foreach (double t in new[] { 0, 0.5, 1.04, 1.039, 1.0, 0.999, 2.9, 1.3, 0.1, 3.5 })
            {
                byte[] expected = Ffmpeg("-v", "error", "-ss", t.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1",
                    "-vf", "scale=160:90:flags=bicubic,format=gray", "-f", "rawvideo", "-");
                bool got = reader.TryReadAt(TimeSpan.FromSeconds(t), out var frame);
                Assert.Equal(expected.Length > 0, got);
                if (got)
                {
                    Assert.True(expected.AsSpan().SequenceEqual(frame.Data), $"{name} at {t} s: not ffmpeg's frame (got {frame.Time})");
                    Assert.Equal(0, frame.Index);
                    Assert.True(frame.Time >= TimeSpan.FromSeconds(t) - TimeSpan.FromMilliseconds(1), $"{name} at {t} s: frame at {frame.Time}");
                }
                // Reading on from there gives the next frame.
                if (got && t < 2.5)
                {
                    Assert.True(reader.TryRead(out var next));
                    Assert.Equal(1, next.Index);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
