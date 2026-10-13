using FrameReader;

/// <summary>
/// The whole-file jobs' progress (issue #242) and async overloads (issue #243). Needs SHOTDETECTOR_FFMPEG_LIBS; without
/// it these do nothing.
/// </summary>
public class WholeFileJobTests
{
    static readonly string? Libs = TestLibraries.Load();

    // Collects reports on the reporting thread (Progress<T> would post them).
    sealed class Recorder : IProgress<MediaProgress>
    {
        public List<MediaProgress> Reports { get; } = [];

        public void Report(MediaProgress value)
        {
            lock (Reports)
                Reports.Add(value);
        }
    }

    // Each job: a first report at once, positions that only grow, and a last one at the whole duration.
    static void Ends(Recorder r, TimeSpan duration)
    {
        Assert.True(r.Reports.Count >= 2, $"{r.Reports.Count} reports");
        Assert.True(r.Reports.Zip(r.Reports.Skip(1)).All(p => p.First.Position <= p.Second.Position));
        Assert.Equal(1.0, r.Reports[^1].Fraction);
        Assert.InRange(r.Reports[^1].Position.TotalSeconds, duration.TotalSeconds - 0.1, duration.TotalSeconds + 0.1);
    }

    [Fact]
    public async Task ReportProgressAndRunAsync()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-jobs-").FullName;
        try
        {
            string clip = Path.Combine(dir, "clip.mkv"), tone = Path.Combine(dir, "tone.wav");
            TestMedia.Write(clip, new TestMediaOptions { Video = ["testsrc2=d=3"], Audio = "sine=duration=3", VideoEncoder = "mpeg4", AudioEncoder = "aac" });
            TestMedia.Write(tone, new TestMediaOptions { Audio = "sine=frequency=1000:duration=3" });
            var three = TimeSpan.FromSeconds(3);

            var convert = new Recorder();
            await Audio.ConvertAsync(clip, Path.Combine(dir, "out.wav"), new AudioConvertOptions { Progress = convert });
            Ends(convert, three);

            var remux = new Recorder();
            await Remux.CopyAsync(clip, Path.Combine(dir, "copy.mp4"), new RemuxOptions { Progress = remux });
            Ends(remux, three);

            var waveform = new Recorder();
            using (var audio = new AudioReader(tone))
                await WaveformData.ReadAsync(audio, new WaveformOptions { Progress = waveform });
            Ends(waveform, three);

            var search = new Recorder();
            using (var audio = new AudioReader(tone))
            {
                var segments = await ReferenceTone.FindAsync(audio, new ReferenceToneOptions { MinDuration = TimeSpan.FromSeconds(1), Progress = search });
                Assert.Single(segments);
            }

            Ends(search, three);
            Assert.Contains("\"streams\"", await MediaProbe.ProbeJsonAsync(clip));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Audio.ConvertAsync(clip, Path.Combine(dir, "x.wav"), null, new CancellationToken(true)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
