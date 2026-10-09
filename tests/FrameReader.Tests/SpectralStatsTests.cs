using System.Diagnostics;
using System.Globalization;
using System.Text;
using FrameReader;

/// <summary>
/// SpectralStats against ffmpeg's command line: what <c>-af aspectralstats=...,ametadata=print</c> prints, window for
/// window and channel for channel. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg);
/// without it these do nothing.
/// </summary>
public class SpectralStatsTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static readonly string[] Names = ["mean", "variance", "centroid", "spread", "skewness", "kurtosis", "entropy", "flatness", "crest", "flux", "slope", "decrease", "rolloff"];

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

    /// <summary>ametadata=print's text: per frame its pts_time and the values by key.</summary>
    static List<(double Time, Dictionary<string, string> Values)> Printed(byte[] text)
    {
        var frames = new List<(double, Dictionary<string, string>)>();
        foreach (var line in Encoding.ASCII.GetString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("frame:"))
                frames.Add((double.Parse(line[(line.IndexOf("pts_time:") + 9)..], CultureInfo.InvariantCulture), []));
            else
                frames[^1].Item2[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
        }
        return frames;
    }

    [Theory]
    [InlineData("stereo.flac", null, null, null)]
    [InlineData("stereo.flac", 1024, "hamming", 0.75)]
    [InlineData("aac.m4a", 4096, "rect", 0.0)]
    public void MeasuresAreFfmpegs(string name, int? windowSize, string? function, double? overlap)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-spectral-").FullName;
        try
        {
            // Two different channels: a tone, and a higher tone with noise.
            string path = Path.Combine(dir, name);
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=sin(440*2*PI*t)|0.5*sin(3000*2*PI*t)+0.1*random(0):s=44100:d=2",
                "-c:a", name.EndsWith(".flac") ? "flac" : "aac", path);
            var options = new SpectralStatsOptions();
            options = options with { WindowSize = windowSize ?? options.WindowSize, WindowFunction = function ?? options.WindowFunction, Overlap = overlap ?? options.Overlap };
            string filter = string.Create(CultureInfo.InvariantCulture,
                $"aspectralstats=win_size={options.WindowSize}:win_func={options.WindowFunction}:overlap={options.Overlap},ametadata=print:file=-");
            var expected = Printed(Ffmpeg("-v", "error", "-i", path, "-af", filter, "-f", "null", "-"));

            using var audio = new AudioReader(path, new AudioReaderOptions { LibraryDirectory = Libs });
            using var stats = new SpectralStats(audio, options);
            int n = 0;
            while (stats.TryRead(out var window))
            {
                Assert.True(n < expected.Count, $"more windows than ffmpeg's {expected.Count}");
                var (time, values) = expected[n];
                Assert.True(Math.Abs(time - window.Time.TotalSeconds) < 1.5e-6, $"window {n}: ffmpeg at {time}, ours {window.Time.TotalSeconds}"); // printed to 6 decimals; TimeSpan has 100 ns ticks
                Assert.Equal(2, window.Channels.Length);
                for (int c = 0; c < 2; c++)
                {
                    var m = window.Channels[c];
                    double[] ours = [m.Mean, m.Variance, m.Centroid, m.Spread, m.Skewness, m.Kurtosis, m.Entropy, m.Flatness, m.Crest, m.Flux, m.Slope, m.Decrease, m.Rolloff];
                    for (int i = 0; i < Names.Length; i++)
                    {
                        string theirs = values[$"lavfi.aspectralstats.{c + 1}.{Names[i]}"];
                        Assert.True(SpectralStats.Parse(theirs).Equals(ours[i]), $"window {n} channel {c + 1} {Names[i]}: ffmpeg {theirs}, ours {ours[i]:R}");
                    }
                }
                n++;
            }
            Assert.Equal(expected.Count, n);
            Assert.True(n > 10);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParsesCsText()
    {
        Assert.Equal(681.56, SpectralStats.Parse("681.56"));
        Assert.Equal(-2.14656e-4, SpectralStats.Parse("-0.000214656"));
        Assert.Equal(7.63457e-05, SpectralStats.Parse("7.63457e-05"));
        Assert.Equal(double.PositiveInfinity, SpectralStats.Parse("inf"));
        Assert.Equal(double.NegativeInfinity, SpectralStats.Parse("-inf"));
        Assert.True(double.IsNaN(SpectralStats.Parse("-nan")));
    }
}
