using FrameReader;

/// <summary>
/// TestMedia (issue #237): synthetic clips from FFmpeg's sources and encoders, in-process. Needs SHOTDETECTOR_FFMPEG_LIBS;
/// without it these do nothing.
/// </summary>
public class TestMediaTests
{
    static readonly string? Libs = TestLibraries.Load();

    // The issue's clip: three sources concatenated (cuts at known frames), interlaced MPEG-2 top field first, AAC.
    [Fact]
    public void WritesConcatenatedInterlacedClip()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-testmedia-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.ts");
            TestMedia.Write(path, new TestMediaOptions
            {
                Video = ["testsrc2=d=2", "smptebars=d=1", "mandelbrot,trim=duration=1"],
                Size = (320, 240),
                FrameRate = new(25, 1),
                FieldOrder = FieldOrder.TopFirst,
                Audio = "sine=frequency=1000:duration=4",
                VideoEncoder = "mpeg2video",
                AudioEncoder = "aac",
            });

            var info = MediaProbe.Probe(path);
            Assert.Equal(("mpeg2video", 320, 240, FieldOrder.TopFirst), (info.Video!.Codec, info.Video.Width, info.Video.Height, info.Video.FieldOrder));
            Assert.Contains(info.Streams, s => s.Type == StreamType.Audio && s.Codec == "aac");

            // 2 + 1 + 1 s at 25 fps; the frames change where the sources do (the test pattern moves every frame, the bars don't).
            var frames = new List<byte[]>();
            using (var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { FrameFormat = FrameFormat.Gray8 }))
            {
                while (reader.TryRead(out var frame))
                    frames.Add(frame.Data.ToArray());
            }

            Assert.Equal(100, frames.Count);

            // Mean absolute difference per pixel: the still bars differ only by the P-frames' quantization noise.
            static double Diff(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
            Assert.True(Diff(frames[60], frames[61]) < 1, $"smptebars: {Diff(frames[60], frames[61])}");
            Assert.True(Diff(frames[49], frames[50]) > 20, $"the cut to the bars: {Diff(frames[49], frames[50])}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // VideoQuality is ffmpeg's -q:v: a fixed quantizer, so a lower one is a bigger file than the default rate control's.
    [Fact]
    public void VideoQualityIsAFixedQuantizer()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-testmedia-").FullName;
        try
        {
            long Size(int? q)
            {
                string path = Path.Combine(dir, $"q{q}.mp4");
                TestMedia.Write(path, new TestMediaOptions { Video = ["mandelbrot,trim=duration=2"], VideoEncoder = "mpeg4", VideoQuality = q });
                return new FileInfo(path).Length;
            }

            long best = Size(2), worst = Size(31), auto = Size(null);
            Assert.True(best > auto && auto > worst, $"q2 {best}, default {auto}, q31 {worst} bytes");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WritesAudioOnlyAndRejectsBadSources()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-testmedia-").FullName;
        try
        {
            string path = Path.Combine(dir, "tone.wav");
            TestMedia.Write(path, new TestMediaOptions { Audio = "sine=frequency=440:duration=1", AudioEncoder = "pcm_s24le" });
            var audio = Assert.Single(MediaProbe.Probe(path).Streams);
            Assert.Equal(("pcm_s24le", 1), (audio.Codec, audio.Channels));

            var e = Assert.Throws<FrameReaderException>(() => TestMedia.Write(Path.Combine(dir, "x.mkv"), new TestMediaOptions { Video = ["nosuchsource=d=1"] }));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
            Assert.Throws<ArgumentException>(() => TestMedia.Write(Path.Combine(dir, "x.mkv"), new TestMediaOptions()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
