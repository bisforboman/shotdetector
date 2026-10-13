using FrameReader;

/// <summary>
/// Remux.CanCopy (issue #241): FFmpeg's own answer to "can these streams be stream-copied there", per stream, without
/// writing anything. Needs SHOTDETECTOR_FFMPEG_LIBS; without it these do nothing.
/// </summary>
public class RemuxCanCopyTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Fact]
    public void SaysWhetherAndWhyNot()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-cancopy-").FullName;
        try
        {
            string clip = Path.Combine(dir, "clip.mkv");
            TestMedia.Write(clip, new TestMediaOptions { Video = ["testsrc2=d=1"], Audio = "sine=duration=1", VideoEncoder = "mpeg4", AudioEncoder = "aac" });

            // DASH (its mp4 segments) takes MPEG-4 Part 2 and AAC; WebM takes neither. Nothing is written either way.
            Assert.True(Remux.CanCopy(clip, Path.Combine(dir, "out.mpd")).CanCopy);
            var webm = Remux.CanCopy(clip, Path.Combine(dir, "out.webm"));
            Assert.False(webm.CanCopy);
            Assert.Equal(2, webm.Reasons.Count);
            Assert.Contains(webm.Reasons, r => r.Contains("(mpeg4): the webm muxer", StringComparison.Ordinal));
            Assert.Contains(webm.Reasons, r => r.Contains("(aac): the webm muxer", StringComparison.Ordinal));

            // Only the streams asked for.
            Assert.False(Remux.CanCopy(clip, Path.Combine(dir, "out.webm"), new RemuxOptions { Streams = StreamSelection.Audio }).CanCopy);
            Assert.Single(Remux.CanCopy(clip, Path.Combine(dir, "out.webm"), new RemuxOptions { StreamIndices = [1] }).Reasons);
            Assert.Equal(["clip.mkv"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
