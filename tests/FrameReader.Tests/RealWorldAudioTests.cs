using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// AudioReader and SpectralStats on a real film's own audio (AAC, MP3; realworld.yml runs it per film) against ffmpeg's
/// command line, like <see cref="AudioReaderTests"/> and <see cref="SpectralStatsTests"/> do on synthetic clips: real
/// encoders' streams (priming, padding, long durations, seeking in the middle). Needs SHOTDETECTOR_REALWORLD_AUDIO (the
/// film) and SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without them these do nothing.
/// </summary>
public class RealWorldAudioTests
{
    static readonly string? Libs = TestLibraries.Load();
    static readonly string? Film = Environment.GetEnvironmentVariable("SHOTDETECTOR_REALWORLD_AUDIO");

    static Process Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(16000, 1, null)]
    [InlineData(48000, 2, 187.3)]   // into the film, past a few hundred seconds of packets
    public async Task SamplesAreFfmpegs(int? rate, int? channels, double? seek)
    {
        if (Libs is null || Film is null)
            return;
        string[] ss = seek is { } t ? ["-ss", t.ToString(CultureInfo.InvariantCulture)] : [];
        string[] conversion = [.. rate is { } r ? new[] { "-ar", $"{r}" } : [], .. channels is { } c ? new[] { "-ac", $"{c}" } : []];
        using var p = Ffmpeg(["-v", "error", .. ss, "-i", Film, "-vn", .. conversion, "-f", "f32le", "-"]);
        var stderr = p.StandardError.ReadToEndAsync();
        var expected = p.StandardOutput.BaseStream;

        // Chunk by chunk (a whole film's samples are hundreds of MB), naming the first sample that differs.
        using var reader = new AudioReader(Film, new AudioReaderOptions { SampleRate = rate, Channels = channels });
        if (seek is { } s)
            reader.Seek(TimeSpan.FromSeconds(s));
        long compared = 0;
        var buffer = new byte[1 << 16];
        while (reader.TryRead(out var chunk))
        {
            var ours = MemoryMarshal.AsBytes(chunk.Samples);
            if (buffer.Length < ours.Length)
                buffer = new byte[ours.Length];
            int n = expected.ReadAtLeast(buffer.AsSpan(0, ours.Length), ours.Length, throwOnEndOfStream: false);
            int at = ours[..n].CommonPrefixLength(buffer.AsSpan(0, n));
            if (at < ours.Length)
            {
                long sample = (compared + at) / sizeof(float) / reader.Channels;
                Assert.Fail(n < ours.Length && at == n
                    ? $"ffmpeg's samples end at {sample} ({(double)sample / reader.SampleRate:F3} s); ours go on"
                    : $"sample {sample} ({(double)sample / reader.SampleRate:F3} s) differs from ffmpeg's");
            }
            compared += ours.Length;
        }
        long rest = 0;
        for (int n; (n = expected.Read(buffer)) > 0;)
            rest += n;
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, await stderr);
        Assert.True(rest == 0, $"ours end at sample {compared / sizeof(float) / reader.Channels}; ffmpeg's has {rest / sizeof(float) / reader.Channels} more");
        Assert.True(compared > 0);
    }

    [Fact]
    public async Task SpectralStatsAreFfmpegs()
    {
        if (Libs is null || Film is null)
            return;
        var options = new SpectralStatsOptions();
        string filter = string.Create(CultureInfo.InvariantCulture,
            $"aspectralstats=win_size={options.WindowSize}:win_func={options.WindowFunction}:overlap={options.Overlap},ametadata=print:file=-");
        using var p = Ffmpeg("-v", "error", "-i", Film, "-vn", "-af", filter, "-f", "null", "-");
        var stderr = p.StandardError.ReadToEndAsync();

        using var audio = new AudioReader(Film, new AudioReaderOptions());
        using var stats = new SpectralStats(audio, options);
        string[] names = ["mean", "variance", "centroid", "spread", "skewness", "kurtosis", "entropy", "flatness", "crest", "flux", "slope", "decrease", "rolloff"];
        int windows = 0;
        string? line = p.StandardOutput.ReadLine();
        while (stats.TryRead(out var window))
        {
            // ametadata=print: "frame:N pts:P pts_time:T", then key=value lines.
            Assert.True(line is not null && line.StartsWith("frame:"), $"window {windows}: ffmpeg has no more ({line})");
            double time = double.Parse(line[(line.IndexOf("pts_time:") + 9)..], CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(time - window.Time.TotalSeconds) < 1.5e-6, $"window {windows}: ffmpeg at {time}, ours {window.Time.TotalSeconds}");
            var values = new Dictionary<string, string>();
            while ((line = p.StandardOutput.ReadLine()) is not null && !line.StartsWith("frame:"))
                values[line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..].Trim();
            for (int c = 0; c < window.Channels.Length; c++)
            {
                var m = window.Channels[c];
                double[] ours = [m.Mean, m.Variance, m.Centroid, m.Spread, m.Skewness, m.Kurtosis, m.Entropy, m.Flatness, m.Crest, m.Flux, m.Slope, m.Decrease, m.Rolloff];
                for (int i = 0; i < names.Length; i++)
                {
                    string theirs = values[$"lavfi.aspectralstats.{c + 1}.{names[i]}"];
                    Assert.True(SpectralStats.Parse(theirs).Equals(ours[i]), $"window {windows} ({time} s) channel {c + 1} {names[i]}: ffmpeg {theirs}, ours {ours[i]:R}");
                }
            }
            windows++;
        }
        Assert.True(line is null, $"ours end after {windows} windows; ffmpeg has more ({line})");
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, await stderr);
        Assert.True(windows > 1000);
    }
}
