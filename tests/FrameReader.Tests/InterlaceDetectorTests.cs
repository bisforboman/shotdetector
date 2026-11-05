using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// InterlaceDetector is FFmpeg 8's idet: on decoded clips of top field first, progressive, bottom field first and
/// telecined picture (fields repeated), its totals are those idet prints at the end (Repeated Fields, Single and Multi
/// frame detection), at its defaults and other thresholds, in 8, 10 and 16 bits, 4:2:0, 4:2:2, 4:4:4, gray and with
/// alpha, odd sizes (the chroma rounded up, as idet sizes it).
/// </summary>
public partial class InterlaceDetectorTests
{
    static readonly string? Libs = TestLibraries.Load();

    // Two seconds each: interlaced top field first, progressive, bottom field first, then 24p telecined to 30.
    const string Clip = "-f lavfi -i testsrc2=s={0}:r=50:d=2 -f lavfi -i testsrc2=s={0}:r=25:d=2 -f lavfi -i testsrc2=s={0}:r=50:d=2 "
        + "-f lavfi -i testsrc2=s={0}:r=24000/1001:d=2 -filter_complex "
        + "[0]interlace=scan=tff,setsar=1,fps=25[a];[1]setsar=1[b];[2]interlace=scan=bff,setsar=1,fps=25[c];"
        + "[3]telecine,setsar=1,fps=25[d];[a][b][c][d]concat=n=4:v=1,format={1}";

    [Theory]
    [InlineData("yuv420p", "320x240", "-c:v ffv1")]
    [InlineData("yuv422p10le", "321x241", "-c:v ffv1")]
    [InlineData("yuv444p16le", "160x90", "-c:v ffv1")]
    [InlineData("gray", "160x91", "-c:v ffv1")]
    [InlineData("yuva420p", "160x90", "-c:v ffv1")]
    [InlineData("yuv420p", "320x240", "-c:v libx264 -flags +ildct+ilme -crf 20")]
    public void IsFfmpegsIdet(string format, string size, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-idet-").FullName;
        try
        {
            string path = Path.Combine(dir, encode.Contains("x264", StringComparison.Ordinal) ? "fields.mp4" : "fields.mkv");
            Run("ffmpeg", ["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Clip, size, format).Split(' '), .. encode.Split(' '), path]);
            foreach (var (interlace, progressive, repeat) in new[] { (1.04f, 1.5f, 3f), (1.2f, 3f, 1.5f) })
            {
                var options = new IdetOptions { InterlaceThreshold = interlace, ProgressiveThreshold = progressive, RepeatThreshold = repeat };
                using var decoder = new FrameDecoder(path, new FrameDecoderOptions { Autorotate = false });
                InterlaceDetector? detector = null;
                while (decoder.Next())
                {
                    detector ??= InterlaceDetector.For(decoder, options) ?? throw new InvalidOperationException(decoder.PixelFormat);
                    detector.Add(decoder);
                }

                var t = detector!.Finish();
                string log = Run("ffmpeg", ["-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:v:0",
                    "-vf", FormattableString.Invariant($"idet=intl_thres={interlace}:prog_thres={progressive}:rep_thres={repeat}"), "-f", "null", "-"]);
                var repeated = Repeated().Matches(log)[^1];
                var frames = Totals().Matches(log);
                var single = frames.Last(m => m.Groups[1].Value == "Single");
                var multi = frames.Last(m => m.Groups[1].Value == "Multi");
                string mine = FormattableString.Invariant($"{t.RepeatedNeither} {t.RepeatedTop} {t.RepeatedBottom} | {t.SingleTff} {t.SingleBff} {t.SingleProgressive} {t.SingleUndetermined} | {t.MultiTff} {t.MultiBff} {t.MultiProgressive} {t.MultiUndetermined}");
                string theirs = $"{repeated.Groups[1].Value} {repeated.Groups[2].Value} {repeated.Groups[3].Value} | {single.Groups[2].Value} {single.Groups[3].Value} {single.Groups[4].Value} {single.Groups[5].Value} | {multi.Groups[2].Value} {multi.Groups[3].Value} {multi.Groups[4].Value} {multi.Groups[5].Value}";
                Assert.True(mine == theirs, $"{format} {size} {encode} {interlace}/{progressive}/{repeat}:\n ours   {mine}\n FFmpeg {theirs}");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The clip's parts tell apart: its bottom field first part, and the telecined part's repeated fields.
    [Fact]
    public void TellsFieldOrdersApart()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-idet-").FullName;
        try
        {
            string path = Path.Combine(dir, "fields.mkv");
            Run("ffmpeg", ["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Clip, "320x240", "yuv420p").Split(' '), "-c:v", "ffv1", path]);
            using var decoder = new FrameDecoder(path);
            InterlaceDetector? detector = null;
            while (decoder.Next())
            {
                detector ??= InterlaceDetector.For(decoder)!;
                detector.Add(decoder);
            }

            var t = detector!.Finish();
            Assert.True(t.MultiTff > t.MultiBff && t.MultiBff >= 40, $"{t}");
            Assert.True(t.RepeatedTop > 0 && t.RepeatedBottom > 0, $"{t}");
            Assert.Same(t.GetType(), detector.Finish().GetType());
            Assert.Throws<InvalidOperationException>(() => detector.Add(new byte[320 * 240], 320, new byte[160 * 120], 160, new byte[160 * 120], 160));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // On planes made here: a frame repeated is progressive-like (no combing against itself), and a plane too small is refused.
    [Fact]
    public void TakesPlanesOfYours()
    {
        var detector = new InterlaceDetector([(16, 16)], 8);
        var plane = new byte[16 * 16];
        for (int i = 0; i < plane.Length; i++)
            plane[i] = (byte)(i * 7);
        for (int i = 0; i < 5; i++)
            detector.Add(plane, 16);

        var t = detector.Finish();
        Assert.Equal(5, t.SingleTff + t.SingleBff + t.SingleProgressive + t.SingleUndetermined);
        Assert.Equal(5, t.RepeatedNeither + t.RepeatedTop + t.RepeatedBottom);
        Assert.Throws<ArgumentException>(() => new InterlaceDetector([(16, 16)], 8).Add(new byte[100], 16));
        Assert.Throws<ArgumentException>(() => new InterlaceDetector([], 8));
        Assert.Throws<ArgumentException>(() => new InterlaceDetector([(16, 16)], 7));
    }

    [GeneratedRegex(@"Repeated Fields: Neither:\s*(\d+) Top:\s*(\d+) Bottom:\s*(\d+)")]
    private static partial Regex Repeated();

    [GeneratedRegex(@"(Single|Multi) frame detection: TFF:\s*(\d+) BFF:\s*(\d+) Progressive:\s*(\d+) Undetermined:\s*(\d+)")]
    private static partial Regex Totals();

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
