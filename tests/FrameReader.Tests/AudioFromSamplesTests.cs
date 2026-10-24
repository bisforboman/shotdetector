using System.Diagnostics;
using FrameReader;

/// <summary>
/// ReferenceToneFinder and WaveformBuilder (issue #494): fed the chunks an AudioReader gives, whole or split anywhere,
/// they give exactly what ReferenceTone.Find and WaveformData.Read give for the file (those need
/// SHOTDETECTOR_FFMPEG_LIBS, an FFmpeg 8.1 shared build's folder with ffmpeg; without it they do nothing); on
/// generated samples, their edges and refusals (no FFmpeg).
/// </summary>
public class AudioFromSamplesTests
{
    static readonly string? Libs = TestLibraries.Load();

    const int Rate = 48000;
    const string Tone = "0.1259*sin(2*PI*1000*t)"; // -18 dBFS, EBU line-up
    const string Content = "(0.2*sin(2*PI*220*t)+0.1*sin(2*PI*440*t))*(0.6+0.4*sin(2*PI*0.3*t))+0.05*(random(0)-0.5)";

    // Line-up tone with a 0.4 s break in both channels (bridged), then programme, then a second, cut run of tone.
    const string Channel = $"'if(lt(t,3),{Tone},if(lt(t,3.4),0,if(lt(t,9),{Tone},if(lt(t,14),{Content},{Tone}))))'";

    sealed record Chunks(int SampleRate, int Channels, bool DecodesFloat, List<(float[] Samples, TimeSpan Time)> List);

