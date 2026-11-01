using System.Diagnostics;
using System.Globalization;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Black spans on the detection pass (<see cref="DetectionOptions.BlackDetect"/>) are ffmpeg's blackdetect's, as its log
/// prints them (<see cref="BlackSpan.ToString"/>, %g), on every path: the executable's graph, in-process decoding
/// (FrameReader's black count), FastYuv's pipeline, chunks read in parallel, a time range, deinterlacing (blackdetect
/// after yadif, as detection reads the frames). The clips start at 0, so ffmpeg's times are the video stream's. The
/// in-process cases need SHOTDETECTOR_FFMPEG_LIBS (FFmpeg 8.1's shared libraries); without them they do nothing.
/// </summary>
public sealed class BlackDetectTests : IDisposable
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
    static readonly Lock MakeLock = new();
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "shotdetector-black-" + Environment.ProcessId);

    public void Dispose()
    {
    }

    // Programme fading to black, 2.5 s of black, fading in, programme, a short dip to black (0.4 s) and black to the end
    // (1.2 s: a run still black at the end ends at the last frame). H.264 with B-frames, 25 fps.
    const string Fades = "-f lavfi -i testsrc2=s=320x240:r=25:d=3,fade=out:st=2:d=1 -f lavfi -i color=black:s=320x240:r=25:d=2.5 "
        + "-f lavfi -i testsrc2=s=320x240:r=25:d=3,fade=in:st=0:d=1 -f lavfi -i color=black:s=320x240:r=25:d=0.4 "
        + "-f lavfi -i testsrc2=s=320x240:r=25:d=1 -f lavfi -i color=0x101010:s=320x240:r=25:d=1.2 "
        + "-filter_complex [0][1][2][3][4][5]concat=n=6:v=1 -c:v libx264 -bf 2 -pix_fmt {0}";

    static string Clip(string name, string format, string extra = "")
    {
        lock (MakeLock)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            if (!File.Exists(path))
            {
                string partial = path + ".part" + Path.GetExtension(path);
                Ffmpeg([.. "-v error -y".Split(' '), .. string.Format(CultureInfo.InvariantCulture, Fades, format).Split(' '), .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries), partial]);
                File.Move(partial, path);
            }

            return path;
        }
    }

    static string Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo(Libs is null ? "ffmpeg" : Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return stderr;
    }

    /// <summary>ffmpeg's blackdetect log lines on the file (the text after "] "), with the filters before it.</summary>
    static List<string> Theirs(string path, BlackDetectOptions o, string before = "") =>
        [.. Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:v:0", "-vf", before + o.Filter(o.MinDuration.TotalSeconds), "-f", "null", "-"])
            .Split('\n')
            .Where(l => l.Contains("[Parsed_blackdetect_", StringComparison.Ordinal) && l.Contains("black_start:", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf("] ", StringComparison.Ordinal) + 2)..].Trim())];

    public static TheoryData<string> Cases =>
    [
        "process", "process-d0", "process-thresholds", "process-start", "process-fastyuv", "process-parallel", "process-deinterlace",
        "inprocess", "inprocess-d0", "inprocess-thresholds", "inprocess-start", "inprocess-fastyuv", "inprocess-parallel", "inprocess-deinterlace",
        "process-10bit", "inprocess-10bit", "inprocess-full", "process-skip", "process-stream", "inprocess-stream", "process-picth1", "inprocess-picth1",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsBlackdetect(string name)
    {
        bool inProcess = name.StartsWith("inprocess", StringComparison.Ordinal);
        if (inProcess && Libs is null)
            return;
        string path = name.EndsWith("10bit", StringComparison.Ordinal) ? Clip("fades10.mkv", "yuv420p10le")
            : name.EndsWith("full", StringComparison.Ordinal) ? Clip("fadesfull.mp4", "yuvj420p")
            : name.EndsWith("deinterlace", StringComparison.Ordinal) ? Clip("fadesi.mp4", "yuv420p", "-flags +ildct+ilme -top 1")
            : name.EndsWith("stream", StringComparison.Ordinal) ? Clip("fades.mkv", "yuv420p")
            : Clip("fades.mp4", "yuv420p");
        var bd = name switch
        {
            _ when name.EndsWith("-d0", StringComparison.Ordinal) => new BlackDetectOptions { MinDuration = TimeSpan.Zero },
            _ when name.EndsWith("picth1", StringComparison.Ordinal) => new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.3), PictureThreshold = 1 },
            _ when name.EndsWith("thresholds", StringComparison.Ordinal) => new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.3), PictureThreshold = 0.9, PixelThreshold = 0.2 },
            _ => new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.3) },
        };
        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            FfmpegDirectory = Libs,
            Decoder = inProcess ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess,
            BlackDetect = bd,
            Mode = DetectionMode.Sequential,
        };
        options = name[(name.IndexOf('-') + 1)..] switch
        {
            "start" => options with { StartTime = "4s" },
            "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
            "parallel" => options with { Mode = DetectionMode.Parallel, Chunks = 3 },
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.On },
            "skip" => options with { FrameSkip = 2 },
            _ => options,
        };

        // Streamed (a Stream: ffmpeg's stdin or FrameDecoder's reader): times from the stream's start, as for a file.
        using var stream = name.EndsWith("stream", StringComparison.Ordinal) ? File.OpenRead(path) : null;
        var result = stream is not null ? ShotDetection.Detect(stream, options) : ShotDetection.Detect(path, options);
        if (stream is not null)
            Assert.True(result.Video.Streamed);
        Assert.Equal(inProcess, result.Video.DecodedInProcess);
        if (name.EndsWith("parallel", StringComparison.Ordinal))
            Assert.Equal(3, result.Video.Chunks);
        var mine = result.BlackSpans!.Select(s => s.ToString()).ToList();
        if (name.EndsWith("start", StringComparison.Ordinal))
        {
            // From the start frame (4 s: 100) on: the whole file's spans that end after it, the one it cuts into starting
            // there (all long enough at d=0.3 s).
            var whole = ShotDetection.Detect(path, options with { StartTime = null }).BlackSpans!;
            Assert.Equal(100, result.Shots[0].Start.FrameNum);
            Assert.Equal(whole.Where(s => s.EndFrame > 100).Select(s => (Math.Max(s.StartFrame, 100), s.EndFrame)), result.BlackSpans!.Select(s => (s.StartFrame, s.EndFrame)));
            return;
        }

        Assert.NotEmpty(mine);
        Assert.Equal(Theirs(path, bd, name.EndsWith("deinterlace", StringComparison.Ordinal) ? "yadif," : ""), mine);
        foreach (var s in result.BlackSpans!)
            Assert.True(s.StartFrame < s.EndFrame);
    }

    // The span's text is blackdetect's (FFmpeg 8.1's av_ts_make_time_string2: six decimals, more under a second, trailing
    // zeros and point dropped) for times of every size.
    [Theory]
    [InlineData(2.92, "2.92")]
    [InlineData(0, "0")]
    [InlineData(1.2083333333333333, "1.208333")]
    [InlineData(705.4166666666666, "705.416667")]
    [InlineData(1234567.5, "1234567.5")]
    [InlineData(0.04, "0.04")]
    [InlineData(0.0000125, "0.0000125")]
    [InlineData(0.041666666666666664, "0.0416667")]
    [InlineData(28, "28")]
    [InlineData(-0.5, "-0.5")]
    public void TimesPrintAsFfmpegs(double seconds, string printed) => Assert.Equal(printed, BlackSpan.Time(seconds));

    // blackdetect's minimum duration in the time base is d / av_q2d(tb) truncated to an integer (check_black_end): at
    // 1/25, 0.3 s is 7.5 units, so 7, and a run of 7 frames (0.28 s) is kept, one of 6 isn't. A run still black at the
    // end ends at its last frame; a run of one frame is a span at d=0 (black_end the next frame).
    [Fact]
    public void SpansAreBlackdetects()
    {
        var frames = new BlackFrames(0.04, 10);
        for (int i = 0; i < 30; i++)
            frames.Frames.Add(new BlackFrame(i, i is (>= 2 and < 9) or (>= 12 and < 18) or 20 or >= 23));
        var spans = frames.Spans(new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(0.3) }, frames.Frames.Count);
        Assert.Equal([(12, 19)], spans.Select(s => (s.StartFrame, s.EndFrame)));
        Assert.Equal("black_start:0.08 black_end:0.36 black_duration:0.28", spans[0].ToString());
        var all = frames.Spans(new BlackDetectOptions { MinDuration = TimeSpan.Zero }, frames.Frames.Count);
        Assert.Equal([(12, 19), (22, 28), (30, 31), (33, 39)], all.Select(s => (s.StartFrame, s.EndFrame)));
        Assert.Equal("black_start:0.92 black_end:1.16 black_duration:0.24", all[3].ToString()); // to the last frame
        Assert.Equal([(12, 19), (22, 24)], frames.Spans(new BlackDetectOptions { MinDuration = TimeSpan.Zero }, 15).Select(s => (s.StartFrame, s.EndFrame)));
    }

    [Fact]
    public void OptionsAreChecked()
    {
        foreach (var bad in new[] { new BlackDetectOptions { MinDuration = TimeSpan.FromSeconds(-1) }, new BlackDetectOptions { PictureThreshold = 1.5 }, new BlackDetectOptions { PixelThreshold = -0.1 }, new BlackDetectOptions { PixelThreshold = double.NaN } })
            Assert.Throws<ArgumentException>(() => ShotDetection.Detect("missing.mp4", new DetectionOptions { BlackDetect = bad }));
        Assert.Equal("blackdetect=d=2:pic_th=0.98:pix_th=0.1", new BlackDetectOptions().Filter(2));
    }
}
