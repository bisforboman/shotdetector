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

    // freezedetect's state on planes made here (one 8x8 plane, 25 fps, d = 2 frames: 80000 µs): a picture differing
    // exactly d after the still's first is a span (micros >= d); a still is opened once, where d is reached, so the spans
    // as of an earlier frame count keep it running from there (Finish(frames)).
    [Fact]
    public void StateIsFreezedetects()
    {
        var options = new FreezeOptions { MinDuration = TimeSpan.FromMilliseconds(80) };
        byte[] a = new byte[64], b = new byte[64];
        Array.Fill(b, (byte)200);

        // Frames 0, 1 the same picture, frame 2 another, 80 ms after frame 0: start and end at frame 2.
        var exact = new FreezeDetector([(8, 8)], 8, new Rational(1, 25), options);
        foreach (var (pts, plane) in new[] { (0L, a), (1L, a), (2L, b) })
            exact.Add(pts, plane, 8);
        Assert.Equal([new FrozenSpan(0, 2, 0, 2, 80000)], exact.Finish());

        // Frames 0 to 9 the same picture: opened at frame 2 (d reached), running; as of 3 frames running too, as of 2 none.
        var still = new FreezeDetector([(8, 8)], 8, new Rational(1, 25), options);
        for (long pts = 0; pts < 10; pts++)
            still.Add(pts, a, 8);
        Assert.Equal([new FrozenSpan(0, null, 0, null, null)], still.Finish());
        Assert.Equal([new FrozenSpan(0, null, 0, null, null)], still.Finish(3));
        Assert.Empty(still.Finish(2));
        still.Add(10, b, 8);
        Assert.Equal([new FrozenSpan(0, 10, 0, 10, 400000)], still.Finish());
        Assert.Equal([new FrozenSpan(0, null, 0, null, null)], still.Finish(10));
    }

    // freezedetect sizes 4:2:0's chroma planes at the height shifted down: at an odd height their last row is left out,
    // so frames differing only there are one still picture, as ffmpeg's filter says too. At 256 wide against ffmpeg as it
    // runs; at 64 against its C code (-cpuflags 0): x86's SIMD SAD (scene_sad.asm) reads a whole vector of each row of a
    // plane narrower than one (32 bytes here, under AVX-512's 64) and the C tail then reads the row again, so there the
    // figures depend on the CPU; the detector is FFmpeg's C code.
    [Theory]
    [InlineData(256, false)]
    [InlineData(64, true)]
    public void OddHeightsLeaveTheLastChromaRowOut(int w, bool cOnly)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-freeze-odd-").FullName;
        try
        {
            int h = 47, cw = w / 2, ch = 24;
            string raw = Path.Combine(dir, "frames.yuv"), path = Path.Combine(dir, "odd.nut");
            using (var file = File.Create(raw))
            {
                for (int f = 0; f < 80; f++)
                {
                    var frame = new byte[(w * h) + (2 * cw * ch)];
                    Array.Fill(frame, (byte)128);
                    byte last = (byte)(f % 2 == 0 ? 16 : 240);
                    frame.AsSpan((w * h) + (cw * (ch - 1)), cw).Fill(last); // U's last row
                    frame.AsSpan((w * h) + (cw * ch) + (cw * (ch - 1)), cw).Fill(last); // V's
                    file.Write(frame);
                }
            }

            Run("ffmpeg", ["-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{w}x{h}", "-r", "25", "-i", raw, "-c:v", "rawvideo", path]);
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions());
            FreezeDetector? detector = null;
            while (decoder.Next())
                (detector ??= FreezeDetector.For(decoder)!).Add(decoder);
            Assert.Equal([0], detector!.Finish().Select(s => s.FirstFrame));
            string log = Run("ffmpeg", ["-hide_banner", "-nostats", "-nostdin", .. cOnly ? new[] { "-cpuflags", "0" } : [], "-i", path, "-vf", "freezedetect", "-f", "null", "-"]);
            Assert.Contains("lavfi.freezedetect.freeze_start: 0", log, StringComparison.Ordinal);
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
