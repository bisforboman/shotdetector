using System.Diagnostics;
using FrameReader;

/// <summary>
/// VideoFrameReader's reads forward and seeks where the container's seeking is weak: MPEG-PS (.mpg, .vob), whose index
/// marks every PES with a dts as a keyframe, and AVI with B-frames, where ffmpeg's -ss margin goes before the first
/// index entry. With ReadAhead 0 and 2, skipping unreferenced frames as by default. Needs SHOTDETECTOR_FFMPEG_LIBS (with
/// an ffmpeg that has libxvid); without it these do nothing. The byte comparisons with ffmpeg's own frames run when the
/// ffmpeg executable is the libraries' own build; the full decode's frames are compared in every run.
/// </summary>
public class SeekDemuxerTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Ffmpeg => Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    static byte[] Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return output.ToArray();
    }

    public static TheoryData<string, string> Sources => new()
    {
        { "ps.mpg", "-c:v mpeg2video -bf 2 -g 12" },
        { "dvd.vob", "-c:v mpeg2video -bf 2 -g 12 -f vob" },
        { "bframes.avi", "-c:v mpeg4 -bf 2" },
        { "xvid.avi", "-c:v libxvid -bf 2" },
    };

    static string Make(string dir, string name, string encode)
    {
        string path = Path.Combine(dir, name);
        Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=8", .. encode.Split(' '), path]);
        return path;
    }

    static VideoFrameReaderOptions Options(int ahead) => new() { FrameFormat = FrameFormat.Gray8, ReadAhead = ahead, Decoder = new FrameDecoderOptions() };

    /// <summary>Every frame of a full decode, every packet decoded: its time and bytes.</summary>
    static List<(TimeSpan Time, byte[] Data)> FullDecode(string path)
    {
        var frames = new List<(TimeSpan, byte[])>();
        using var reader = new VideoFrameReader(path, Options(0) with { DecodeEveryPacket = true });
        while (reader.TryRead(out var frame))
            frames.Add((frame.Time, frame.Data.ToArray()));
        return frames;
    }

    // Forward to times further apart than a keyframe (so the reader would jump): the frame a full decode has there, as
    // ffmpeg's output-side -ss gives it. In MPEG-PS the jump landed past the target's keyframe (0.9 s gave 1.44 s).
    [Theory]
    [MemberData(nameof(Sources))]
    public void ForwardIsTheFullDecodesFrame(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seekdemux-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var full = FullDecode(path);
            bool sameBuild = AudioWriterTests.SameBuild();
            double[] times = [0, 0.912, 1.872, 3.312, 3.352, 5.712, 7.512]; // 0.8 of a frame past one: the trim's rounding (to AVI's 1/25) isn't in question
            foreach (int ahead in new[] { 0, 2 })
            {
                using var reader = new VideoFrameReader(path, Options(ahead));
                foreach (double t in times)
                {
                    var time = TimeSpan.FromSeconds(t);
                    var expected = full.First(f => f.Time >= time);
                    Assert.True(reader.TryReadForwardTo(time, out var frame), $"{name} {t} s");
                    Assert.True(expected.Time == frame.Time && frame.Data.SequenceEqual(expected.Data),
                        $"{name} {t} s, ReadAhead {ahead}: the frame at {frame.Time.TotalSeconds} s, the full decode's at {expected.Time.TotalSeconds} s");
                    if (sameBuild && ahead == 0)
                    {
                        byte[] ffmpegs = Run("-v", "error", "-i", path, "-ss", Arg(t), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-");
                        Assert.True(frame.Data.SequenceEqual(ffmpegs), $"{name} {t} s: not ffmpeg's frame");
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Seeks near the start, where ffmpeg's margin for formats that seek by dts goes before an AVI's first index entry:
    // ffmpeg's command line only warns and reads from the start; so does the reader (a fresh one and one that has read
    // on), rather than throwing "Operation not permitted". Further on, ffmpeg's -ss frame as before.
    [Theory]
    [MemberData(nameof(Sources))]
    public void SeekNearTheStartReadsFromTheStartAsFfmpeg(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seekdemux-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var full = FullDecode(path);
            bool sameBuild = AudioWriterTests.SameBuild();
            double[] times = [0, 0.032, 0.112, 0.912, 3.312];
            foreach (int ahead in new[] { 0, 2 })
            {
                using var reused = new VideoFrameReader(path, Options(ahead));
                foreach (double t in times)
                {
                    var time = TimeSpan.FromSeconds(t);
                    using var fresh = new VideoFrameReader(path, Options(ahead));
                    Assert.True(reused.TryReadForwardTo(TimeSpan.FromSeconds(2), out _)); // read on past the time first
                    foreach (var (reader, which) in new[] { (fresh, "fresh"), (reused, "reused") })
                    {
                        Assert.True(reader.TryReadAt(time, out var frame), $"{name} {t} s");
                        var expected = full.First(f => f.Time >= time);
                        bool fromStart = t < 0.13 && name.EndsWith(".avi", StringComparison.Ordinal); // the seek's point is before the first index entry
                        if (fromStart && which == "fresh")
                        {
                            // ffmpeg's: the failed seek drops what was read to probe, so it reads on from there (its frames as
                            // decoded without what came before, at the full decode's times).
                            Assert.Equal(expected.Time, frame.Time);
                        }
                        else if (fromStart)
                        {
                            // Having read on, it reads again from the start (where ffmpeg would be is gone): the full decode's frame.
                            Assert.True(expected.Time == frame.Time && frame.Data.SequenceEqual(expected.Data),
                                $"{name} {t} s, ReadAhead {ahead}, {which}: the frame at {frame.Time.TotalSeconds} s, the full decode's at {expected.Time.TotalSeconds} s");
                        }

                        if (sameBuild && ahead == 0 && !(fromStart && which == "reused"))
                        {
                            byte[] ffmpegs = Run("-v", "error", "-ss", Arg(t), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-");
                            Assert.True(frame.Data.SequenceEqual(ffmpegs), $"{name} {t} s, {which}: not ffmpeg's -ss frame");
                        }
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static string Arg(double t) => t.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
