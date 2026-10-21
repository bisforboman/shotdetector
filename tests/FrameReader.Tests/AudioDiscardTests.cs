using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameReader;

/// <summary>
/// AudioReader skips the streams it doesn't decode (<c>AVDISCARD_ALL</c>, as ffmpeg's <c>-vn</c>): the same samples,
/// chunk times and positions as reading every stream, and ffmpeg's, from the start and after seeks, in containers that
/// skip the bytes (MP4, MXF) and ones that parse them anyway (MPEG-TS, Matroska). Needs SHOTDETECTOR_FFMPEG_LIBS (an
/// FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class AudioDiscardTests
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

    const string Video = "-f lavfi -i testsrc2=s=320x240:r=25:d=6";
    const string Stereo = "-f lavfi -i sine=f=440:d=6:r=48000 -ac 2";

    [Theory]
    [InlineData("aac.mp4", Video + " " + Stereo + " -c:v mpeg4 -g 50 -bf 2 -c:a aac")]
    [InlineData("opus.mkv", Video + " " + Stereo + " -c:v mpeg4 -g 75 -c:a libopus")]
    [InlineData("mp2.ts", Video + " " + Stereo + " -c:v mpeg2video -g 12 -bf 2 -c:a mp2")]

    // Timestamps that wrap at 2^33 / 90 kHz part way (the wrap reference comes from a program's first packet).
    [InlineData("wrap.ts", Video + " " + Stereo + " -c:v mpeg2video -g 12 -bf 2 -c:a mp2 -output_ts_offset 95440")]
    [InlineData("pcm.mxf", Video + " " + Stereo + " -c:v mpeg2video -pix_fmt yuv422p -b:v 20M -g 12 -bf 2 -c:a pcm_s24le")]
    [InlineData("pcm.avi", Video + " " + Stereo + " -c:v mpeg4 -g 50 -c:a pcm_s16le")]

    // Two audio streams (stereo and 5.1, the one av_find_best_stream picks) besides the video.
    [InlineData("two.mkv", Video + " " + Stereo + " -f lavfi -i sine=f=330:d=6:r=48000 -map 0 -map 1 -map 2 -ac:a:1 6 -c:v mpeg4 -g 50 -c:a aac")]
    [InlineData("two.mp4", Video + " " + Stereo + " -f lavfi -i sine=f=330:d=6:r=48000 -map 0 -map 1 -map 2 -ac:a:1 6 -c:v mpeg4 -g 50 -c:a aac")]
    public void SamplesAreTheSameAsReadingEveryStream(string name, string make)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-discard-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. make.Split(' '), path]);
            foreach (var (rate, channels) in new (int?, int?)[] { (null, null), (16000, 1) })
            {
                foreach (double? seek in new double?[] { null, 0.5, 2.37, 4.9 })
                {
                    var (discarded, samples, audioIndex) = Read(path, rate, channels, seek, true);
                    var (every, everySamples, _) = Read(path, rate, channels, seek, false);
                    Assert.True(every.SequenceEqual(discarded) && everySamples.AsSpan().SequenceEqual(samples),
                        $"{name} rate {rate} channels {channels} seek {seek}: {every.Count} chunks reading every stream, {discarded.Count} discarding");

                    string[] ss = seek is { } t ? ["-ss", t.ToString(CultureInfo.InvariantCulture)] : [];
                    string[] conversion = [.. rate is { } r ? new[] { "-ar", $"{r}" } : [], .. channels is { } c ? new[] { "-ac", $"{c}" } : []];
                    byte[] expected = Ffmpeg(["-v", "error", .. ss, "-i", path, "-map", $"0:a:{audioIndex}", .. conversion, "-f", "f32le", "-"]);
                    Assert.True(expected.AsSpan().SequenceEqual(samples),
                        $"{name} rate {rate} channels {channels} seek {seek}: not ffmpeg's samples");
                }
            }

            // Audio.Convert (which reads through AudioReader) still writes ffmpeg's file.
            string expectedWav = Path.Combine(dir, "expected.wav"), actualWav = Path.Combine(dir, "actual.wav");
            Ffmpeg("-v", "error", "-y", "-i", path, "-vn", "-c:a", "pcm_f32le", "-fflags", "+bitexact", "-flags:a", "+bitexact", expectedWav);
            Audio.Convert(path, actualWav, new AudioConvertOptions { Encoder = "pcm_f32le", Bitexact = true });
            Assert.True(File.ReadAllBytes(expectedWav).AsSpan().SequenceEqual(File.ReadAllBytes(actualWav)), $"{name}: Audio.Convert isn't ffmpeg's file");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Each chunk's size, time and position, all the samples' bytes, and which audio stream (a:N) was read.
    static (List<(int Count, TimeSpan Time, long? Position)> Chunks, byte[] Samples, int AudioIndex) Read(string path, int? rate, int? channels, double? seek, bool discard)
    {
        using var reader = new AudioReader(path, new AudioReaderOptions { SampleRate = rate, Channels = channels, DiscardOtherStreams = discard });
        if (seek is { } s)
            reader.Seek(TimeSpan.FromSeconds(s));
        var chunks = new List<(int, TimeSpan, long?)>();
        var samples = new MemoryStream();
        while (reader.TryRead(out var chunk))
        {
            chunks.Add((chunk.Samples.Length, chunk.Time, reader.ChunkPosition));
            samples.Write(MemoryMarshal.AsBytes(chunk.Samples));
        }

        int audioIndex = 0;
        unsafe
        {
            for (int i = 0; i < reader.StreamIndex; i++)
            {
                if (reader.Format->streams[i]->codecpar->codec_type == FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_AUDIO)
                    audioIndex++;
            }
        }

        return (chunks, samples.ToArray(), audioIndex);
    }
}
