using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FrameReader;

/// <summary>
/// AudioFilter against ffmpeg's command line: the samples of <c>-af GRAPH [-ar R -ac C] -f f32le</c>, byte for byte, and
/// analysis filters' metadata as <c>ametadata=print</c> prints it. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared
/// build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioFilterTests
{
    static readonly string? Libs = TestLibraries.Load();

    static byte[] Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return output.ToArray();
    }

    [Theory]
    // Integer PCM: volume computes on s16 there, as ffmpeg's graph gets it.
    [InlineData("pcm.wav", "-c:a pcm_s16le", "volume=0.37", null, null, null)]
    [InlineData("pcm.wav", "-c:a pcm_s16le", "highpass=f=300,equalizer=f=1000:t=q:w=1:g=-9:enable='between(t,0.5,1.2)'", null, null, null)]
    [InlineData("aac.m4a", "-c:a aac", "bass=g=6,treble=g=-4,afade=t=out:st=1.5:d=0.4", null, null, null)]
    [InlineData("aac.m4a", "-c:a aac", "acompressor=threshold=0.1:ratio=4,alimiter=limit=0.5", 16000, 1, null)]
    [InlineData("pcm.wav", "-c:a pcm_s16le", "pan=mono|c0=0.5*c0+0.5*c1,lowpass=f=2000", null, null, 0.73)]
    [InlineData("opus.webm", "-c:a libopus", "dynaudnorm=f=100,agate=threshold=0.05,bandpass=f=800,bandreject=f=1500", null, null, null)]
    public void SamplesAreFfmpegs(string name, string encode, string graph, int? rate, int? channels, double? seek)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-afilter-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.8*sin(440*2*PI*t)*lt(t\\,1.4)|0.4*sin(3000*2*PI*t)+0.2*random(0):s=48000:d=2",
                .. encode.Split(' '), path]);
            string[] ss = seek is { } t ? ["-ss", t.ToString(CultureInfo.InvariantCulture)] : [];
            string[] conversion = [.. rate is { } r ? new[] { "-ar", $"{r}" } : [], .. channels is { } c ? new[] { "-ac", $"{c}" } : []];
            byte[] expected = Ffmpeg(["-v", "error", .. ss, "-i", path, "-af", graph, .. conversion, "-f", "f32le", "-"]);

            using var audio = new AudioReader(path, new AudioReaderOptions { SampleRate = rate, Channels = channels });
            if (seek is { } s)
                audio.Seek(TimeSpan.FromSeconds(s));
            using var filter = new AudioFilter(audio, graph);
            var actual = new MemoryStream();
            while (filter.TryRead(out var chunk))
                actual.Write(MemoryMarshal.AsBytes(chunk.Samples));
            byte[] got = actual.ToArray();
            int first = 0;
            while (first < Math.Min(expected.Length, got.Length) && expected[first] == got[first])
                first++;
            Assert.True(expected.AsSpan().SequenceEqual(got),
                $"{name} {graph}: {expected.Length} bytes from ffmpeg, {got.Length} filtered, first difference at sample {first / 4 / (channels ?? 2)} (frame-interleaved)");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("silencedetect=n=-30dB:d=0.3")]
    [InlineData("astats=metadata=1:reset=1")]
    [InlineData("ebur128=metadata=1")]
    public void MetadataIsFfmpegs(string graph)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-afilter-").FullName;
        try
        {
            string path = Path.Combine(dir, "speech.wav");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.5*sin(300*2*PI*t)*(lt(t\\,1)+gt(t\\,2.2)):s=48000:d=3.5", "-c:a", "pcm_s16le", path);
            // Every frame's metadata, in order, from ametadata=print: "frame:N pts:P pts_time:T" then key=value lines.
            var expected = new List<Dictionary<string, string>>();
            foreach (var line in Encoding.ASCII.GetString(Ffmpeg("-v", "error", "-i", path, "-af", $"{graph},ametadata=print:file=-", "-f", "null", "-"))
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("frame:"))
                    expected.Add([]);
                else
                    expected[^1][line[..line.IndexOf('=')]] = line[(line.IndexOf('=') + 1)..];
            }

            using var audio = new AudioReader(path, new AudioReaderOptions());
            using var filter = new AudioFilter(audio, graph);
            var actual = new List<Dictionary<string, string>>();
            while (filter.TryRead(out _))
                if (filter.Metadata.Count > 0)
                    actual.Add(new(filter.Metadata));
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i].OrderBy(kv => kv.Key), actual[i].OrderBy(kv => kv.Key));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AGraphThatDoesntParseSaysSo()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-afilter-").FullName;
        try
        {
            string path = Path.Combine(dir, "a.wav");
            Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "sine=d=0.2", path);
            using var audio = new AudioReader(path, new AudioReaderOptions());
            using var filter = new AudioFilter(audio, "nosuchfilter=1");
            var e = Assert.Throws<FrameReaderException>(() => filter.TryRead(out _));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
            Assert.Throws<ArgumentException>(() => new AudioFilter(audio, " "));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
