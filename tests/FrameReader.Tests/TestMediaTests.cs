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
            Assert.Equal(frames[60], frames[61]); // smptebars: still
            Assert.NotEqual(frames[49], frames[50]); // the cut to the bars
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
