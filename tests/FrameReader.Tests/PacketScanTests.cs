using System.Diagnostics;
using FrameReader;

/// <summary>
/// The packet scan (<see cref="ProbeOptions.PacketTimestamps"/>) of an MP4/MOV file reads its sample tables, not its media
/// data, where that gives the same: every value MediaProbe reads equals the full read's. Needs SHOTDETECTOR_FFMPEG_LIBS
/// (with ffmpeg beside the libraries to make the clips); a codec whose encoder that ffmpeg lacks is left out.
/// </summary>
public class PacketScanTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Tool(string name) => Path.Combine(Libs!, OperatingSystem.IsWindows() ? name + ".exe" : name);

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Tool(tool)) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    static readonly Lazy<string> Encoders = new(() => Run("ffmpeg", "-hide_banner", "-encoders"));

    static bool HasEncoder(string name) => Encoders.Value.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var n, ..] && n == name);

    // name, video encoding, what the scan reads (headers: FileInput skipping the media data, for TimestampsWithoutData's
    // codecs; file: the whole file through FFmpeg's file protocol, as before), and what's done to the file after.
    [Theory]
    [InlineData("h264-b.mp4", "libx264 -bf 3 -c:a aac", "headers", "")]
    [InlineData("h264-faststart.mp4", "libx264 -bf 2 -movflags +faststart -c:a aac", "headers", "")]
    [InlineData("h264-interlaced.mp4", "libx264 -flags +ildct+ilme -x264opts tff=1 -an", "headers", "")]
    [InlineData("h264-mp3.mov", "libx264 -c:a libmp3lame", "headers", "")]
    [InlineData("h264-cut.mp4", "libx264 -movflags +faststart -an", "headers", "cut")]
    [InlineData("hevc.mp4", "libx265 -tag:v hvc1 -an", "headers", "")]
    [InlineData("mpeg4.mp4", "mpeg4 -bf 2 -c:a aac", "headers", "")]
    [InlineData("mpeg2.mov", "mpeg2video -bf 2 -an", "headers", "")]
    [InlineData("mjpeg.mov", "mjpeg -an", "headers", "")]
    [InlineData("prores.mov", "prores -an", "headers", "")]
    [InlineData("vp9.mp4", "libvpx-vp9 -an", "file", "")]
    [InlineData("av1.mp4", "libsvtav1 -an", "file", "")]
    [InlineData("mpeg1.mov", "mpeg1video -an", "file", "")]
    [InlineData("h264-fragmented.mp4", "libx264 -movflags +frag_keyframe+empty_moov -c:a aac", "file", "")]
    public void TimestampsWithoutMediaDataAreTheSame(string name, string encode, string reads, string after)
    {
        if (Libs is null)
            return;
        string[] args = encode.Split(' ');
        if (!HasEncoder(args[0]) || (args.Contains("libmp3lame") && !HasEncoder("libmp3lame")))
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-scan-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=40", "-f", "lavfi", "-i", "sine=d=40",
                "-pix_fmt", args[0] is "prores" ? "yuv422p10le" : args[0] is "mjpeg" ? "yuvj420p" : "yuv420p", "-c:v", .. args, path]);
            if (after == "cut")
            {
                byte[] bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(path, bytes[..(bytes.Length * 7 / 10)]);
            }

            long size = new FileInfo(path).Length, read = -1, readFull = -1;
            var full = MediaProbe.Probe(path, new ProbeOptions { PacketTimestamps = true, ReadsMediaData = true, OnFileBytesRead = n => readFull = n });
            var scan = MediaProbe.Probe(path, new ProbeOptions { PacketTimestamps = true, OnFileBytesRead = n => read = n });
            Assert.Equal(-1, readFull); // the full read through the file protocol, as before
            Assert.NotNull(full.PacketTimestamps);
            Assert.Equal(full.PacketTimestamps, scan.PacketTimestamps);
            Assert.Equal(full.PacketPositions, scan.PacketPositions);
            Assert.Equal((full.Container, full.Duration, full.HasAudio), (scan.Container, scan.Duration, scan.HasAudio));
            Assert.Equal(full.Video! with { ExtraData = default }, scan.Video! with { ExtraData = default });
            Assert.Equal(full.Video!.ExtraData.ToArray(), scan.Video!.ExtraData.ToArray());
            Assert.Equal(full.Streams, scan.Streams);
            if (reads == "headers")
                Assert.InRange(read, 1, size / 4);
            else
                Assert.Equal(-1, read);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The first video packet's bytes are read whole, also with the packet scan (find_stream_info read them, and keeps them).
    [Fact]
    public void TheFirstPacketIsReadWhole()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-scan-").FullName;
        try
        {
            string path = Path.Combine(dir, "first.mp4");

            // Large frames: the probe's buffer no longer holds the first one when the scan comes back to it.
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=25:d=8", "-c:v", "mpeg4", "-q:v", "2", path);
            var both = MediaProbe.Probe(path, new ProbeOptions { PacketTimestamps = true, FirstVideoPacket = true });
            var first = MediaProbe.Probe(path, new ProbeOptions { FirstVideoPacket = true });
            Assert.Equal(first.FirstVideoPacket!.Value.ToArray(), both.FirstVideoPacket!.Value.ToArray());
            Assert.Contains(both.FirstVideoPacket.Value.ToArray(), b => b != 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Top-level boxes: each mdat's payload (64-bit sizes, to the end, cut short); none where the file is fragmented or
    // doesn't start with a box FileInput knows.
    [Fact]
    public void MediaDataIsEachMdatsPayload()
    {
        static byte[] Box(string type, int payload, bool large = false)
        {
            var b = new List<byte>();
            long size = payload + (large ? 16 : 8);
            if (large)
                b.AddRange([0, 0, 0, 1]);
            else
                b.AddRange(BitConverter.GetBytes((uint)size).Reverse());
            b.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            if (large)
                b.AddRange(BitConverter.GetBytes(size).Reverse());
            b.AddRange(new byte[payload]);
            return [.. b];
        }

        (long, long)[]? Of(params byte[][] boxes) => FileInput.MediaData(new MemoryStream([.. boxes.SelectMany(b => b)]));
        Assert.Equal([(32L, 132L), (164L, 264L)], Of(Box("ftyp", 16), Box("mdat", 100), Box("moov", 8), Box("mdat", 100, large: true)));
        Assert.Equal([(24L, 74L)], Of(Box("moov", 8), [.. Box("mdat", 100)[..58]])); // cut short
        byte[] toEnd = Box("mdat", 40);
        toEnd[3] = 0;
        Assert.Equal([(8L, 48L)], Of(toEnd));
        Assert.Null(Of(Box("ftyp", 16), Box("moov", 8), Box("moof", 8), Box("mdat", 10)));
        Assert.Null(Of(Box("RIFF", 16)));
    }
}
