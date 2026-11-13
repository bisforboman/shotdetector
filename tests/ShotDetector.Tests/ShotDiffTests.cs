using System.Diagnostics;
using System.Text.Json;
using ShotDetector;

/// <summary>
/// ShotDiff, behind <c>shotdetect diff</c> (issue #771, docs/decisions/2026-11-09-shotdetect-diff.md): the alignment on
/// made-up frames, where every frame's hash and picture are known; then two cuts made with ffmpeg with known edits, a
/// new grade, a logo, another size and encoder quality, whose report must be exact (recall and precision 1, every trim
/// to the frame).
/// </summary>
public class ShotDiffTests(RecutClips clips) : IClassFixture<RecutClips>
{
    /// <summary>A shot of made-up frames: <paramref name="Length"/> frames of source shot <paramref name="Source"/> from its <paramref name="From"/>th; Still: every frame the first.</summary>
    record struct Piece(int Source, int From, int Length, bool Still = false);

    /// <summary>A cut of made-up frames: each source frame has its own random hash and picture (unrelated to every other).</summary>
    static (List<FrameSpan> Shots, CutFrames Frames) Cut(params Piece[] pieces) => NoisyCut(0, pieces);

    /// <summary><see cref="Cut"/> with <paramref name="flips"/> bits of every hash flipped and noise on every picture, as re-encoding would.</summary>
    static (List<FrameSpan> Shots, CutFrames Frames) NoisyCut(int flips, params Piece[] pieces)
    {
        var shots = new List<FrameSpan>();
        var hashes = new List<ulong>();
        var pictures = new List<byte>();
        foreach (var p in pieces)
        {
            shots.Add(new(hashes.Count, hashes.Count + p.Length));
            for (int u = 0; u < p.Length; u++)
            {
                var random = new Random((p.Source * 10_000) + (p.Still ? p.From : p.From + u));
                ulong hash = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63);
                var picture = new byte[CutFrames.Size];
                random.NextBytes(picture);
                var noise = new Random(hashes.Count + 1);
                for (int bit = 0; bit < flips; bit++)
                    hash ^= 1UL << ((bit * 7) + hashes.Count) % 64;
                for (int i = 0; flips > 0 && i < picture.Length; i++)
                    picture[i] = (byte)Math.Clamp(picture[i] + noise.Next(-6, 7), 0, 255);
                hashes.Add(hash);
                pictures.AddRange(picture);
            }
        }

