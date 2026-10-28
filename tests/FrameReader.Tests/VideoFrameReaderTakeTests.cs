using System.Diagnostics;
using System.Security.Cryptography;
using FFmpeg.AutoGen;
using FrameReader;

/// <summary>
/// VideoFrameReader's TryTake methods (issue #574): the decoder's own frames, unconverted, at exactly the positions the
/// reads find (keyframe jumps, the ffmpeg-like seek, ReadAhead's queue, deinterlacing, autorotation); reads and takes
/// mixed on one reader; frames the caller owns, valid after the next read and after the reader. Needs
/// SHOTDETECTOR_FFMPEG_LIBS; without it these do nothing.
/// </summary>
public class VideoFrameReaderTakeTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
    }

    static string Make(string dir, string name, string encode, string mux, double seconds = 6)
    {
        string path = Path.Combine(dir, name), plain = Path.Combine(dir, "plain" + Path.GetExtension(name));
        Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s=320x240:r=25:d={seconds}", .. encode.Split(' '), mux == "" ? path : plain]);
        if (mux != "")
            Ffmpeg(["-v", "error", "-y", .. mux.Split(' '), "-i", plain, "-c", "copy", path]);
        return path;
    }

    /// <summary>A decoded frame as a line: its times, size, format and the hash of its pixels (each row's, without padding).</summary>
    internal static string Describe(DecodedFrame f)
    {
        var format = ffmpeg.av_get_pix_fmt(f.PixelFormat);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int p = 0; p < 4; p++)
        {
            int bytes = ffmpeg.av_image_get_linesize(format, f.Width, p);
            if (bytes <= 0)
                break;
            var plane = f.GetPlane(p, out int stride);
            for (int row = 0; row + bytes <= plane.Length; row += stride)
                hash.AppendData(plane.Slice(row, bytes));
        }

        return $"{f.Pts} {f.Time?.Ticks} {f.Width}x{f.Height} {f.PixelFormat} {Convert.ToHexString(hash.GetHashAndReset())}";
    }

    public static TheoryData<string, string, string, int> Sources => new()
    {
        { "bframes.mp4", "-c:v mpeg4 -bf 2 -g 12", "", 0 },                         // indexed: TryTakeForwardTo jumps
        { "opengop.mkv", "-c:v mpeg2video -bf 2 -g 12", "", 0 },                    // open GOPs: jumps from a keyframe early
        { "late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10", "", 0 },   // no index, starts at 10 s
        { "rotated.mov", "-c:v mpeg4 -g 12", "-display_rotation 90", 0 },           // autorotate
        { "interlaced.mkv", "-c:v mpeg2video -flags +ilme+ildct -g 12", "", 1 },     // FFmpeg's yadif
        { "interlaced-rows.mkv", "-c:v mpeg2video -flags +ilme+ildct -g 12", "", 2 }, // row-wise yadif
    };

    // Each take is the frame the decoder has there (all frames: the decoder's sequence; forward: the full decode's frame at
    // that pts; at a time: the frame ffmpeg's seek decodes, open-GOP B-frames and all), with the read's index, pts and
    // time; reads and takes alternate on one reader without moving each other; ReadAhead 0, 2 and 8 alike.
    [Theory]
    [MemberData(nameof(Sources))]
    public void TakesAreTheDecodersFramesWhereTheReadsAre(string name, string encode, string mux, int deinterlace)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-take-").FullName;
        try
        {
            string path = Make(dir, name, encode, mux);
            var decoderOptions = new FrameDecoderOptions
            {
                Deinterlace = deinterlace > 0,
                RowDeinterlacer = deinterlace == 2 ? new FrameDecoderTests.Averaging() : null,
            };
            bool canSeek = deinterlace == 0;

            // The decoder's frames, in order and by pts.
            var sequence = new List<string>();
            var byPts = new Dictionary<long, string>();
            using (var decoder = new FrameDecoder(path, decoderOptions))
            {
                while (decoder.Next())
                {
                    using var f = decoder.TakeFrame();
                    sequence.Add(Describe(f));
                    byPts[f.Pts] = sequence[^1];
                }
            }

            // ffmpeg's seek, on the decoder itself.
            string Sought(double t)
            {
                using var decoder = new FrameDecoder(path, decoderOptions);
                long keep = decoder.SeekLikeFfmpeg(TimeSpan.FromSeconds(t));
                while (decoder.Next())
                {
                    if (decoder.Pts + keep >= 0)
                    {
                        using var f = decoder.TakeFrame();
                        return Describe(f);
                    }
                }

                return "end";
            }

            List<string> Run(int ahead)
            {
                var lines = new List<string>();

                // Converting options don't reach the takes: the frames come at their own size and format.
                var options = new VideoFrameReaderOptions { MaxWidth = 200, FrameFormat = FrameFormat.Yuv420p, Decoder = decoderOptions, ReadAhead = ahead };
                using (var reader = new VideoFrameReader(path, options))
                {
                    for (int i = 0; ; i++)
                    {
                        if (!reader.TryTake(out var f))
                        {
                            Assert.Equal(sequence.Count, i);
                            break;
                        }

                        using (f)
                            Assert.True(sequence[i] == Describe(f), $"{name} ReadAhead {ahead}, frame {i}");
                    }

                    Assert.False(reader.TryTake(out var none));
                    Assert.Null(none);
                }

                // Forward, with jumps over GOPs; even steps read, odd ones take, against a reader that only reads.
                using (var reader = new VideoFrameReader(path, options))
                using (var reads = new VideoFrameReader(path, options))
                {
                    int step = 0;
                    foreach (double t in new[] { 0, 0.3, 1.0, 1.0, 1.04, 2, -1, -1, 3.3, 3.3, -1, 4.1, 5.9, 5.9, 7 })
                    {
                        bool read = t < 0 ? reads.TryRead(out var expected) : reads.TryReadForwardTo(TimeSpan.FromSeconds(t), out expected);
                        string at = $"{name} ReadAhead {ahead}, to {t} (step {step})";
                        if (step++ % 2 == 0)
                        {
                            Assert.Equal(read, t < 0 ? reader.TryRead(out var frame) : reader.TryReadForwardTo(TimeSpan.FromSeconds(t), out frame));
                            if (read)
                            {
                                Assert.True((expected.Index, expected.Pts, expected.Time) == (frame.Index, frame.Pts, frame.Time), at);
                                Assert.True(expected.Data.SequenceEqual(frame.Data), at);
                            }

                            lines.Add($"read {t} " + (read ? $"{frame.Index} {frame.Pts}" : "end"));
                            continue;
                        }

                        DecodedFrame? taken;
                        Assert.Equal(read, t < 0 ? reader.TryTake(out taken) : reader.TryTakeForwardTo(TimeSpan.FromSeconds(t), out taken));
                        if (!read)
                        {
                            Assert.Null(taken);
                            lines.Add($"take {t} end");
                            continue;
                        }

                        using (taken)
                        {
                            Assert.True((expected.Pts, expected.Time) == (taken!.Pts, taken.Time), at);
                            Assert.True(byPts[taken.Pts] == Describe(taken), at);
                            lines.Add($"take {t} {Describe(taken)}");
                        }
                    }
                }

                if (canSeek)
                {
                    using var reader = new VideoFrameReader(path, options);
                    foreach (double t in new[] { 2.0, 0.5, 4.1, 0.0, 9.0, 1.0 })
                    {
                        string expected = Sought(t);
                        bool got = reader.TryTakeAt(TimeSpan.FromSeconds(t), out var taken);
                        Assert.Equal(expected != "end", got);
                        if (!got)
                        {
                            lines.Add($"at {t} end");
                            continue;
                        }

                        using (taken)
                        {
                            Assert.True(expected == Describe(taken!), $"{name} ReadAhead {ahead}, at {t}");
                            lines.Add($"at {t} {expected}");
                        }

                        // Reading on from there: the next frame, numbered 1.
                        if (reader.TryRead(out var next))
                            lines.Add($"next {next.Index} {next.Pts}");
                    }
                }

                return lines;
            }

            var without = Run(0);
            Assert.Equal(without, Run(2));
            Assert.Equal(without, Run(8));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A taken frame is the caller's: unchanged after the reads that follow and after the reader is disposed (read ahead,
    // the queue lets go of its own reference only); disposed mid-decode with frames held, the reader lets go of all of its.
    [Fact]
    public void TakenFramesOutliveReadsAndTheReader()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-take-").FullName;
        try
        {
            string path = Make(dir, "h264.mp4", "-c:v libx264 -preset ultrafast -g 25 -pix_fmt yuv420p", "", 4);
            foreach (int ahead in new[] { 0, 1, 2 })
            {
                var held = new List<(DecodedFrame Frame, string Line)>();
                try
                {
                    using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = ahead }))
                    {
                        foreach (double t in new[] { 0, 0.5, 0.5, 1, 2.5 })
                        {
                            Assert.True(reader.TryTakeForwardTo(TimeSpan.FromSeconds(t), out var f));
                            held.Add((f, Describe(f)));
                        }

                        Assert.True(reader.TryTake(out var next));
                        held.Add((next, Describe(next)));
                        for (int i = 0; i < 10; i++)
                            Assert.True(reader.TryRead(out _));
                        foreach (var (frame, line) in held)
                            Assert.Equal(line, Describe(frame));
                    }

                    foreach (var (frame, line) in held)
                        Assert.Equal(line, Describe(frame));
                    Assert.Equal(0, ReadAhead.Held);
                }
                finally
                {
                    foreach (var (frame, _) in held)
                        frame.Dispose();
                }

                Assert.Throws<ObjectDisposedException>(() => held[0].Frame.Width);
            }

            // Disposed with the thread mid-frame and taken frames still held: no hang, no crash, the queue's let go.
            for (int round = 0; round < 40; round++)
            {
                var held = new List<DecodedFrame>();
                using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = 1 + (round % 4) }))
                {
                    for (int i = 0; i < round % 5; i++)
                    {
                        Assert.True(reader.TryTake(out var f));
                        held.Add(f);
                    }

                    if (round % 3 == 0)
                        Thread.Sleep(round % 2);
                }

                Assert.Equal(0, ReadAhead.Held);
                foreach (var f in held)
                {
                    Assert.Equal(320, f.Width);
                    f.Dispose();
                }
            }

            // Taking after Dispose is an error, as reading is.
            var disposed = new VideoFrameReader(path, new VideoFrameReaderOptions { ReadAhead = 2 });
            disposed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposed.TryTake(out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // FrameRate: TryTake gives the fps filter's frames, those the reads give there (number, pts in 1/rate, time, the
    // frame's own size), reads and takes mixed on one reader, read ahead or not, valid after the reader; seeking and
    // reading forward to a time stay refused, as for reads.
    [Fact]
    public void FrameRateTakesAreTheFpsFiltersFrames()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-take-").FullName;
        try
        {
            string path = Make(dir, "late.ts", "-c:v mpeg2video -bf 2 -g 12 -output_ts_offset 10", "", 4);
            foreach (var rate in new Rational[] { new(1, 1), new(3, 2), new(30, 1) })
            {
                foreach (int ahead in new[] { 0, 2 })
                {
                    var options = new VideoFrameReaderOptions { FrameRate = rate, MaxWidth = 100, ReadAhead = ahead };
                    var held = new List<(DecodedFrame Frame, string Line)>();
                    try
                    {
                        using (var reader = new VideoFrameReader(path, options))
                        using (var reads = new VideoFrameReader(path, options))
                        {
                            for (int step = 0; ; step++)
                            {
                                bool read = reads.TryRead(out var expected);
                                string at = $"{rate} ReadAhead {ahead}, frame {step}";
                                if (step % 2 == 0)
                                {
                                    Assert.Equal(read, reader.TryRead(out var frame));
                                    if (read)
                                        Assert.True((expected.Index, expected.Pts, expected.Time) == (frame.Index, frame.Pts, frame.Time), at);
                                }
                                else
                                {
                                    Assert.Equal(read, reader.TryTake(out var taken));
                                    if (read)
                                    {
                                        Assert.True((expected.Pts, (TimeSpan?)expected.Time) == (taken!.Pts, taken.Time), at);
                                        Assert.True((320, 240) == (taken.Width, taken.Height), at);
                                        held.Add((taken, Describe(taken)));
                                    }
                                }

                                if (!read)
                                    break;
                            }

                            Assert.Throws<InvalidOperationException>(() => reader.TryTakeAt(TimeSpan.Zero, out _));
                            Assert.Throws<InvalidOperationException>(() => reader.TryTakeForwardTo(TimeSpan.Zero, out _));
                        }

                        Assert.NotEmpty(held);
                        foreach (var (frame, line) in held)
                            Assert.Equal(line, Describe(frame));
                        Assert.Equal(0, ReadAhead.Held);
                    }
                    finally
                    {
                        foreach (var (frame, _) in held)
                            frame.Dispose();
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
