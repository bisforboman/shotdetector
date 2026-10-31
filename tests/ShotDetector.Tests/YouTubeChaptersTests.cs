using System.Diagnostics;
using ShotDetector;

/// <summary>
/// Timeline.YouTubeChapters: YouTube's rules (first chapter at 0:00, three or more, each at least 10 s, the last one
/// too) and the merge the owner chose (docs/decisions/2026-10-31-youtube-chapters.md).
/// </summary>
public class YouTubeChaptersTests(Clips clips) : IClassFixture<Clips>
{
    [Fact]
    public void ShortShotsMergeUntilTheChapterHasRunTenSeconds()
    {
        // Cuts at 4, 9, 12.5 (13), 20, 22, 23.04 (24) s: chapters at 0, 13 and 24.
        Assert.Equal(new long[] { 0, 13, 24 }, Timeline.ChapterStarts([4, 9, 12.5, 20, 22, 23.04], 40, 10));
    }

    [Fact]
    public void ACutExactlyTenSecondsOnStartsAChapter()
    {
        Assert.Equal(new long[] { 0, 10, 20 }, Timeline.ChapterStarts([10, 20], 30, 10));
        Assert.Equal(new long[] { 0, 10 }, Timeline.ChapterStarts([10, 18.9], 30, 10));
    }

    [Fact]
    public void TimesRoundUpFromTheirMilliseconds()
    {
        // 20.0000000001 is 20 s to the millisecond, so it stays at 20; 10.0004 too (and 20.001, 21, is too close); 10.001 is 11.
        Assert.Equal(new long[] { 0, 10, 20 }, Timeline.ChapterStarts([10.0004, 20.0000000001, 20.001], 40, 10));
        Assert.Equal(new long[] { 0, 11 }, Timeline.ChapterStarts([10.001], 40, 10));
    }

    [Fact]
    public void AShortLastChapterJoinsTheOneBefore()
    {
        // The chapter at 30 would run 9.96 s to the end at 39.96: it goes, and the one before ends at the video's end.
        Assert.Equal(new long[] { 0, 10, 20 }, Timeline.ChapterStarts([10, 20, 30], 39.96, 10));
        Assert.Equal(new long[] { 0, 10, 20, 30 }, Timeline.ChapterStarts([10, 20, 30], 40, 10));
    }

    [Fact]
    public void ALongerMinimumMakesFewerChapters()
    {
        Assert.Equal(new long[] { 0, 60, 125 }, Timeline.ChapterStarts([10, 30, 60, 90, 124.2, 150], 200, 60));
    }

    [Fact]
    public void TheMinimumIsYouTubesTenSecondsOrMore()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline.YouTubeChapters(r, 9.99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline.YouTubeChapters(r, double.NaN));
    }

    // Other players' chapters (the Jellyfin plugin's, #634) take the same rule with a shorter minimum: cuts at 2 s and
    // 4 s of the 6 s clip are chapters 2 s long.
    [Fact]
    public void ChapterStartsTakeAnyMinimum()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], Timeline.ChapterStarts(r, 2));

        // At 2.5 s, 2 s is too soon and the chapter from 4 s would run 2 s to the end: one chapter.
        Assert.Equal([TimeSpan.Zero], Timeline.ChapterStarts(r, 2.5));
        Assert.Equal([TimeSpan.Zero], Timeline.ChapterStarts(r));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline.ChapterStarts(r, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timeline.ChapterStarts(r, double.NaN));
    }

    [Fact]
    public void TimestampsAreMinutesAndSecondsThenHours()
    {
        Assert.Equal("0:00", Timeline.ChapterTime(0));
        Assert.Equal("0:09", Timeline.ChapterTime(9));
        Assert.Equal("10:05", Timeline.ChapterTime(605));
        Assert.Equal("59:59", Timeline.ChapterTime(3599));
        Assert.Equal("1:00:00", Timeline.ChapterTime(3600));
        Assert.Equal("2:03:04", Timeline.ChapterTime((2 * 3600) + (3 * 60) + 4));
    }

    [Fact]
    public void FewerThanThreeChaptersIsAnError()
    {
        // Cuts at 2 s and 4 s of a 6 s clip: one chapter.
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        var e = Assert.Throws<InvalidOperationException>(() => Timeline.YouTubeChapters(r));
        Assert.Contains("these shots make 1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineAChapterFromTheDetectedCuts()
    {
        string dir = Directory.CreateTempSubdirectory("chapters-").FullName;
        try
        {
            var r = ShotDetection.Detect(LongClip(dir), new() { Detector = DetectorKind.Content });
            Assert.Equal(new[] { 12.0, 24.0 }, r.Cuts.Select(c => c.Seconds));
            Assert.Equal("0:00 Chapter 1\n0:12 Chapter 2\n0:24 Chapter 3\n", Timeline.YouTubeChapters(r));
            Assert.Throws<InvalidOperationException>(() => Timeline.YouTubeChapters(r, 12.5));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A 36 s clip at 5 fps with cuts at 12 s and 24 s (three different test patterns).</summary>
    internal static string LongClip(string dir)
    {
        string path = Path.Combine(dir, "long.mkv");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string a in (string[])["-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=s=160x120:r=5:d=12", "-f", "lavfi", "-i", "smptebars=s=160x120:r=5:d=12",
            "-f", "lavfi", "-i", "testsrc2=s=160x120:r=5:d=12",
            "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0,format=yuv420p", "-c:v", "mpeg4", "-q:v", "2", path])
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }
}
