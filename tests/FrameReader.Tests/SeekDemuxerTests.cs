using System.Diagnostics;
using FFmpeg.AutoGen;
using FrameReader;

/// <summary>
/// VideoFrameReader's reads forward and seeks where the container's seeking is weak: MPEG-PS (.mpg, .vob), whose index
/// marks every PES with a dts as a keyframe; AVI with B-frames, where ffmpeg's -ss margin goes before the first index
/// entry; MP4 with MPEG-4 Part 2 B-frames, indexed by dts; FLV, whose seeks near the start fail; raw elementary streams
/// (.m2v, .m4v, .h264, .hevc), which have no timestamps at all. With ReadAhead 0 and 2, skipping unreferenced frames as
/// by default. Needs SHOTDETECTOR_FFMPEG_LIBS (with an ffmpeg that has libxvid, libx264 and libx265); without it these do
/// nothing, and formats whose demuxer or decoder the libraries lack are left out. The byte comparisons with ffmpeg's own
/// frames run when the ffmpeg executable is the libraries' own build; the full decode's frames are compared in every run.
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
        { "mpeg4.mp4", "-c:v mpeg4 -bf 2" }, // what ffmpeg writes to .m4v too (its ipod muxer)
        { "x264.flv", "-c:v libx264 -bf 2" }, // one keyframe
        { "gop.flv", "-c:v libx264 -bf 2 -g 12" },
        { "raw.m2v", "-c:v mpeg2video -bf 2 -g 12" },
        { "raw.m4v", "-c:v mpeg4 -bf 2 -f m4v" },
        { "raw.h264", "-c:v libx264 -bf 2" },
        { "raw.hevc", "-c:v libx265 -bf 2" },
    };

    /// <summary>Whether the libraries read this source (FrameReader.Native has every demuxer and these decoders; other builds may not).</summary>
    static unsafe bool Readable(string name)
    {
        var (demuxer, decoder) = name switch
        {
            "raw.m2v" => ("mpegvideo", "mpeg2video"),
            "raw.m4v" => ("m4v", "mpeg4"),
            "raw.h264" => ("h264", "h264"),
            "raw.hevc" => ("hevc", "hevc"),
            _ when name.EndsWith(".flv", StringComparison.Ordinal) => ("flv", "h264"),
            _ => (null, null),
        };
        return demuxer is null || (ffmpeg.av_find_input_format(demuxer) != null && ffmpeg.avcodec_find_decoder_by_name(decoder) != null);
    }

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
    // ffmpeg's output-side -ss gives it. In MPEG-PS the jump landed past the target's keyframe (0.9 s gave 1.44 s); in
    // MP4 with MPEG-4 Part 2 on the keyframe after the target (3.312 s gave 3.36 s: its index is by dts, and the B-frames
    // before the keyframe don't decode after a jump). Raw streams are timed as ffmpeg's command line times them (it drops
    // their parser's timestamps: raw.m2v's frames were a frame late) and are read through (no index to jump by).
    [Theory]
    [MemberData(nameof(Sources))]
    public void ForwardIsTheFullDecodesFrame(string name, string encode)
    {
        if (Libs is null || !Readable(name))
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

                // Again from the start, now that the index knows the whole file (a raw stream's is built as it's read):
                // jumps where the reader would make them (AVI: from the start to 1.072 s gave 1.04 s's frame).
                reader.Seek(TimeSpan.Zero);
                foreach (double t in new[] { 1.072, 2.112, 3.312, 5.712 })
                {
                    var expected = full.First(f => f.Time >= TimeSpan.FromSeconds(t));
                    Assert.True(reader.TryReadForwardTo(TimeSpan.FromSeconds(t), out var frame), $"{name} {t} s after Seek(0)");
                    Assert.True(expected.Time == frame.Time && frame.Data.SequenceEqual(expected.Data),
                        $"{name} {t} s after Seek(0), ReadAhead {ahead}: the frame at {frame.Time.TotalSeconds} s, the full decode's at {expected.Time.TotalSeconds} s");
                }
            }

            // ReadAt's readers each read forward: the same frames.
            var found = new byte[times.Length][];
            int count = VideoFrameReader.ReadAt(path, [.. times.Select(TimeSpan.FromSeconds)], Options(0), 2, (i, frame) => found[i] = frame.Data.ToArray());
            Assert.Equal(times.Length, count);
            for (int i = 0; i < times.Length; i++)
                Assert.True(found[i].SequenceEqual(full.First(f => f.Time >= TimeSpan.FromSeconds(times[i])).Data), $"{name} ReadAt {times[i]} s");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Seeks ffmpeg's -ss can't make. Near the start, ffmpeg's margin for formats that seek by dts goes before an AVI's
    // or FLV's first index entry: the container refuses, and ffmpeg's command line only warns and reads on from where the
    // refused seek left the input, past the first keyframe (FLV: a frame 5.28 s in, or none with one keyframe). A raw
    // stream drops its timestamps, so ffmpeg's -ss lands on some keyframe and keeps every frame from there (raw.m2v at
    // 0.912 s gave 0.52 s's), or finds none at all (H.264 and HEVC without timestamps). There the reader gives the full
    // decode's frame (a fresh one and one that has read on), from the start; elsewhere ffmpeg's -ss frame as before.
    // TryTakeAt positions as TryReadAt.
    [Theory]
    [MemberData(nameof(Sources))]
    public void SeekNearTheStartReadsFromTheStartAsFfmpeg(string name, string encode)
    {
        if (Libs is null || !Readable(name))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seekdemux-").FullName;
        try
        {
            string path = Make(dir, name, encode);
            var full = FullDecode(path);
            bool sameBuild = AudioWriterTests.SameBuild();
            bool raw = name.StartsWith("raw.", StringComparison.Ordinal);
            double[] times = [0, 0.032, 0.112, 0.912, 3.312];
            foreach (int ahead in new[] { 0, 2 })
            {
                using var reused = new VideoFrameReader(path, Options(ahead));
                foreach (double t in times)
                {
                    var time = TimeSpan.FromSeconds(t);
                    using var fresh = new VideoFrameReader(path, Options(ahead));
                    Assert.True(reused.TryReadForwardTo(TimeSpan.FromSeconds(2), out _)); // read on past the time first
                    var expected = full.First(f => f.Time >= time);

                    // The seek's point is before the first index entry (AVI: the margin; FLV: also its start, 0.08 s).
                    bool refused = (t < 0.13 && name.EndsWith(".avi", StringComparison.Ordinal)) || (t < 0.05 && name.EndsWith(".flv", StringComparison.Ordinal));
                    foreach (var (reader, which) in new[] { (fresh, "fresh"), (reused, "reused") })
                    {
                        Assert.True(reader.TryReadAt(time, out var frame), $"{name} {t} s, {which}");
                        if (refused || raw)
                        {
                            Assert.True(expected.Time == frame.Time && frame.Data.SequenceEqual(expected.Data),
                                $"{name} {t} s, ReadAhead {ahead}, {which}: the frame at {frame.Time.TotalSeconds} s, the full decode's at {expected.Time.TotalSeconds} s");
                        }
                        else if (sameBuild && ahead == 0)
                        {
                            byte[] ffmpegs = Run("-v", "error", "-ss", Arg(t), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-");
                            Assert.True(frame.Data.SequenceEqual(ffmpegs), $"{name} {t} s, {which}: not ffmpeg's -ss frame");
                        }

                        long pts = frame.Pts;
                        Assert.True(reader.TryTakeAt(time, out var taken), $"{name} {t} s, {which}: TryTakeAt");
                        using (taken)
                            Assert.Equal(pts, taken.Pts);
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
