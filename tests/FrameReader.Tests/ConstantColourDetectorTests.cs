using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// ConstantColourDetector's figures are FFmpeg 8.1's signalstats: every frame's YLOW, YHIGH, ULOW, UHIGH, VLOW and
/// VHIGH exactly and YAVG, UAVG and VAVG to signalstats' printing, in 8 and 10 bits, 4:2:0, 4:2:2 and 4:4:4, odd sizes;
/// frames left out early are those whose YHIGH less YLOW is over the spread. Its runs are the flat ones, to the frame.
/// </summary>
public partial class ConstantColourDetectorTests
{
    static readonly string? Libs = TestLibraries.Load();

    // Motion, red for 3 s, motion, black for 3 s, motion, grey for 2.6 s to the end.
    const string Clip = "testsrc2={0}:d=2[a];color=c=red:{0}:d=3[b];testsrc2={0}:d=2[c];color=c=black:{0}:d=3[d];"
        + "testsrc2={0}:d=2[e];color=c=gray:{0}:d=2.6[f];[a][b][c][d][e][f]concat=n=6:v=1:a=0,format={1}";

    [Theory]
    [InlineData("yuv420p", "320x240")]
    [InlineData("yuv422p10le", "322x241")]
    [InlineData("yuv444p", "160x90")]
    public void IsFfmpegsSignalstats(string format, string size)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-colour-").FullName;
        try
        {
            string path = Path.Combine(dir, "colour.mkv");
            Run("ffmpeg", ["-v", "error", "-y", "-filter_complex", string.Format(CultureInfo.InvariantCulture, Clip, $"s={size}:r=25", format), "-c:v", "ffv1", path]);
            var options = new ConstantColourOptions();
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions { Autorotate = false });
            ConstantColourDetector? detector = null;
            while (decoder.Next())
            {
                var frame = decoder.TakeFrame();
                detector ??= ConstantColourDetector.For(PictureFormat.Of(frame, decoder.TimeBase)!, options);
                detector.Add(frame);
                frame.Dispose();
            }

            var spans = detector!.Finish();
            var expected = Signalstats(path);
            var frames = detector.Frames;
            Assert.Equal(expected.Count, frames.Count);
            int depth = format.Contains("10", StringComparison.Ordinal) ? 10 : 8;
            double spread = options.Spread * (1 << (depth - 8));
            for (int i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                if (frames[i] is not { } m)
                {
                    Assert.True(e.YHigh - e.YLow > spread, $"Frame {i} was left out, but its YHIGH less YLOW is {e.YHigh - e.YLow}.");
                    continue;
                }

                Assert.Equal((e.YLow, e.YHigh, e.ULow, e.UHigh, e.VLow, e.VHigh), (m.YLow, m.YHigh, m.ULow, m.UHigh, m.VLow, m.VHigh));
                Assert.Equal(e.YAvg, m.YAvg, 0.0006);
                Assert.Equal(e.UAvg, m.UAvg, 0.0006);
                Assert.Equal(e.VAvg, m.VAvg, 0.0006);
            }

            // The red, black and grey runs, the last to the end: each from its first frame to the frame after its last.
            Assert.Equal([(50, 125), (175, 250), (300, 365)], spans.Select(s => (s.FirstFrame, s.EndFrame)));
            double tb = decoder.TimeBase.Num / (double)decoder.TimeBase.Den;
            Assert.Equal([2.0, 7.0, 12.0], spans.Select(s => Math.Round(s.StartPts * tb, 3)));
            Assert.Equal(14.6, Math.Round(spans[^1].EndPts * tb, 3));

            // Red's mean colour: lavfi's red, BT.601 limited (81, 90, 240), within a code.
            Assert.Equal(81, spans[0].Y, 1.0);
            Assert.Equal(90, spans[0].Cb, 1.0);
            Assert.Equal(240, spans[0].Cr, 1.0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AShortRunIsLeftOut()
    {
        var detector = new ConstantColourDetector(4, 4, ChromaSubsampling.Yuv444, 8, new Rational(1, 25), new ConstantColourOptions { MinDuration = TimeSpan.FromSeconds(1) });
        byte[] grey = [.. Enumerable.Repeat((byte)128, 16)];
        for (int i = 0; i < 20; i++)
            detector.Add(i, grey, 4, grey, 4, grey, 4);

        Assert.Empty(detector.Finish());
    }

    [Fact]
    public void AColourChangeEndsARun()
    {
        var detector = new ConstantColourDetector(4, 4, ChromaSubsampling.Yuv444, 8, new Rational(1, 25), new ConstantColourOptions { MinDuration = TimeSpan.Zero });
        byte[] grey = [.. Enumerable.Repeat((byte)128, 16)], light = [.. Enumerable.Repeat((byte)140, 16)];
        for (int i = 0; i < 10; i++)
            detector.Add(i, i < 5 ? grey : light, 4, grey, 4, grey, 4);

        Assert.Equal([(0, 5, 128.0), (5, 10, 140.0)], detector.Finish().Select(s => (s.FirstFrame, s.EndFrame, s.Y)));
    }

    [Fact]
    public void RefusesWhatIsntAPicture()
    {
        Assert.Throws<ArgumentException>(() => new ConstantColourDetector(0, 4, ChromaSubsampling.Yuv420, 8, new Rational(1, 25)));
        Assert.Throws<ArgumentException>(() => new ConstantColourDetector(4, 4, ChromaSubsampling.Yuv420, 17, new Rational(1, 25)));
        var detector = new ConstantColourDetector(4, 4, ChromaSubsampling.Yuv420, 8, new Rational(1, 25));
        Assert.Throws<ArgumentException>(() => detector.Add(0, new byte[15], 4, new byte[4], 2, new byte[4], 2));
    }

    // signalstats,metadata=print's figures, frame by frame.
    static List<SignalStats> Signalstats(string path)
    {
        string log = Run("ffmpeg", ["-hide_banner", "-nostats", "-nostdin", "-i", path, "-an", "-vf", "signalstats,metadata=print", "-f", "null", "-"]);
        var frames = new List<SignalStats>();
        var values = new Dictionary<string, double>();
        void Flush()
        {
            if (values.Count == 0)
                return;
            ushort At(string key) => (ushort)values[key];
            frames.Add(new SignalStats(At("YLOW"), At("YHIGH"), (float)values["YAVG"], At("ULOW"), At("UHIGH"), (float)values["UAVG"], At("VLOW"), At("VHIGH"), (float)values["VAVG"]));
            values.Clear();
        }

        foreach (Match m in SignalstatsLine().Matches(log))
        {
            if (m.Groups[1].Success)
                Flush();
            else
                values[m.Groups[2].Value] = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        Flush();
        return frames;
    }

    [GeneratedRegex(@"(frame:\d+\s+pts:)|lavfi\.signalstats\.([YUV](?:LOW|HIGH|AVG))=(-?[\d.]+)")]
    private static partial Regex SignalstatsLine();

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
