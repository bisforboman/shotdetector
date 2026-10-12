using System.Diagnostics;
using FrameReader;

/// <summary>
/// Remux.Copy against ffmpeg's command line: <c>ffmpeg -i IN -map ... -c copy -fflags +bitexact OUT</c>, byte for byte.
/// Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class RemuxTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
    }

    // Sources: H.264 (x264 in the test's ffmpeg) or MPEG-4 Part 2 video with AAC or MP3 audio, a second audio stream with
    // a language, subtitles, chapters and a start time that isn't 0 (MPEG-TS).
    const string Make = "-f lavfi -i testsrc2=s=320x240:r=25:d=3 -f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=660:d=3";

    [Theory]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "video.mp4", StreamSelection.Video, "-map 0:v")]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "audio.m4a", StreamSelection.Audio, "-map 0:a")]
    [InlineData("in.mp4", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "all.mkv", StreamSelection.All, "-map 0")]
    [InlineData("in.mkv", "-map 0 -map 1 -map 2 -c:v mpeg4 -g 12 -c:a libmp3lame -metadata:s:a:1 language=swe", "both.mp4", StreamSelection.Video | StreamSelection.Audio, "-map 0:v -map 0:a")]
    [InlineData("in.mkv", "-map 0 -map 1 -map 2 -c:v mpeg4 -g 12 -c:a libmp3lame -metadata:s:a:1 language=swe", "audio.mp3", StreamSelection.Audio, "-map 0:a:0")]
    [InlineData("in.ts", "-map 0 -map 1 -c:v mpeg4 -g 12 -c:a aac", "fromts.mp4", StreamSelection.All, "-map 0")]
    public void FileIsFfmpegs(string source, string make, string output, StreamSelection streams, string map)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string input = Path.Combine(dir, source), expected = Path.Combine(dir, "expected-" + output), actual = Path.Combine(dir, output);
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), .. make.Split(' '), input]);
            Ffmpeg(["-v", "error", "-y", "-i", input, .. map.Split(' '), "-c", "copy", "-fflags", "+bitexact", expected]);

            // "-map 0:a:0" is the first audio stream: by index there.
            var options = map == "-map 0:a:0"
                ? new RemuxOptions { StreamIndices = [1], Bitexact = true }
                : new RemuxOptions { Streams = streams, Bitexact = true };
            Remux.Copy(input, actual, options);
            byte[] want = File.ReadAllBytes(expected), got = File.ReadAllBytes(actual);
            int first = 0;
            while (first < Math.Min(want.Length, got.Length) && want[first] == got[first])
                first++;
            Assert.True(want.AsSpan().SequenceEqual(got), $"{source} -> {output}: ffmpeg {want.Length} bytes, ours {got.Length}, first difference at {first}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ChaptersAndMetadataCome()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string meta = Path.Combine(dir, "meta.txt"), input = Path.Combine(dir, "in.mkv"), expected = Path.Combine(dir, "expected.mp4"), actual = Path.Combine(dir, "out.mp4");
            File.WriteAllText(meta, ";FFMETADATA1\ntitle=Remux test\nartist=Someone\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=One\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=3000\ntitle=Two\n");
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), "-i", meta, "-map", "0", "-map", "1", "-map_metadata", "3", "-map_chapters", "3", "-c:v", "mpeg4", "-c:a", "aac", input]);
            Ffmpeg("-v", "error", "-y", "-i", input, "-map", "0", "-c", "copy", "-fflags", "+bitexact", expected);
            Remux.Copy(input, actual, new RemuxOptions { Bitexact = true });
            Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
            Assert.Throws<FrameReaderException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { Streams = StreamSelection.Subtitle }));
            Assert.Throws<FrameReaderException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { StreamIndices = [7] }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // DASH (issue #171): fMP4 segments and a static MPD from stream copy, every file ffmpeg's, with muxer options as
    // ffmpeg's output options. And an .mp4 with movflags=+faststart.
    [Fact]
    public void MuxerOptionsAsFfmpegsOutputOptions()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.mp4");
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), "-map", "0", "-map", "1", "-c:v", "mpeg4", "-g", "12", "-c:a", "aac", input]);

            var dash = new Dictionary<string, string>
            {
                ["seg_duration"] = "1",
                ["use_template"] = "1",
                ["use_timeline"] = "0",
                ["init_seg_name"] = "init-$RepresentationID$.mp4",
                ["media_seg_name"] = "chunk-$RepresentationID$-$Number%05d$.m4s",
                ["adaptation_sets"] = "id=0,streams=v id=1,streams=a",
            };
            string want = Directory.CreateDirectory(Path.Combine(dir, "ffmpeg")).FullName, got = Directory.CreateDirectory(Path.Combine(dir, "ours")).FullName;
            Ffmpeg(["-v", "error", "-y", "-i", input, "-map", "0", "-c", "copy", "-fflags", "+bitexact", "-f", "dash",
                .. dash.SelectMany(o => new[] { "-" + o.Key, o.Value }), Path.Combine(want, "manifest.mpd").Replace('\\', '/')]); // ffmpeg needs '/' (ours: either)
            Remux.Copy(input, Path.Combine(got, "manifest.mpd"), new RemuxOptions { Container = "dash", MuxerOptions = dash, Bitexact = true });
            string[] names = [.. Directory.GetFiles(want).Select(f => Path.GetFileName(f)).Order()];
            Assert.Equal(names, Directory.GetFiles(got).Select(f => Path.GetFileName(f)).Order());
            Assert.Contains("init-0.mp4", names);
            Assert.Contains("chunk-1-00003.m4s", names);
            foreach (string name in names)
                Assert.True(File.ReadAllBytes(Path.Combine(want, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(got, name))), name);

            string expected = Path.Combine(dir, "expected-fast.mp4"), actual = Path.Combine(dir, "fast.mp4");
            Ffmpeg("-v", "error", "-y", "-i", input, "-map", "0", "-c", "copy", "-fflags", "+bitexact", "-movflags", "+faststart", expected);
            Remux.Copy(input, actual, new RemuxOptions { Bitexact = true, MuxerOptions = new Dictionary<string, string> { ["movflags"] = "+faststart" } });
            Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));

            Assert.Throws<ArgumentException>(() => Remux.Copy(input,
                Path.Combine(dir, "x.mp4"),
                new RemuxOptions { MuxerOptions = new Dictionary<string, string> { ["no_such_option"] = "1" } }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // CENC (issue #171): FFmpeg's mp4 muxer encrypts, byte for byte ffmpeg's -encryption_* output, for DASH segments and a
    // plain .mp4. Widevine adds the pssh box (pssh-box.py's; CI checks against it with SHOTDETECTOR_WIDEVINE_PSSH) to each
    // init segment, which is otherwise ffmpeg's, and ContentProtection to the MPD; ffmpeg decrypts it back to the input.
    [Fact]
    public void EncryptedAsFfmpegWithWidevineSignalling()
    {
        if (Libs is null)
            return;
        const string Key = "00112233445566778899aabbccddeeff", KeyId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        string dir = Directory.CreateTempSubdirectory("framereader-remux-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.mp4");
            Ffmpeg(["-v", "error", "-y", .. Make.Split(' '), "-map", "0", "-map", "1", "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "aac", input]);
            var dash = new Dictionary<string, string>
            {
                ["seg_duration"] = "1",
                ["use_template"] = "1",
                ["use_timeline"] = "0",
                ["init_seg_name"] = "init-$RepresentationID$.mp4",
                ["media_seg_name"] = "chunk-$RepresentationID$-$Number%05d$.m4s",
                ["adaptation_sets"] = "id=0,streams=v id=1,streams=a",
            };
            var cenc = new EncryptionOptions { KeyId = KeyId, Key = Key };
            string want = Directory.CreateDirectory(Path.Combine(dir, "ffmpeg")).FullName;
            Ffmpeg(["-v", "error", "-y", "-i", input, "-map", "0", "-c", "copy", "-fflags", "+bitexact", "-f", "dash",
                .. dash.SelectMany(o => new[] { "-" + o.Key, o.Value }),
                "-format_options", $"encryption_scheme=cenc-aes-ctr:encryption_key={Key}:encryption_kid={KeyId}",
                Path.Combine(want, "manifest.mpd").Replace('\\', '/')]);
            string[] names = [.. Directory.GetFiles(want).Select(f => Path.GetFileName(f)).Order()];

            string plain = Directory.CreateDirectory(Path.Combine(dir, "plain")).FullName;
            Remux.Copy(input, Path.Combine(plain, "manifest.mpd"), new RemuxOptions { Container = "dash", MuxerOptions = dash, Encryption = cenc, Bitexact = true });
            foreach (string name in names)
                Assert.True(File.ReadAllBytes(Path.Combine(want, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(plain, name))), name);

            string wv = Directory.CreateDirectory(Path.Combine(dir, "widevine")).FullName;
            Remux.Copy(input, Path.Combine(wv, "manifest.mpd"), new RemuxOptions { Container = "dash", MuxerOptions = dash, Encryption = cenc with { Widevine = true }, Bitexact = true });
            Assert.Equal(names, Directory.GetFiles(wv).Select(f => Path.GetFileName(f)).Order());

            // pssh: version 0, Widevine's system ID, WidevinePsshData with the key ID (field 2)
            byte[] pssh = Convert.FromHexString("000000327073736800000000edef8ba979d64acea3c827dcd51d21ed000000121210" + KeyId);
            if (Environment.GetEnvironmentVariable("SHOTDETECTOR_WIDEVINE_PSSH") is { Length: > 0 } fromPsshBox)
                Assert.Equal(Convert.FromBase64String(fromPsshBox.Trim()), pssh);
            foreach (string name in names.Where(n => n.StartsWith("init-", StringComparison.Ordinal)))
            {
                byte[] ffmpegs = File.ReadAllBytes(Path.Combine(want, name)), ours = File.ReadAllBytes(Path.Combine(wv, name));
                int moov = ffmpegs.AsSpan().IndexOf("moov"u8) - 4, moovEnd = moov + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(ffmpegs.AsSpan(moov));
                byte[] expected = [.. ffmpegs.AsSpan(0, moovEnd), .. pssh, .. ffmpegs.AsSpan(moovEnd)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(moov), moovEnd - moov + pssh.Length);
                Assert.True(expected.AsSpan().SequenceEqual(ours), name);
            }

            foreach (string name in names.Where(n => n.EndsWith(".m4s", StringComparison.Ordinal)))
                Assert.True(File.ReadAllBytes(Path.Combine(want, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(wv, name))), name);

            var mpd = System.Xml.Linq.XDocument.Load(Path.Combine(wv, "manifest.mpd"));
            System.Xml.Linq.XNamespace ns = "urn:mpeg:dash:schema:mpd:2011", cencNs = "urn:mpeg:cenc:2013";
            var sets = mpd.Descendants(ns + "AdaptationSet").ToList();
            Assert.Equal(2, sets.Count);
            foreach (var set in sets)
            {
                var protections = set.Elements(ns + "ContentProtection").ToList();
                Assert.Equal("urn:mpeg:dash:mp4protection:2011", protections[0].Attribute("schemeIdUri")?.Value);
                Assert.Equal("a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90", protections[0].Attribute(cencNs + "default_KID")?.Value);
                Assert.Equal("urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", protections[1].Attribute("schemeIdUri")?.Value);
                Assert.Equal(Convert.ToBase64String(pssh), protections[1].Element(cencNs + "pssh")?.Value);
            }

            // ffmpeg reads the Widevine stream with the key: the input's video and audio, frame for frame.
            foreach (var (id, kind) in new[] { ("0", "v"), ("1", "a") })
            {
                string joined = Path.Combine(dir, $"joined-{id}.mp4");
                File.WriteAllBytes(joined, [.. new[] { $"init-{id}.mp4" }.Concat(names.Where(n => n.StartsWith($"chunk-{id}-", StringComparison.Ordinal)))
                    .SelectMany(n => File.ReadAllBytes(Path.Combine(wv, n)))]);
                string Hashes(params string[] args)
                {
                    string md5 = Path.Combine(dir, "frames.md5");
                    Ffmpeg(["-v", "error", "-y", .. args, "-map", "0:" + kind, "-f", "framemd5", md5]);
                    return string.Join("\n", File.ReadAllLines(md5).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[^1]));
                }

                Assert.Equal(Hashes("-i", input), Hashes("-decryption_key", Key, "-i", joined));
            }

            string mp4 = Path.Combine(dir, "enc.mp4"), ffmpegMp4 = Path.Combine(dir, "expected-enc.mp4");
            Ffmpeg("-v",
                "error",
                "-y",
                "-i",
                input,
                "-map",
                "0",
                "-c",
                "copy",
                "-fflags",
                "+bitexact",
                "-encryption_scheme",
                "cenc-aes-ctr",
                "-encryption_key",
                Key,
                "-encryption_kid",
                KeyId,
                ffmpegMp4);
            Remux.Copy(input, mp4, new RemuxOptions { Encryption = cenc, Bitexact = true });
            Assert.Equal(File.ReadAllBytes(ffmpegMp4), File.ReadAllBytes(mp4));

            Assert.Throws<ArgumentException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { Encryption = cenc with { Widevine = true } }));
            Assert.Throws<ArgumentException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { Encryption = cenc with { Key = "0011" } }));
            Assert.Throws<ArgumentException>(() => Remux.Copy(input, Path.Combine(dir, "x.mp4"), new RemuxOptions { Encryption = cenc with { KeyId = new string('g', 32) } }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
