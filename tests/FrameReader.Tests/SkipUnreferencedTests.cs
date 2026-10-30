using System.Diagnostics;
using System.Security.Cryptography;
using FFmpeg.AutoGen;
using FrameReader;

/// <summary>
/// VideoFrameReader leaves packets of frames nothing references out of the decoder when it reads forward past them
/// (TryReadForwardTo, ReadAt, TryReadAt's seek, the TryTake methods): every frame it gives, its time and its tags, are
/// byte for byte those of decoding every packet, on each codec, container and guard. Needs SHOTDETECTOR_FFMPEG_LIBS
/// (with an ffmpeg that has libx264, libx265 and libxvid, as CI's); without it these do nothing.
/// </summary>
public class SkipUnreferencedTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }

    // Name, encoding, and whether packets are left out: true (some are), false (none: a guard), null (either).
    public static TheoryData<string, string, bool?> Sources => new()
    {
        { "h264.mp4", "-c:v libx264 -preset veryfast", true },               // b-pyramid: some B-frames are references
        { "nopyramid.mp4", "-c:v libx264 -preset veryfast -x264-params b-pyramid=none", true },
        { "h264.ts", "-c:v libx264 -preset veryfast", true },                // Annex B, the SPS in the packets
        { "opengop.ts", "-c:v libx264 -preset veryfast -x264-params open-gop=1:keyint=30", true }, // seeks land on non-IDR I-frames
        { "mbaff.mkv", "-c:v libx264 -preset veryfast -flags +ildct+ilme -x264-params interlaced=1", false }, // fields coded
        { "intrarefresh.ts", "-c:v libx264 -preset veryfast -x264-params intra-refresh=1:keyint=30", true }, // recovery points
        { "hevc.mp4", "-c:v libx265 -preset fast -x265-params log-level=error", true },
        { "hevc.ts", "-c:v libx265 -preset fast -x265-params log-level=error", true },
        { "mpeg1.mkv", "-c:v mpeg1video -q:v 3 -bf 2", true },
        { "mpeg2.ts", "-c:v mpeg2video -q:v 3 -bf 2", true },
        { "mpeg2.mpg", "-c:v mpeg2video -q:v 3 -bf 2", null },               // most packets without a pts: off from the first
        { "interlaced.mkv", "-c:v mpeg2video -q:v 3 -bf 2 -flags +ildct+ilme -top 1", false },
        { "mpeg4.mp4", "-c:v mpeg4 -q:v 3 -bf 2", true },
        { "mpeg4.avi", "-c:v mpeg4 -q:v 3 -bf 2", false },                   // B-frames without a pts
        { "xvid.avi", "-c:v libxvid -q:v 3 -bf 2", false },                  // DivX packed B-frames
        { "vp9.webm", "-c:v libvpx-vp9 -deadline realtime -cpu-used 8", false },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void FramesAreThoseOfDecodingEveryPacket(string name, string encode, bool? skips)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-skip-").FullName;
        try
        {
            // Shots of moving test patterns, so the encoders make B-frames, scene-cut I-frames and P-frames.
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=4", "-f", "lavfi", "-i", "mandelbrot=s=320x240:r=25",
                "-filter_complex", "[1]trim=duration=3,setpts=PTS-STARTPTS[m];[0][m]concat=n=2:v=1:a=0,format=yuv420p", "-g", "50", .. encode.Split(' '), path]);
            long skipped = 0, again = 0;
            foreach (int ahead in new[] { 0, 4 })
            {
                foreach (int threads in new[] { 1, 0 })
                {
                    var options = new VideoFrameReaderOptions
                    {
                        FrameFormat = FrameFormat.Gray8,
                        ReadAhead = ahead,
                        Decoder = new FrameDecoderOptions { Threads = threads },
                    };
                    foreach (double step in new[] { 1, 0.36 })
                        skipped += Same(path, options, $"{name} forward {step} s, ReadAhead {ahead}, threads {threads}", r => Forward(r, step), ref again);
                    skipped += Same(path, options, $"{name} at, ReadAhead {ahead}, threads {threads}", r => At(r, 0.7), ref again);
                    skipped += Same(path, options, $"{name} steps, ReadAhead {ahead}, threads {threads}", Steps, ref again);
                }
            }

            // ReadAt (two readers, the next time known while a slow handler works and decoding runs ahead).
            var times = Enumerable.Range(0, 30).Select(i => TimeSpan.FromSeconds(i * 0.2)).ToList();
            string ReadAt(bool every)
            {
                var lines = new string[times.Count];
                var options = new VideoFrameReaderOptions { FrameFormat = FrameFormat.Gray8, ReadAhead = 8, DecodeEveryPacket = every };
                int found = VideoFrameReader.ReadAt(path, times, options, 2, (i, f) =>
                {
                    lines[i] = Line(f);
                    Thread.Sleep(10);
                });
                return string.Join('\n', lines.Take(found));
            }

            Assert.Equal(ReadAt(true), ReadAt(false));
            if (skips is { } expected)
                Assert.True(expected == skipped > 0, $"{name}: {skipped} packets left out");

            // A short step after longer ones guesses the next time too late: read again where the input can go back to
            // a keyframe (mp4, mkv: their index's; MPEG-TS: one read), never guessed elsewhere (AVI, MPEG-PS: their seeks
            // can land late).
            bool indexed = Path.GetExtension(name) is ".mp4" or ".mkv" or ".ts";
            if (skips == true)
                Assert.True(indexed == again > 0, $"{name}: read again {again} times");
            else
                Assert.Equal(0, again);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static byte[] Hex(string hex) => Convert.FromHexString(hex);

    static byte[] Nals(params string[] nals) => [.. nals.SelectMany(n => Hex("000001" + n))];

    // H.264 SPSs as encoders write them: x264's (frame-coded, VUI with bitstream_restriction), an Apple encoder's (the
    // BBB trailer's: no bitstream_restriction, so FFmpeg guesses the reorder depth) and x264's MBAFF (fields coded).
    const string X264Sps = "6764001eacd940d83de6e1000003000100000300300f162d96";
    const string NoReorderSps = "274d401ea9181b07bcde00d4040406db0ad7bdf010";
    const string MbaffSps = "6764001eacd940d87bcdc2000003000200000300603e28532c";
    const string Idr = "65888400", NonReference = "019e00", Reference = "419a00";

    [Fact]
    public void H264SlicesWithoutReferencesAfterAnIdrOfAFrameCodedSpsWithAReorderDepth()
    {
        var u = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_H264, AVFieldOrder.AV_FIELD_UNKNOWN, [])!;
        Assert.False(u.Unreferenced(Nals(NonReference))); // no SPS yet
        Assert.False(u.Unreferenced(Nals(X264Sps, Idr)));
        Assert.True(u.Unreferenced(Nals(NonReference)));
        Assert.True(u.Unreferenced(Nals("09f0", "06051a", NonReference))); // with an access unit delimiter and SEI
        Assert.False(u.Unreferenced(Nals(Reference)));
        Assert.False(u.Unreferenced(Nals(NonReference, Reference)));

        // After a seek: only from the next IDR (FFmpeg holds back frames it hasn't recovered by their output order).
        u.Restart();
        Assert.False(u.Unreferenced(Nals(NonReference)));
        Assert.False(u.Unreferenced(Nals(Idr)));
        Assert.True(u.Unreferenced(Nals(NonReference)));

        // Length-prefixed (avcC, its SPS in the extradata).
        var sps = Hex(X264Sps);
        byte[] avcc = [1, 0x64, 0, 0x1e, 0xff, 0xe1, 0, (byte)sps.Length, .. sps, 1, 0, 4, 0x68, 0xeb, 0xe3, 0xcb];
        var iso = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_H264, AVFieldOrder.AV_FIELD_PROGRESSIVE, avcc)!;
        Assert.False(iso.Unreferenced([0, 0, 0, 4, .. Hex(Idr)]));
        Assert.True(iso.Unreferenced([0, 0, 0, 3, .. Hex(NonReference)]));
        Assert.False(iso.Unreferenced([0, 0, 0, 3, .. Hex(Reference)]));

        foreach (string other in new[] { NoReorderSps, MbaffSps })
        {
            var off = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_H264, AVFieldOrder.AV_FIELD_UNKNOWN, [])!;
            Assert.False(off.Unreferenced(Nals(other, Idr)));
            Assert.False(off.Unreferenced(Nals(NonReference)));
        }
    }

    [Fact]
    public void HevcSubLayerNonReferencePicturesOfTheTopLayer()
    {
        // An SPS with two temporal layers (sps_max_sub_layers_minus1 1): TRAIL_N of layer 0 may be referenced by layer 1.
        var u = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_HEVC, AVFieldOrder.AV_FIELD_PROGRESSIVE, [])!;
        Assert.False(u.Unreferenced(Nals("000201"))); // no SPS yet
        Assert.False(u.Unreferenced(Nals("40010c01", "42010301", "4401c172", "26010af0"))); // VPS, SPS, PPS, IDR
        Assert.True(u.Unreferenced(Nals("000201"))); // TRAIL_N, TemporalId 1
        Assert.False(u.Unreferenced(Nals("000101"))); // TRAIL_N, TemporalId 0
        Assert.False(u.Unreferenced(Nals("020201"))); // TRAIL_R
        Assert.True(u.Unreferenced(Nals("4e0105", "100201"))); // SEI, RASL_N
        Assert.Null(UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_HEVC, AVFieldOrder.AV_FIELD_TT, []));
    }

    [Fact]
    public void MpegBFramesAndDivXPackedOnes()
    {
        var mpeg2 = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_MPEG2VIDEO, AVFieldOrder.AV_FIELD_PROGRESSIVE, [])!;
        Assert.True(mpeg2.Unreferenced(Nals("000018ff", "01aa")));      // a B-picture and its slice
        Assert.False(mpeg2.Unreferenced(Nals("000008ff", "01aa")));     // an I-picture
        Assert.False(mpeg2.Unreferenced(Nals("b8000000", "000018ff"))); // a GOP header
        Assert.Null(UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_MPEG2VIDEO, AVFieldOrder.AV_FIELD_TB, []));

        var mpeg4 = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_MPEG4, AVFieldOrder.AV_FIELD_UNKNOWN, [])!;
        Assert.True(mpeg4.Unreferenced(Nals("b68000")));            // one B-VOP
        Assert.False(mpeg4.Unreferenced(Nals("b64000")));           // a P-VOP
        Assert.False(mpeg4.Unreferenced(Nals("b64000", "b68000"))); // two VOPs (packed)
        Assert.False(mpeg4.Unreferenced([.. Nals("b2"), .. "DivX503b1393p"u8, 0, .. Nals("b60000")]));
        Assert.False(mpeg4.Unreferenced(Nals("b68000"))); // DivX packed from here on

        // The same user data in the extradata.
        var packed = UnreferencedPackets.For(AVCodecID.AV_CODEC_ID_MPEG4, AVFieldOrder.AV_FIELD_UNKNOWN, [.. Nals("b2"), .. "DivX503b1393p"u8, 0])!;
        Assert.False(packed.Unreferenced(Nals("b68000")));
    }

    /// <summary>The frames <paramref name="read"/> gives with every packet decoded and with packets left out: the same.</summary>
    static long Same(string path, VideoFrameReaderOptions options, string what, Func<VideoFrameReader, List<string>> read)
    {
        long again = 0;
        return Same(path, options, what, read, ref again);
    }

    static long Same(string path, VideoFrameReaderOptions options, string what, Func<VideoFrameReader, List<string>> read, ref long again)
    {
        List<string> every, some;
        using (var reader = new VideoFrameReader(path, options with { DecodeEveryPacket = true }))
            every = read(reader);
        long skipped;
        using (var reader = new VideoFrameReader(path, options))
        {
            some = read(reader);
            skipped = reader.SkippedPackets;
            again += reader.ReadAgain;
        }

        Assert.True(every.Count > 5, what);
        for (int i = 0; i < Math.Max(every.Count, some.Count); i++)
            Assert.True(i < every.Count && i < some.Count && every[i] == some[i], $"{what}: frame {i} differs");
        return skipped;
    }

    static List<string> Forward(VideoFrameReader reader, double step)
    {
        var lines = new List<string>();
        for (int i = 0; reader.TryReadForwardTo(TimeSpan.FromSeconds(i * step), out var frame); i++)
        {
            lines.Add(Line(frame));

            // A take at the next time as well, every third (the same positioning, unconverted).
            if (i % 3 == 2 && reader.TryTakeForwardTo(TimeSpan.FromSeconds((i + 0.5) * step), out var taken))
            {
                using (taken)
                    lines.Add($"take {taken.Pts} {taken.Time?.Ticks}");
            }
        }

        return lines;
    }

    /// <summary>
    /// Forward reads at irregular steps, which the reader guesses the next time from after steps of one length: constant,
    /// a short step after them (the guess too late: what it left out is read again), increasing, constant again (guessed
    /// after more of them), a jump over more than a second, a read back, plain reads and takes in between (the next frame,
    /// which a guess may have left out), and past the end, then back (still none).
    /// </summary>
    static List<string> Steps(VideoFrameReader reader)
    {
        var lines = new List<string>();
        foreach (double t in new[] { 0, 0.5, 1, 1.5, 2, 2.04, 2.6, 3.3, 3.34, 3.38, 3.42, 3.46, 3.5, 3.54, -1, -2, 4.9, 5.1, 5.3, 5.32, -1, -2, 5.6, 5.9, 2, 6.2, -1, 6.24, 6.28, 6.9, 9, 6.95, -1 })
        {
            if (t == -2)
            {
                if (reader.TryTake(out var taken))
                {
                    using (taken)
                        lines.Add($"take {taken.Pts} {taken.Time?.Ticks}");
                }

                continue;
            }

            // Before the short step, decoding ahead (ReadAhead) has time to leave out frames past it: read again for sure,
            // also where the decoder has no frame threads to read ahead (MPEG-1/2).
            if (t == 2.04)
                Thread.Sleep(50);
            if (t < 0 ? reader.TryRead(out var frame) : reader.TryReadForwardTo(TimeSpan.FromSeconds(t), out frame))
                lines.Add(Line(frame));
        }

        return lines;
    }

    static List<string> At(VideoFrameReader reader, double step)
    {
        var lines = new List<string>();
        for (int i = 1; i * step < 6.9 && reader.TryReadAt(TimeSpan.FromSeconds(i * step), out var frame); i++)
        {
            // from 0.7 s: AVI's seek to 0 fails (backlog)
            lines.Add(Line(frame));
            if (reader.TryRead(out var next))
                lines.Add(Line(next)); // the frame after the one sought: decoded whole
        }

        return lines;
    }

    static string Line(VideoFrame f) =>
        $"{f.Index} {f.Pts} {f.Time.Ticks} {f.Width}x{f.Height} {f.SampleAspectRatio} {f.ColorRange} {f.ColorSpace} {f.ColorPrimaries} {f.ColorTransfer} {Convert.ToHexString(SHA256.HashData(f.Data))}";
}