        return (shots, new CutFrames([.. hashes], [.. pictures]));
    }

    static List<ShotDiffEntry> Compare((List<FrameSpan> Shots, CutFrames Frames) a, (List<FrameSpan> Shots, CutFrames Frames) b) =>
        ShotDiff.Compare(a.Shots, a.Frames, b.Shots, b.Frames);

    static readonly Piece[] Old = [new(1, 0, 40), new(2, 0, 30), new(3, 0, 50), new(4, 0, 25), new(5, 0, 60)];

    [Fact]
    public void TheSameCutIsUnchanged()
    {
        var report = Compare(Cut(Old), NoisyCut(4, Old));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => new ShotDiffEntry(ShotChange.Unchanged, i, i)), report);
    }

    [Fact]
    public void ARemovedShotStandsWhereItWas()
    {
        var report = Compare(Cut(Old), Cut(Old[0], Old[1], Old[3], Old[4]));
        Assert.Equal(
            [
                new(ShotChange.Unchanged, 0, 0), new(ShotChange.Unchanged, 1, 1), new(ShotChange.Removed, 2, null),
                new(ShotChange.Unchanged, 3, 2), new(ShotChange.Unchanged, 4, 3),
            ],
            report);
    }

    [Fact]
    public void AShotFromElsewhereIsAdded()
    {
        var report = Compare(Cut(Old), Cut(Old[0], Old[1], new(9, 0, 35), Old[2], Old[3], Old[4]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Added, null, 2), report[2]);
        Assert.Equal(5, report.Count(r => r.Change == ShotChange.Unchanged));
    }

    // The longest run in the old order stays; the shot out of it moved, wherever it went.
    [Fact]
    public void AShotOutOfOrderMoved()
    {
        var report = Compare(Cut(Old), Cut(Old[4], Old[0], Old[1], Old[2], Old[3]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Moved, 4, 0), report[0]);
        Assert.All(report.Skip(1), r => Assert.Equal(ShotChange.Unchanged, r.Change));
    }

    [Fact]
    public void ABlockMovedTogether()
    {
        var report = Compare(Cut(Old), Cut(Old[0], Old[3], Old[4], Old[1], Old[2]));
        Assert.Equal([ShotChange.Unchanged, ShotChange.Moved, ShotChange.Moved, ShotChange.Unchanged, ShotChange.Unchanged], report.Select(r => r.Change));
    }

    [Fact]
    public void TrimsAreFrameExact()
    {
        var report = Compare(Cut(Old), NoisyCut(4, Old[0], new(2, 7, 23), new(3, 0, 41), new(4, 3, 18), Old[4]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 1, 1, 7, 0), report[1]);
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 2, 2, 0, 9), report[2]);
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 3, 3, 3, 4), report[3]);
    }

    // A moved shot keeps its trims; a shot longer than before has negative trims.
    [Fact]
    public void AMovedShotKeepsItsTrimsAndALongerShotGrows()
    {
        var report = Compare(Cut(Old[0], Old[1], new(3, 5, 40), Old[3], Old[4]), Cut(new(4, 0, 20), Old[0], Old[1], new(3, 0, 50), Old[4]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Moved, 3, 0, 0, 5), report[0]);
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 2, 3, -5, -5), report[3]);
    }

    // The middle of a shot cut out: both pieces are the same old shot, trimmed, in order.
    [Fact]
    public void ASplitShotIsTwoTrimmedPieces()
    {
        var report = Compare(Cut(Old), Cut(Old[0], Old[1], new(3, 0, 20), new(3, 35, 15), Old[3], Old[4]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 2, 2, 0, 30), report[2]);
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 2, 3, 35, 0), report[3]);
        Assert.Equal(6, report.Count);
    }

    /// <summary><paramref name="cut"/> with shot <paramref name="index"/> and the next one as one shot: the detector didn't see the cut between them.</summary>
    static (List<FrameSpan> Shots, CutFrames Frames) Joined((List<FrameSpan> Shots, CutFrames Frames) cut, int index)
    {
        var shots = new List<FrameSpan>(cut.Shots);
        shots[index] = new(shots[index].Start, shots[index + 1].End);
        shots.RemoveAt(index + 1);
        return (shots, cut.Frames);
    }

    // A cut in the old cut only (a black interstitial whose new grade lifts it into the next shot): one new shot, a run
    // of old ones, unchanged. Old[1] (30 frames) has fewer fingerprint frames than Old[2] (50), which anchors the match.
    [Fact]
    public void AJoinedShotIsARunOfOldShots()
    {
        var report = Compare(Cut(Old), Joined(NoisyCut(4, Old), 1));
        Assert.Equal(
            [
                new(ShotChange.Unchanged, 0, 0), new(ShotChange.Unchanged, 1, 1, ALast: 2), new(ShotChange.Unchanged, 3, 2),
                new(ShotChange.Unchanged, 4, 3),
            ],
            report);
        var tail = Compare(Cut(Old), Joined(NoisyCut(4, Old), 2));
        Assert.Equal(new ShotDiffEntry(ShotChange.Unchanged, 2, 2, ALast: 3), tail[2]);
        Assert.Equal(4, tail.Count);
    }

    // The joined run's trims are the run's: the first old shot's head, the last one's tail.
    [Fact]
    public void AJoinedRunKeepsItsTrims()
    {
        var report = Compare(Cut(Old), Joined(NoisyCut(4, Old[0], new(2, 6, 24), new(3, 0, 45), Old[3], Old[4]), 1));
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 1, 1, 6, 5, ALast: 2), report[1]);
        Assert.Equal(4, report.Count);
    }

    // Frames the old cut doesn't have before a shot aren't its neighbour's: the neighbour is removed, the shot longer.
    [Fact]
    public void ANewHeadIsNotARun()
    {
        var report = Compare(Cut(Old), Joined(Cut(Old[0], new(9, 0, 30), Old[2], Old[3], Old[4]), 1));
        Assert.Equal(new ShotDiffEntry(ShotChange.Removed, 1, null), report[1]);
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 2, 1, -30, 0), report[2]);
    }

    // A cut in the new cut only: an old shot as a run of new ones, each where the one before ends.
    [Fact]
    public void ASplitShotWithAllItsFramesIsARunOfNewShots()
    {
        var report = Compare(Cut(Old), NoisyCut(4, Old[0], Old[1], new(3, 0, 20), new(3, 20, 30), Old[3], Old[4]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Unchanged, 2, 2, BLast: 3), report[2]);
        Assert.Equal(5, report.Count);
        Assert.All(report, r => Assert.Equal(ShotChange.Unchanged, r.Change));
    }

    // Every frame of a still picture fits anywhere: the shot keeps its head.
    [Fact]
    public void AStillPictureKeepsItsHead()
    {
        var report = Compare(Cut(Old[0], new(7, 0, 50, Still: true), Old[1]), NoisyCut(4, Old[0], new(7, 0, 38, Still: true), Old[1]));
        Assert.Equal(new ShotDiffEntry(ShotChange.Trimmed, 1, 1, 0, 12), report[1]);
    }

    // Two old shots alike (the same picture used twice): each new one takes the one the order expects.
    [Fact]
    public void RepeatedShotsMatchInOrder()
    {
        Piece[] old = [Old[0], new(7, 0, 30, Still: true), Old[1], new(7, 0, 30, Still: true), Old[2]];
        var report = Compare(Cut(old), NoisyCut(4, old));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => new ShotDiffEntry(ShotChange.Unchanged, i, i)), report);
    }

    [Fact]
    public void HashesMatchWithinMaxDistance()
    {
        var near = Compare(Cut(Old), NoisyCut(ShotDiff.MaxDistance, Old));
        Assert.All(near, r => Assert.Equal(ShotChange.Unchanged, r.Change));
        var far = Compare(Cut(Old), NoisyCut(ShotDiff.MaxDistance + 1, Old));
        Assert.Equal(10, far.Count);
        Assert.All(far, r => Assert.Contains(r.Change, new[] { ShotChange.Added, ShotChange.Removed }));
    }

    [Fact]
    public void FingerprintFramesSitInTheMiddlesOfEqualParts()
    {
        Assert.Equal([10, 30, 50, 70, 90], ShotDiff.Fingerprint(new(0, 100)));
        Assert.Equal([3, 4, 5], ShotDiff.Fingerprint(new(3, 6)));
    }

    [Fact]
    public void TheLongestRunInOrderStays()
    {
        (int, int)?[] match = [(0, 0), (3, 0), (1, 0), (2, 0), null, (4, 0)];
        Assert.Equal([true, false, true, true, false, true], ShotDiff.LongestInOrder(match));
        Assert.Equal([true, true], ShotDiff.LongestInOrder([(2, 0), (2, 5)]));
        Assert.Single(ShotDiff.LongestInOrder([(2, 5), (2, 0)]), true);
    }

    // The two cuts of RecutClips: every change found, nothing else reported, every trim to the frame (shot numbers and
    // frames are the shot list's, 1-based).
    [Fact]
    public void ARecutIsReportedExactly()
    {
        var a = ShotDiff.Read(clips.Old);
        var b = ShotDiff.Read(clips.New);
        var report = ShotDiff.Compare(a, b).Select(r => (r.Change, A: r.A + 1, B: r.B + 1, r.Head, r.Tail)).ToList();
        (ShotChange, int?, int?, int, int)[] expected =
        [
            (ShotChange.Unchanged, 1, 1, 0, 0),
            (ShotChange.Trimmed, 2, 2, 10, 0),
            (ShotChange.Removed, 3, null, 0, 0),
            (ShotChange.Unchanged, 4, 3, 0, 0),
            (ShotChange.Moved, 8, 4, 0, 0),
            (ShotChange.Trimmed, 5, 5, 0, 8),
            (ShotChange.Unchanged, 6, 6, 0, 0),
            (ShotChange.Added, null, 7, 0, 0),
            (ShotChange.Unchanged, 7, 8, 0, 0),
        ];
        Assert.Equal(expected, report);
    }

    // Scored in chunks, each chunk's hash detector tells its frames from its own thread (the frame where two meet twice):
    // the same hashes and pictures as one pass.
    [Fact]
    public void ChunksGiveTheSameFrames()
    {
        var sequential = ShotDiff.Read(clips.Old, new() { Mode = DetectionMode.Sequential });
        var parallel = ShotDiff.Read(clips.Old, new() { Mode = DetectionMode.Parallel, Chunks = 3 });
        Assert.Equal(3, parallel.Result.Video.Chunks);
        Assert.Equal(400, sequential.Frames.Count);
        Assert.Equal(sequential.Frames.Hashes, parallel.Frames.Hashes);
        Assert.Equal(sequential.Frames.Pictures, parallel.Frames.Pictures);
        Assert.Equal(sequential.Shots, parallel.Shots);
    }

    // The CLI: the JSON's shape (docs/decisions/2026-11-09-shotdetect-diff.md), exit 3 on changes, 0 on a cut only
    // re-encoded, graded and logoed.
    [Fact]
    public void TheCliReportsAsJsonAndExitsThreeOnChanges()
    {
        var (exitCode, output, err) = CliTests.Run("diff", clips.Old, clips.New, "--json", "-");
        Assert.True(exitCode == 3, err);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(8, root.GetProperty("a").GetProperty("shots").GetInt32());
        Assert.Equal(400, root.GetProperty("a").GetProperty("frames").GetInt32());
        var summary = root.GetProperty("summary");
        Assert.Equal((4, 1, 2, 1, 1),
            (summary.GetProperty("unchanged").GetInt32(), summary.GetProperty("moved").GetInt32(), summary.GetProperty("trimmed").GetInt32(),
                summary.GetProperty("added").GetInt32(), summary.GetProperty("removed").GetInt32()));
        var shots = root.GetProperty("shots");
        var trimmed = shots[1];
        Assert.Equal("trimmed", trimmed.GetProperty("status").GetString());
        Assert.Equal(2, trimmed.GetProperty("a").GetProperty("lastShot").GetInt32());
        Assert.Equal(51, trimmed.GetProperty("a").GetProperty("startFrame").GetInt32());
        Assert.Equal(100, trimmed.GetProperty("a").GetProperty("endFrame").GetInt32());
        Assert.Equal("00:00:02.000", trimmed.GetProperty("b").GetProperty("startTimecode").GetString());
        Assert.Equal(90, trimmed.GetProperty("b").GetProperty("endFrame").GetInt32());
        Assert.Equal(10, trimmed.GetProperty("headTrim").GetInt32());
        var removed = shots[2];
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("b").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("headTrim").ValueKind);

        var (same, table, sameErr) = CliTests.Run("diff", clips.Old, clips.Regraded);
        Assert.True(same == 0, sameErr + table);
        Assert.Contains("8 unchanged, 0 moved, 0 trimmed, 0 added, 0 removed", table, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCliNeedsTwoVideos()
    {
        var (exitCode, _, err) = CliTests.Run("diff", clips.Old);
        Assert.Equal(2, exitCode);
        Assert.Contains("two videos", err, StringComparison.Ordinal);
    }
}

