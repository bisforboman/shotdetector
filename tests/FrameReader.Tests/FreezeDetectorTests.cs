using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// FreezeDetector is FFmpeg 8.1's freezedetect: on decoded clips with still pictures (a clean one, one under light noise
/// that n tells apart, one running to the end, short ones under d), the spans' times print as freezedetect's log lines
/// (freeze_start, freeze_duration, freeze_end) at several n and d, in 8 and 10 bits, 4:2:0, 4:2:2, gray and NV12, odd sizes
/// (the chroma's last row left out, as freezedetect sizes it).
/// </summary>
public class FreezeDetectorTests
{
    static readonly string? Libs = TestLibraries.Load();

    // Motion, a still frame held (tpad clone), motion, a still with light noise (noise=alls=2), motion, a still to the end.
    const string Stills = "-f lavfi -i testsrc2=s={0}:r=25:d=1 -f lavfi -i testsrc2=s={0}:r=25:d=1 -f lavfi -i testsrc2=s={0}:r=25:d=1 "
        + "-filter_complex [0]tpad=stop_mode=clone:stop_duration=2.5[a];[1]tpad=stop_mode=clone:stop_duration=1.2,noise=alls=2:allf=t[b];"
        + "[2]tpad=stop_mode=clone:stop_duration=2.2[c];[a][b][c]concat=n=3:v=1,format={1}";

    [Theory]
    [InlineData("yuv420p", "320x240", "-c:v ffv1")]
    [InlineData("yuv422p10le", "321x241", "-c:v ffv1")]
    [InlineData("gray", "160x91", "-c:v ffv1")]
    [InlineData("nv12", "160x90", "-c:v rawvideo")]
    [InlineData("yuv420p", "161x91", "-c:v libx264 -bf 2 -crf 30")]
    public void IsFfmpegsFreezedetect(string format, string size, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-freeze-").FullName;
        try
        {
            string path = Path.Combine(dir, encode.Contains("ffv1", StringComparison.Ordinal) ? "stills.mkv" : encode.Contains("x264", StringComparison.Ordinal) ? "stills.mp4" : "stills.nut");
            Run("ffmpeg", ["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Stills, size, format).Split(' '), .. encode.Split(' '), path]);
            foreach (var (n, d) in new[] { (0.001, 2.0), (0.001, 0.5), (0.02, 0.5), (0.0, 0.0), (0.001, 1.04) })
            {
                var options = new FreezeOptions { Noise = n, MinDuration = TimeSpan.FromSeconds(d) };
                using var decoder = new FrameDecoder(path, new FrameDecoderOptions { Autorotate = false });
                FreezeDetector? detector = null;
                int frames = 0;
                while (decoder.Next())
                {
                    detector ??= FreezeDetector.For(decoder, options) ?? throw new InvalidOperationException(decoder.PixelFormat);
                    detector.Add(decoder);
                    frames++;
                }

                double tb = decoder.TimeBase.Num / (double)decoder.TimeBase.Den;
                var mine = new List<string>();
                foreach (var s in detector!.Finish())
                {
                    mine.Add("freeze_start: " + Time(s.StartPts * tb));
                    if (s.EndPts is { } end)
                    {
                        mine.Add("freeze_duration: " + Time(s.DurationMicros!.Value / 1e6));
                        mine.Add("freeze_end: " + Time(end * tb));
                    }
                }

                string log = Run("ffmpeg", ["-hide_banner", "-nostats", "-nostdin", "-copyts", "-i", path, "-map", "0:v:0",
                    "-vf", FormattableString.Invariant($"freezedetect=n={n}:d={d}"), "-f", "null", "-"]);
                var theirs = log.Split('\n').Where(l => l.Contains("lavfi.freezedetect.", StringComparison.Ordinal))
                    .Select(l => l[(l.IndexOf("lavfi.freezedetect.", StringComparison.Ordinal) + "lavfi.freezedetect.".Length)..].Trim()).ToList();
                Assert.True(theirs.SequenceEqual(mine), $"{format} {size} n={n} d={d}:\n ours   {string.Join(" | ", mine)}\n FFmpeg {string.Join(" | ", theirs)}");
                if (n == 0.001 && d == 0.5 && !encode.Contains("x264", StringComparison.Ordinal))
                {
                    // Lossless: the clean still and the one to the end (no freeze_end); the noisy one differs beyond n (x264 smooths it).
                    Assert.Equal(2, detector.Finish().Count);
                    Assert.Null(detector.Finish()[^1].EndPts);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RescalesAsFfmpeg()
    {
        // av_rescale_q, AV_ROUND_NEAR_INF: halves away from zero.
        Assert.Equal(40000, FreezeDetector.Rescale(1, 1, 25, 1, 1_000_000));
        Assert.Equal(33367, FreezeDetector.Rescale(1001, 1, 30000, 1, 1_000_000)); // 33366.67
        Assert.Equal(1, FreezeDetector.Rescale(1, 1, 2_000_000, 1, 1_000_000)); // 0.5 up
        Assert.Equal(-1, FreezeDetector.Rescale(-1, 1, 2_000_000, 1, 1_000_000)); // -0.5 away
        Assert.Throws<ArgumentException>(() => new FreezeDetector([(0, 1)], 8, new Rational(1, 25)));
        Assert.Throws<ArgumentException>(() => new FreezeDetector([(4, 4)], 8, new Rational(1, 25), new FreezeOptions { Noise = 2 }));
    }

    /// <summary>A time as FFmpeg 8.1 prints it (av_ts_make_time_string2).</summary>
    static string Time(double t)
    {
        double log = t == 0 ? double.NegativeInfinity : Math.Floor(Math.Log10(Math.Abs(t)));
        int precision = double.IsFinite(log) && log < 0 ? (int)-log + 5 : 6;
        string s = t.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }

    static string Run(string program, string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? program + ".exe" : program)) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return stderr;
    }
}
