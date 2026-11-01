using System.Diagnostics;
using System.Globalization;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// Still pictures on the detection pass (<see cref="DetectionOptions.FreezeDetect"/>) are ffmpeg's freezedetect's, as its
/// log prints them (freeze_start, freeze_duration, freeze_end; the start alone for one still at the end), on every path:
/// the executable's graph, in-process decoding (FrameReader's FreezeDetector), FastYuv's pipeline, a time range,
/// deinterlacing (freezedetect after yadif), a Stream, 10-bit. The clips start at 0. The in-process cases need
/// SHOTDETECTOR_FFMPEG_LIBS; without it they do nothing.
/// </summary>
public sealed class FreezeDetectTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
    static readonly Lock MakeLock = new();
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "shotdetector-freeze-" + Environment.ProcessId);

    // Motion, a still held 2.5 s, motion, a still under light noise (1.2 s), motion, a still to the end (2.2 s). H.264, 25 fps.
    const string Stills = "-f lavfi -i testsrc2=s=320x240:r=25:d=1 -f lavfi -i testsrc2=s=320x240:r=25:d=1 -f lavfi -i testsrc2=s=320x240:r=25:d=1 "
        + "-filter_complex [0]tpad=stop_mode=clone:stop_duration=2.5[a];[1]tpad=stop_mode=clone:stop_duration=1.2,noise=alls=2:allf=t[b];"
        + "[2]tpad=stop_mode=clone:stop_duration=2.2[c];[a][b][c]concat=n=3:v=1 -c:v libx264 -bf 2 -crf 12 -pix_fmt {0}";

    static string Clip(string name, string format, string extra = "")
    {
        lock (MakeLock)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            if (!File.Exists(path))
            {
                string partial = path + ".part" + Path.GetExtension(path);
                Ffmpeg([.. "-v error -y".Split(' '), .. string.Format(CultureInfo.InvariantCulture, Stills, format).Split(' '), .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries), partial]);
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

    /// <summary>ffmpeg's freezedetect lines on the file, as one line per still picture (<see cref="FreezeSpan.ToString"/>'s).</summary>
    static List<string> Theirs(string path, FreezeDetectOptions o, string before = "")
    {
        var spans = new List<string>();
        foreach (string line in Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:v:0", "-vf", before + o.Filter(), "-f", "null", "-"]).Split('\n'))
        {
            int at = line.IndexOf("lavfi.freezedetect.", StringComparison.Ordinal);
            if (at < 0)
                continue;
            string[] kv = line[(at + "lavfi.freezedetect.".Length)..].Trim().Split(": ");
            if (kv[0] == "freeze_start")
                spans.Add($"freeze_start:{kv[1]}");
            else
                spans[^1] += $" {kv[0]}:{kv[1]}";
        }

        return spans;
    }

    public static TheoryData<string> Cases =>
    [
        "process", "process-d", "process-noise", "process-start", "process-fastyuv", "process-deinterlace", "process-stream", "process-10bit",
        "inprocess", "inprocess-d", "inprocess-noise", "inprocess-start", "inprocess-fastyuv", "inprocess-deinterlace", "inprocess-stream", "inprocess-10bit",
        "inprocess-parallel", "process-skip",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsFreezedetect(string name)
    {
        bool inProcess = name.StartsWith("inprocess", StringComparison.Ordinal);
        if (inProcess && Libs is null)
            return;
        string kind = name[(name.IndexOf('-') + 1)..];
        string path = kind == "10bit" ? Clip("stills10.mkv", "yuv420p10le")
            : kind == "deinterlace" ? Clip("stillsi.mp4", "yuv420p", "-flags +ildct+ilme -top 1")
            : kind == "stream" ? Clip("stills.mkv", "yuv420p")
            : Clip("stills.mp4", "yuv420p");
        var fz = kind switch
        {
            "d" => new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(1.04) },
            "noise" => new FreezeDetectOptions { Noise = 0.02, MinDuration = TimeSpan.FromSeconds(0.5) },
            _ => new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(0.5) },
        };
        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            FfmpegDirectory = Libs,
            Decoder = inProcess ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess,
            FreezeDetect = fz,
            Mode = DetectionMode.Sequential,
        };
        options = kind switch
        {
            "start" => options with { StartTime = "1.5s" },
            "fastyuv" => options with { Yuv420Converter = new SwscaleYuv420() },
            "parallel" => options with { Mode = DetectionMode.Parallel, Chunks = 3 },
            "deinterlace" => options with { Deinterlace = DeinterlaceMode.On },
            "skip" => options with { FrameSkip = 2 },
            _ => options,
        };
        using var stream = kind == "stream" ? File.OpenRead(path) : null;
        var result = stream is not null ? ShotDetection.Detect(stream, options) : ShotDetection.Detect(path, options);
        Assert.Equal(inProcess, result.Video.DecodedInProcess);
        var mine = result.FreezeSpans!.Select(s => s.ToString()).ToList();
        if (kind == "parallel")
        {
            // One pass, and why.
            Assert.Equal(1, result.Video.Chunks);
            Assert.Contains(result.Warnings, w => w.Contains("freeze detection", StringComparison.Ordinal));
        }

        if (kind == "start")
        {
            // From frame 38 (1.5 s) on: the first still (frames 25 to 87) is cut into, so its first frame is 38.
            Assert.Equal(38, result.Shots[0].Start.FrameNum);
            Assert.Equal(38, result.FreezeSpans![0].StartFrame);
            Assert.Equal(Theirs(path, fz).Skip(1), mine.Skip(1));
            return;
        }

        Assert.Equal(Theirs(path, fz, kind == "deinterlace" ? "yadif," : ""), mine);
        Assert.Null(result.FreezeSpans![^1].End); // still at the end
        Assert.True(result.FreezeSpans.Count >= 2);
    }

    [Fact]
    public void OptionsAreChecked()
    {
        foreach (var bad in new[] { new FreezeDetectOptions { MinDuration = TimeSpan.FromSeconds(-1) }, new FreezeDetectOptions { Noise = 1.5 }, new FreezeDetectOptions { Noise = double.NaN } })
            Assert.Throws<ArgumentException>(() => ShotDetection.Detect("missing.mp4", new DetectionOptions { FreezeDetect = bad }));
        Assert.Equal("freezedetect=n=0.001:d=2000000us", new FreezeDetectOptions().Filter());
        Assert.Equal("freeze_start:1.2 freeze_duration:3.04 freeze_end:4.24", new FreezeSpan(30, 106, 1.2, 4.24) { Duration = 3.04 }.ToString());
        Assert.Equal("freeze_start:1.2", new FreezeSpan(30, null, 1.2, null).ToString());
    }
}
