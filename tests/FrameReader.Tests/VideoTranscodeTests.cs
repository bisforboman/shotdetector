using FrameReader;

/// <summary>
/// Video.Transcode (issue #240): a whole file re-encoded in one call, deinterlaced when flagged interlaced. Needs
/// SHOTDETECTOR_FFMPEG_LIBS; without it these do nothing.
/// </summary>
public class VideoTranscodeTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Fact]
    public async Task TranscodesInterlacedToProgressiveWithAudio()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-transcode-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.ts"), output = Path.Combine(dir, "out.mp4");
            TestMedia.Write(input, new TestMediaOptions
            {
                Video = ["testsrc2=d=2"],
                FieldOrder = FieldOrder.TopFirst,
                Audio = "sine=duration=2",
                VideoEncoder = "mpeg2video",
                AudioEncoder = "aac",
            });

            // Deinterlace: automatic (the input is flagged tt). mpeg4 because ShotDetector.Native has no H.264 encoder.
            await Video.TranscodeAsync(input, output, new VideoTranscodeOptions { Encoder = "mpeg4", KeyframeEvery = TimeSpan.FromSeconds(1) });

            var info = MediaProbe.Probe(output);
            Assert.Equal(("mpeg4", "yuv420p", 320, 240), (info.Video!.Codec, info.Video.PixelFormat, info.Video.Width, info.Video.Height));
            Assert.NotEqual(FieldOrder.TopFirst, info.Video.FieldOrder);
            Assert.Contains(info.Streams, s => s.Type == StreamType.Audio);
            int frames = 0;
            using (var reader = new VideoFrameReader(output, new VideoFrameReaderOptions { FrameFormat = FrameFormat.Gray8 }))
            {
                while (reader.TryRead(out _))
                    frames++;
            }

            Assert.Equal(50, frames);

            // Video only.
            string silent = Path.Combine(dir, "silent.mkv");
            Video.Transcode(input, silent, new VideoTranscodeOptions { Encoder = "mpeg4", Audio = false });
            Assert.DoesNotContain(MediaProbe.Probe(silent).Streams, s => s.Type == StreamType.Audio);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