    static string Make(string dir, string name, string encode, double seconds)
    {
        string path = Path.Combine(dir, name);
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in (string[])["-v", "error", "-y", "-f", "lavfi", "-i", $"aevalsrc={Channel}|{Channel}:s={Rate}:d={seconds}", .. encode.Split(' ', StringSplitOptions.RemoveEmptyEntries), path])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return path;
    }

    static Chunks Read(string path)
    {
        using var audio = new AudioReader(path, new AudioReaderOptions());
        var list = new List<(float[], TimeSpan)>();
        while (audio.TryRead(out var chunk))
            list.Add((chunk.Samples.ToArray(), chunk.Time)); // a chunk is valid until the next read
        return new Chunks(audio.SampleRate, audio.Channels, audio.DecodesFloat, list);
    }

    // The chunks re-cut into pieces of `instants` (0: as decoded), each piece's time from its first sample's.
    static IEnumerable<(float[] Samples, TimeSpan Time)> Split(Chunks chunks, int instants)
    {
        foreach (var (samples, time) in chunks.List)
        {
            if (instants == 0)
            {
                yield return (samples, time);
                continue;
            }

            int step = instants * chunks.Channels;
            for (int at = 0; at < samples.Length; at += step)
                yield return (samples[at..Math.Min(samples.Length, at + step)], time + TimeSpan.FromSeconds((double)(at / chunks.Channels) / chunks.SampleRate));
        }
    }

    static IReadOnlyList<ToneSegment> FindIn(Chunks chunks, int instants, ReferenceToneOptions? options)
    {
        var finder = new ReferenceToneFinder(chunks.SampleRate, chunks.Channels, options);
        foreach (var (samples, time) in Split(chunks, instants))
        {
            if (!finder.Add(samples, time))
                break;
        }

        return finder.Finish();
    }

    static byte[] Bytes(WaveformData waveform)
    {
        var bytes = new MemoryStream();
        waveform.Save(bytes);
        return bytes.ToArray();
    }

    public static TheoryData<string, string> Files => new()
    {
        { "clip.m4a", "" },
        { "clip.wav", "-c:a pcm_s16le" },
        { "clip.flac", "-c:a flac -sample_fmt s32" },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void FinderGivesFindsSegmentsHoweverTheChunksAreCut(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-samples-").FullName;
        try
        {
            string path = Make(dir, name, encode, 20);
            var chunks = Read(path);
            foreach (var options in new ReferenceToneOptions?[] { null, new() { SearchDuration = TimeSpan.FromSeconds(7.013) }, new() { SearchDuration = TimeSpan.FromSeconds(30) } })
            {
                IReadOnlyList<ToneSegment> expected;
                using (var audio = new AudioReader(path, new AudioReaderOptions()))
                    expected = ReferenceTone.Find(audio, options);
                Assert.NotEmpty(expected);
                foreach (int instants in new[] { 0, 1, 7, 1024 })
                    Assert.Equal(expected, FindIn(chunks, instants, options));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BuilderGivesReadsPointsHoweverTheChunksAreCut(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-samples-").FullName;
        try
        {
            string path = Make(dir, name, encode, 2.0137);
            var chunks = Read(path);
            Assert.Equal(name == "clip.m4a", chunks.DecodesFloat);
            foreach (var options in new WaveformOptions?[] { null, new() { SplitChannels = true, SamplesPerPixel = 300 }, new() { PixelsPerSecond = 50 } })
            {
                byte[] expected;
                using (var audio = new AudioReader(path, new AudioReaderOptions()))
                    expected = Bytes(WaveformData.Read(audio, options));
                foreach (int instants in new[] { 0, 1, 7, 1024 })
                {
                    var builder = new WaveformBuilder(chunks.SampleRate, chunks.Channels, options, integerSamples: !chunks.DecodesFloat);
                    foreach (var (samples, time) in Split(chunks, instants))
                        builder.Add(samples, time);
                    Assert.Equal(expected, Bytes(builder.Finish()));
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Generated: a -18 dBFS 1 kHz sine for 0-4 s, a 0.5 s break, again to 9 s, then silence to 12 s; stereo.
    static float[] Generated(double seconds = 12)
    {
        var samples = new float[(int)(seconds * Rate) * 2];
        for (int i = 0; i < samples.Length / 2; i++)
        {
            double t = (double)i / Rate;
            float x = t < 4 || (t >= 4.5 && t < 9) ? (float)(0.1259 * Math.Sin(2 * Math.PI * 1000 * t)) : 0;
            (samples[2 * i], samples[2 * i + 1]) = (x, x);
        }

        return samples;
    }

    [Fact]
    public void FinderBridgesTheBreakAndCountsFromTheFirstTime()
    {
        var finder = new ReferenceToneFinder(Rate, 2);
        float[] samples = Generated();
        var origin = TimeSpan.FromSeconds(100);
        for (int at = 0; at < samples.Length; at += 2 * 1000)
            Assert.True(finder.Add(samples.AsSpan(at, Math.Min(2 * 1000, samples.Length - at)), origin + TimeSpan.FromSeconds(at / 2.0 / Rate)));
        Assert.False(finder.IsComplete);
        var segment = Assert.Single(finder.Finish());
        Assert.Equal(origin, segment.Start);
        Assert.InRange(segment.End - origin, TimeSpan.FromSeconds(8.999), TimeSpan.FromSeconds(9.001));
        Assert.InRange(segment.Level, -18.1, -17.9);
    }

    [Fact]
    public void FinderStopsAtTheSearchDuration()
    {
        var progress = new List<MediaProgress>();
        var finder = new ReferenceToneFinder(Rate, 2, new ReferenceToneOptions { SearchDuration = TimeSpan.FromSeconds(6), Progress = new SyncProgress(progress.Add) });
        float[] samples = Generated();
        int added = 0;
        while (finder.Add(samples.AsSpan(added, 2 * 4800), TimeSpan.FromSeconds(added / 2.0 / Rate)))
            added += 2 * 4800;
        Assert.True(finder.IsComplete);
        Assert.Equal(6 * Rate * 2, added + 2 * 4800); // stopped by the chunk that reached 6 s
        Assert.False(finder.Add(samples.AsSpan(0, 2), TimeSpan.FromSeconds(6))); // more is ignored
        var segment = Assert.Single(finder.Finish());
        Assert.InRange(segment.End, TimeSpan.FromSeconds(5.999), TimeSpan.FromSeconds(6.001)); // cut at the search's end
        Assert.Equal(new MediaProgress(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6)), progress[^1]);
    }

    [Fact]
    public void ProgressWithoutADurationReportsPositions()
    {
        var progress = new List<MediaProgress>();
        var finder = new ReferenceToneFinder(Rate, 2, new ReferenceToneOptions { Progress = new SyncProgress(progress.Add) });
        finder.Add(new float[2 * 480], TimeSpan.FromSeconds(1));
        finder.Add(new float[2 * 480], TimeSpan.FromSeconds(1.01));
        Assert.Empty(finder.Finish());
        Assert.Equal(new MediaProgress(TimeSpan.FromSeconds(0.01), null), progress[^1]);
        Assert.Null(progress[^1].Fraction);

        var shorter = new List<MediaProgress>();
        var bounded = new ReferenceToneFinder(Rate, 2, new ReferenceToneOptions { SearchDuration = TimeSpan.FromSeconds(60), Progress = new SyncProgress(shorter.Add) }, TimeSpan.FromSeconds(10));
        bounded.Finish();
        Assert.Equal(new MediaProgress(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), shorter[^1]); // the input is shorter than the search

        var points = new List<MediaProgress>();
        var builder = new WaveformBuilder(Rate, 1, new WaveformOptions { Progress = new SyncProgress(points.Add) });
        builder.Add(new float[200], TimeSpan.FromSeconds(2));
        Assert.Equal(1, builder.Finish().Length);
        Assert.Equal(new MediaProgress(TimeSpan.FromSeconds(2), null), points[^1]);
    }

    [Fact]
    public void BuilderMakesPointsAndAPartialLastOne()
    {
        float[] samples = [0.5f, -0.5f, -0.25f, 0.25f, 1f, 0f, 0f, 0f, 0.1f, 0.1f];
        WaveformData Build(bool split, bool integer, int instants)
        {
            var builder = new WaveformBuilder(8000, 2, new WaveformOptions { SamplesPerPixel = 4, SplitChannels = split }, integerSamples: integer);
            for (int at = 0; at < samples.Length; at += 2 * instants)
                builder.Add(samples.AsSpan(at, Math.Min(2 * instants, samples.Length - at)), TimeSpan.Zero);
            return builder.Finish();
        }

        static short[] Points(WaveformData w) =>
            [.. Enumerable.Range(0, w.Length).SelectMany(i => Enumerable.Range(0, w.Channels).SelectMany(c => new[] { w.Min(c, i), w.Max(c, i) }))];

        foreach (int instants in new[] { 1, 3, 5 })
        {
            // Float sources: scaled by 32767, truncated.
            var split = Build(true, false, instants);
            Assert.Equal((8000, 4, 2, 2), (split.SampleRate, split.SamplesPerPixel, split.Channels, split.Length));
            Assert.Equal([-8191, 32767, -16383, 8191, 3276, 3276, 3276, 3276], Points(split));
            Assert.Equal([0, 16383, 3276, 3276], Points(Build(false, false, instants))); // the channels' mean, toward zero

            // Integer sources: shifted to 16 bits, rounding down; full scale clipped.
            Assert.Equal([-8192, 32767, -16384, 8192, 3276, 3276, 3276, 3276], Points(Build(true, true, instants)));
        }
    }

    [Fact]
    public void Refusals()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceToneFinder(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceToneFinder(Rate, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceToneFinder(-1, 2));
        Assert.Throws<ArgumentException>(() => new ReferenceToneFinder(Rate, 2, new ReferenceToneOptions { SearchDuration = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaveformBuilder(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaveformBuilder(Rate, 0));
        Assert.Throws<ArgumentException>(() => new WaveformBuilder(Rate, 25));
        Assert.Throws<ArgumentException>(() => new WaveformBuilder(Rate, 2, new WaveformOptions { SamplesPerPixel = 1 }));
        Assert.Throws<ArgumentException>(() => new WaveformBuilder(Rate, 2, new WaveformOptions { PixelsPerSecond = 0 }));
        Assert.Throws<ArgumentException>(() => new WaveformBuilder(100, 2, new WaveformOptions { PixelsPerSecond = 60 })); // 1 sample per point

        var finder = new ReferenceToneFinder(Rate, 2);
        Assert.Throws<ArgumentException>(() => finder.Add(new float[3], TimeSpan.Zero));
        Assert.Empty(finder.Finish());
        Assert.Throws<InvalidOperationException>(() => finder.Add(new float[2], TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => finder.Finish());

        var builder = new WaveformBuilder(Rate, 3);
        Assert.Throws<ArgumentException>(() => builder.Add(new float[4], TimeSpan.Zero));
        Assert.Equal(0, builder.Finish().Length);
        Assert.Throws<InvalidOperationException>(() => builder.Add(new float[3], TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => builder.Finish());
    }

    // Nothing on this path loads FFmpeg: in a process of its own (this one has FFmpeg.AutoGen loaded by other tests), with
    // no ffmpeg on PATH and no FFmpeg libraries named, AudioWithoutFfmpeg runs both on generated samples and checks that
    // neither FFmpeg.AutoGen nor any of FFmpeg's libraries got loaded (the system's would be found if anything tried).
    [Fact]
    public async Task LoadsNothingOfFfmpeg()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string check = Path.Combine(bin.Parent!.Parent!.Parent!.Parent!.FullName, "AudioWithoutFfmpeg", "bin", bin.Parent.Name, bin.Name, "AudioWithoutFfmpeg.dll");
        string dotnet = Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet"
            ? host
            : Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(check);
        psi.Environment.Remove("SHOTDETECTOR_FFMPEG_LIBS");
        psi.Environment.Remove("LD_LIBRARY_PATH");
        psi.Environment.Remove("DYLD_LIBRARY_PATH");
        psi.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"AudioWithoutFfmpeg failed ({p.ExitCode}): {err}");
        Assert.StartsWith("Tone 0.00-10.00 s", await output);
    }

    // Reports at once, on the calling thread (Progress<T> posts to the thread pool).
    sealed class SyncProgress(Action<MediaProgress> report) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => report(value);
    }
}
