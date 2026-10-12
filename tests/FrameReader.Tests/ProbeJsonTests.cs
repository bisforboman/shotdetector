using System.Diagnostics;
using System.Text.Json.Nodes;
using FrameReader;

/// <summary>
/// MediaProbe.ProbeJson against ffprobe's <c>-print_format json -show_format -show_streams</c> (issue #193): the same
/// keys and values, types included. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg and
/// ffprobe); without it these do nothing.
/// </summary>
public class ProbeJsonTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return stdout;
    }

    [Theory]
    [InlineData("h264.mp4", "-f lavfi -i testsrc2=s=320x240:r=25:d=2 -f lavfi -i sine=d=2 -c:v libx264 -pix_fmt yuv420p -c:a aac -metadata title=Probe")]
    [InlineData("rotated.mp4", "-display_rotation 90 -i {h264.mp4} -c copy")] // a display matrix in the side data
    [InlineData("interlaced.mxf", "-f lavfi -i testsrc2=s=720x576:r=25:d=2 -f lavfi -i sine=d=2 -c:v mpeg2video -flags +ilme+ildct -top 1 -c:a pcm_s16le -ar 48000")]
    [InlineData("surround.mkv", "-f lavfi -i testsrc2=s=160x120:r=30000/1001:d=2 -f lavfi -i sine=d=2 -f lavfi -i sine=f=660:d=2 -filter_complex [1][2]join=inputs=2:channel_layout=stereo,pan=5.1|FL=c0|FR=c1|FC=c0|LFE=c1|BL=c0|BR=c1[a] -map 0 -map [a] -c:v mpeg4 -c:a aac -metadata:s:a:0 language=swe -disposition:a:0 default")]
    [InlineData("tags.mp3", "-f lavfi -i sine=d=2 -c:a libmp3lame -b:a 128k -metadata artist=Someone -metadata album=Tests")]
    public void SameAsFfprobe(string name, string make)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-probejson-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            if (make.Contains("{h264.mp4}", StringComparison.Ordinal))
            {
                string source = Path.Combine(dir, "source.mp4");
                Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", source);
                make = make.Replace("{h264.mp4}", source, StringComparison.Ordinal);
            }

            Run("ffmpeg", ["-v", "error", "-y", .. make.Split(' '), path]);
            var want = JsonNode.Parse(Run("ffprobe", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path))!;
            var got = JsonNode.Parse(MediaProbe.ProbeJson(path))!;
            Assert.True(JsonNode.DeepEquals(want, got), $"{name}:\nffprobe: {want.ToJsonString()}\nours:    {got.ToJsonString()}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0.0078125, "0.007812")] // a tie at the 7th decimal: half to even, as C's printf
    [InlineData(0.0234375, "0.023438")]
    [InlineData(10.0, "10.000000")]
    [InlineData(-0.5, "-0.500000")]
    [InlineData(1.0 / 3, "0.333333")]
    public void TimesAreFormattedAsCsPrintf(double value, string expected) => Assert.Equal(expected, FfprobeJson.Fixed6(value));
}