/// <summary>
/// Two cuts of a programme of eight 2 s shots (25 fps, 320x240): the new one with the second shot's first 10 frames and
/// the fifth's last 8 cut, the third removed, the last moved to fourth place and a shot from elsewhere added before the
/// seventh; then a new grade, a half-transparent logo, 288x216 and a lower encoder quality. Every source is a picture
/// with structure both ways (bars that vary only across, or a flat colour, have hashes that noise decides) and the same
/// frames each run (the random ones seeded).
/// </summary>
public sealed class RecutClips : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-recut-").FullName;

    const string Size = "s=320x240:r=25";

    static readonly Dictionary<string, string> Sources = new()
    {
        ["testsrc2"] = $"testsrc2={Size}",
        ["mandelbrot"] = $"mandelbrot={Size}",
        ["bars"] = $"smptebars={Size},scroll=h=0.004",
        ["spectrum"] = $"colorspectrum={Size},scroll=v=0.005",
        ["testsrc"] = $"testsrc={Size}",
        ["sierpinski"] = $"sierpinski={Size}:seed=1",
        ["hdbars"] = $"smptehdbars={Size}",
        ["chart"] = "colorchart=r=25,scale=320:240",
        ["yuv"] = $"yuvtestsrc={Size}",
    };

    /// <summary>The old cut.</summary>
    public string Old { get; }

    /// <summary>The new cut.</summary>
    public string New { get; }

    /// <summary>The old cut with the new one's grade, logo, size and quality, and no edit.</summary>
    public string Regraded { get; }

    public RecutClips()
    {
        Old = Path.Combine(_dir, "old.mp4");
        New = Path.Combine(_dir, "new.mp4");
        Regraded = Path.Combine(_dir, "regraded.mp4");
        (string, int, int)[] old = [.. new[] { "testsrc2", "mandelbrot", "bars", "spectrum", "testsrc", "sierpinski", "hdbars", "chart" }.Select(n => (n, 0, 50))];
        (string, int, int)[] recut =
        [
            ("testsrc2", 0, 50), ("mandelbrot", 10, 50), ("spectrum", 0, 50), ("chart", 0, 50), ("testsrc", 0, 42),
            ("sierpinski", 0, 50), ("yuv", 0, 50), ("hdbars", 0, 50),
        ];

        // lutyuv rather than eq, which needs a GPL build.
        const string Graded = ",lutyuv=y=val*0.9+20:u=(val-128)*1.2+128:v=(val-128)*1.2+128,drawbox=x=iw-44:y=8:w=36:h=16:color=white@0.7:t=fill,scale=288:216";
        Make(Old, old, "", 3);
        Make(New, recut, Graded, 6);
        Make(Regraded, old, Graded, 6);
    }

    /// <summary>The sources' frames From..To-1, one after another, as mpeg4 (built into every ffmpeg).</summary>
    static void Make(string path, (string Source, int From, int To)[] shots, string after, int quality)
    {
        var args = new List<string> { "-v", "error", "-y" };
        var graph = new List<string>();
        for (int i = 0; i < shots.Length; i++)
        {
            args.AddRange(["-f", "lavfi", "-i", Sources[shots[i].Source]]);
            graph.Add($"[{i}]trim=start_frame={shots[i].From}:end_frame={shots[i].To},setpts=PTS-STARTPTS,setsar=1,format=yuv420p[v{i}]");
        }

        string joined = string.Concat(Enumerable.Range(0, shots.Length).Select(i => $"[v{i}]"));
        args.AddRange(["-filter_complex", $"{string.Join(';', graph)};{joined}concat=n={shots.Length}:v=1:a=0{after},format=yuv420p",
            "-r", "25", "-c:v", "mpeg4", "-q:v", quality.ToString(System.Globalization.CultureInfo.InvariantCulture), path]);
        string? libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        var psi = new ProcessStartInfo(libs is null ? "ffmpeg" : Path.Combine(libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}
