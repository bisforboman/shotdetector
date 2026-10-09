using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// FrameDecoder through its public API, against ffmpeg/ffprobe on generated clips. Needs SHOTDETECTOR_FFMPEG_LIBS (an
/// FFmpeg 8.1 shared build's folder with ffmpeg and ffprobe); without it these do nothing. ShotDetector's in-process
/// tests check the frames byte for byte against the ffmpeg executable.
/// </summary>
public class FrameDecoderTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    static string Clip(string dir, string name, params string[] args)
    {
        string path = Path.Combine(dir, name);
        Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", .. args, path]);
        return path;
    }

    static FrameDecoderOptions Options => new() { LibraryDirectory = Libs };

    [Fact]
    public void DecodesEveryFrameWithFfmpegsTimestamps()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Clip(dir, "clip.mp4", "-c:v", "mpeg4", "-bf", "2");
            var expected = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp", "-of", "csv=p=0", path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            using var decoder = new FrameDecoder(path, Options);
            var pts = new List<string>();
            while (decoder.Next())
            {
                pts.Add(decoder.Pts.ToString(CultureInfo.InvariantCulture));
                Assert.Equal((320, 240, "yuv420p"), (decoder.Width, decoder.Height, decoder.PixelFormat));
                var y = decoder.GetPlane(0, out int ys);
                var u = decoder.GetPlane(1, out int us);
                Assert.True(ys >= 320 && y.Length == ys * 240);
                Assert.True(us >= 160 && u.Length == us * 120);
            }
            Assert.Equal(expected, pts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DeinterlacesRotatesAndReadsStreams()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            // Deinterlacing (FFmpeg's yadif, when the libraries have it): the same frame count, progressive frames.
            string interlaced = Clip(dir, "interlaced.mkv", "-c:v", "mpeg2video", "-flags", "+ilme+ildct");
            if (FFmpegLibraries.CanDeinterlace(Libs))
            {
                using var decoder = new FrameDecoder(interlaced, Options with { Deinterlace = true });
                int n = 0;
                while (decoder.Next())
                    n++;
                Assert.Equal(50, n);
            }

            // A 90-degree display matrix: frames come out turned, as ffmpeg's autorotate turns them.
            string plain = Clip(dir, "plain.mp4", "-c:v", "mpeg4"), rotated = Path.Combine(dir, "rotated.mp4");
            Run("ffmpeg", "-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated);
            using (var decoder = new FrameDecoder(rotated, Options))
            {
                Assert.True(decoder.Next());
                Assert.Equal((240, 320), (decoder.Width, decoder.Height));
            }

            // A stream (mkv: headers first): the same frames as the file.
            int FromFile()
            {
                using var d = new FrameDecoder(interlaced, Options);
                int n = 0;
                while (d.Next())
                    n++;
                return n;
            }
            using (var stream = File.OpenRead(interlaced))
            using (var decoder = new FrameDecoder(stream, Options))
            {
                int n = 0;
                while (decoder.Next())
                    n++;
                Assert.Equal(FromFile(), n);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoVideoIsInvalidInput()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-dec-").FullName;
        try
        {
            string path = Path.Combine(dir, "audio.wav");
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=d=1", path);
            var e = Assert.Throws<FrameReaderException>(() => new FrameDecoder(path, Options));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
