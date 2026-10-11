using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// FrameReader's audio from a headers-first Stream (read once, not seekable, as ffmpeg reads a pipe): AudioFilter,
/// SpectralStats and WaveformData give the file's results, and AudioReader after a Seek gives ffmpeg's
/// <c>-ss T -i -</c> on the same bytes through stdin. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's
/// folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioStreamTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Exe => Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    static byte[] Ffmpeg(string? stdinFile, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdinFile is not null };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var feed = stdinFile is null ? Task.CompletedTask : Task.Run(() =>
        {
            try
            {
                using (var f = File.OpenRead(stdinFile))
                    f.CopyTo(p.StandardInput.BaseStream);
                p.StandardInput.Close();
            }
            catch (IOException) { } // ffmpeg may stop reading early
        });
        var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        feed.Wait();
        Assert.True(p.ExitCode == 0, stderr.Result);
        return output.ToArray();
    }

    /// <summary>A file's bytes as a stream that can't seek or tell its length, like a pipe.</summary>
    sealed class Pipe(string path) : Stream
    {
        readonly FileStream _file = File.OpenRead(path);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _file.Dispose();
            base.Dispose(disposing);
        }
    }

    // Containers with their headers first: what a Stream can be read from.
    public static TheoryData<string, string> Clips => new()
    {
        { "aac.m4a", "-ac 2 -c:a aac -movflags +faststart" },
        { "mp3.mp3", "-c:a libmp3lame" },
        { "opus.mkv", "-ac 2 -c:a libopus" },
        { "flac.flac", "-ac 2 -c:a flac" },
        { "pcm.wav", "-c:a pcm_s16le" },
    };

    static void WithClip(string name, string encode, Action<string> test)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-astream-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            // A tone, then a different one with noise, so filters, statistics and peaks have something to show.
            Ffmpeg(null, ["-v", "error", "-y", "-f", "lavfi", "-i",
                "aevalsrc='if(lt(t,1.5),0.4*sin(440*2*PI*t),0.3*sin(2500*2*PI*t)+0.1*random(0))':s=48000:d=4", .. encode.Split(' '), path]);
            test(path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static byte[] Samples(AudioReader reader)
    {
        var all = new MemoryStream();
        while (reader.TryRead(out var chunk))
            all.Write(MemoryMarshal.AsBytes(chunk.Samples));
        return all.ToArray();
    }

    static byte[] Samples(AudioFilter filter)
    {
        var all = new MemoryStream();
        while (filter.TryRead(out var chunk))
            all.Write(MemoryMarshal.AsBytes(chunk.Samples));
        return all.ToArray();
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void SeekIsFfmpegsOnAPipe(string name, string encode) => WithClip(name, encode, path =>
    {
        foreach (double seek in new[] { 0.5, 2.37 })
        {
            byte[] expected = Ffmpeg(path, "-v", "error", "-ss", seek.ToString(CultureInfo.InvariantCulture), "-i", "-", "-vn", "-f", "f32le", "-");
            using var stream = new Pipe(path);
            using var reader = new AudioReader(stream);
            reader.Seek(TimeSpan.FromSeconds(seek));
            byte[] actual = Samples(reader);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{name} seek {seek}: {expected.Length} bytes from ffmpeg, {actual.Length} read");
        }
    });

    [Theory]
    [MemberData(nameof(Clips))]
    public void AudioFilterIsTheFiles(string name, string encode) => WithClip(name, encode, path =>
    {
        const string Graph = "highpass=f=300,volume=0.8,astats=metadata=1:reset=1";
        (byte[], string) Run(AudioReader reader)
        {
            using var filter = new AudioFilter(reader, Graph);
            byte[] samples = Samples(filter);
            return (samples, string.Join(";", filter.Metadata.Select(kv => kv.Key + "=" + kv.Value)));
        }
        using var file = new AudioReader(path);
        using var stream = new Pipe(path);
        using var piped = new AudioReader(stream);
        var (fileSamples, fileMeta) = Run(file);
        var (pipeSamples, pipeMeta) = Run(piped);
        Assert.True(fileSamples.Length > 0);
        Assert.True(fileSamples.AsSpan().SequenceEqual(pipeSamples), $"{name}: {fileSamples.Length} bytes from the file, {pipeSamples.Length} from the stream");
        Assert.Equal(fileMeta, pipeMeta);
    });

    [Theory]
    [MemberData(nameof(Clips))]
    public void SpectralStatsAreTheFiles(string name, string encode) => WithClip(name, encode, path =>
    {
        static List<string> Run(AudioReader reader)
        {
            using var stats = new SpectralStats(reader);
            var windows = new List<string>();
            while (stats.TryRead(out var w))
                windows.Add(w.Time.Ticks + ":" + string.Join(",", w.Channels.ToArray().Select(m => $"{m.Mean:R}/{m.Centroid:R}/{m.Flux:R}/{m.Rolloff:R}")));
            return windows;
        }
        using var file = new AudioReader(path);
        using var stream = new Pipe(path);
        using var piped = new AudioReader(stream);
        var expected = Run(file);
        Assert.True(expected.Count > 10);
        Assert.Equal(expected, Run(piped));
    });

    [Theory]
    [MemberData(nameof(Clips))]
    public void WaveformDataIsTheFiles(string name, string encode) => WithClip(name, encode, path =>
    {
        static byte[] Run(AudioReader reader)
        {
            var output = new MemoryStream();
            WaveformData.Read(reader).Save(output);
            return output.ToArray();
        }
        using var file = new AudioReader(path);
        using var stream = new Pipe(path);
        using var piped = new AudioReader(stream);
        Assert.Equal(Run(file), Run(piped));
    });
}
