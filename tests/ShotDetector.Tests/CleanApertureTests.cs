using System.Buffers.Binary;
using ShotDetector;

/// <summary>
/// A MOV/MP4 with a clean aperture ('clap', as iPhones write) is read whole, as OpenCV reads it: libavcodec alone doesn't
/// crop to the container's aperture, where ffmpeg's command line does from FFmpeg 7.1 (FFmpeg 8.1 exposes the box as
/// frame-cropping side data). So detection and save-images give the same as on the file without the box, through the
/// executable and in-process. The in-process cases need SHOTDETECTOR_FFMPEG_LIBS; without them they do nothing.
/// </summary>
public sealed class CleanApertureTests : IDisposable
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
    readonly string _dir = Directory.CreateTempSubdirectory("shotdetector-clap-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    string Ffmpeg => Libs is null ? "ffmpeg" : Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>Two shots of 320x240 H.264 in a MOV, and the same file with a 'clap' of 256x192 in the middle.</summary>
    (string Plain, string Clap) Clips()
    {
        string plain = Path.Combine(_dir, "plain.mov"), clap = Path.Combine(_dir, "clap.mov");
        VideoReader.Run(Ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=1", "-f", "lavfi", "-i",
                "mandelbrot=s=320x240:r=25,trim=duration=1", "-filter_complex", "[0][1]concat=n=2:v=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", plain],
            default);
        File.WriteAllBytes(clap, WithCleanAperture(File.ReadAllBytes(plain), 256, 192));
        return (plain, clap);
    }

    [Fact]
    public void TheBoxIsWhatFfmpegCropsTo()
    {
        // Without -apply_cropping codec, FFmpeg 8.1's command line gives 256x192 frames: the box is read.
        var (_, clap) = Clips();
        string probe = VideoReader.Run(Path.Combine(Path.GetDirectoryName(Ffmpeg) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"),
            ["-v", "error", "-select_streams", "v:0", "-show_streams", clap],
            default);
        Assert.Contains("crop_left=32", probe, StringComparison.Ordinal);
        Assert.Contains("crop_top=24", probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWholeAsOpenCvDoes(bool inProcess)
    {
        if (inProcess && Libs is null)
            return;
        var (plain, clap) = Clips();
        var options = new DetectionOptions
        {
            Detector = DetectorKind.Content,
            CollectStats = true,
            FfmpegDirectory = Libs,
            Decoder = inProcess ? VideoDecoder.InProcess : VideoDecoder.FfmpegProcess,
        };
        var expected = ShotDetection.Detect(plain, options);
        var actual = ShotDetection.Detect(clap, options);
        Assert.Equal(inProcess, actual.Video.DecodedInProcess);
        Assert.Equal((320, 240), (actual.Video.Width, actual.Video.Height));
        Assert.Equal(2, actual.Shots.Count);
        Assert.Equal(expected.Shots.Select(s => s.Start.FrameNum), actual.Shots.Select(s => s.Start.FrameNum));
        Assert.Equal(expected.Stats!.Csv(expected.Video.Position), actual.Stats!.Csv(actual.Video.Position));

        // save-images: OpenCV's frames too (the executable's own run, or FrameReader's encoder).
        var mine = Export.SaveImages(actual, Path.Combine(_dir, $"clap-{inProcess}"), new ImageOptions { Format = "png" });
        var theirs = Export.SaveImages(expected, Path.Combine(_dir, $"plain-{inProcess}"), new ImageOptions { Format = "png" });
        Assert.Equal(theirs.Count, mine.Count);
        for (int i = 0; i < mine.Count; i++)
            Assert.Equal(File.ReadAllBytes(theirs[i]), File.ReadAllBytes(mine[i]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAnFfmpegThatCropsIsToldNotTo(bool has)
    {
        // A stand-in ffmpeg printing its options as 8.1 or 6.1 does (shell script: not on Windows).
        if (OperatingSystem.IsWindows())
            return;
        string exe = Path.Combine(_dir, "ffmpeg");
        string option = has ? "-apply_cropping[:<stream_spec>]  select the cropping to apply" : "";
        File.WriteAllText(exe, $"#!/bin/sh\necho '-autorotate[:<stream_spec>]  automatically insert correct rotate filters'\necho '{option}'\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] expected = has ? ["-apply_cropping", "codec"] : [];
        Assert.Equal(expected, VideoReader.IgnoreContainerCropping(exe, CancellationToken.None));
        Assert.Equal(expected, VideoReader.IgnoreContainerCropping(exe, CancellationToken.None)); // cached
    }

    /// <summary>
    /// The file with a centred 'clap' box of <paramref name="width"/> x <paramref name="height"/> appended to its video
    /// sample entry (moov/trak/mdia/minf/stbl/stsd/avc1), every enclosing box grown by its size. ffmpeg writes the moov
    /// after the mdat, so no chunk offset moves.
    /// </summary>
    static byte[] WithCleanAperture(byte[] file, int width, int height)
    {
        var clap = new byte[40];
        BinaryPrimitives.WriteInt32BigEndian(clap, clap.Length);
        "clap"u8.CopyTo(clap.AsSpan(4));
        int[] fields = [width, 1, height, 1, 0, 1, 0, 1]; // width, height, horizontal and vertical offsets: N/D each
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(clap.AsSpan(8 + 4 * i), fields[i]);

        // The path down, each box's start; stsd's entries follow its version, flags and count, avc1's children its 78 bytes.
        var starts = new List<int>();
        int at = 0, end = file.Length;
        foreach (string name in new[] { "moov", "trak", "mdia", "minf", "stbl", "stsd", "avc1" })
        {
            while (Type(file, at) != name)
            {
                at += BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
                Assert.True(at < end, $"no {name} box");
            }

            starts.Add(at);
            end = at + BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            at += name switch { "stsd" => 16, "avc1" => 86, _ => 8 };
        }

        byte[] result = [.. file.AsSpan(0, end), .. clap, .. file.AsSpan(end)];
        foreach (int start in starts)
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(start), BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(start)) + clap.Length);
        return result;
    }

    static string Type(byte[] file, int at) => System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
}
