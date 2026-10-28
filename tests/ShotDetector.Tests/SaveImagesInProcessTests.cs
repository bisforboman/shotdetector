using ShotDetector;

/// <summary>
/// save-images in-process (FrameReader's ImageEncoder from decoded frames) writes the files the ffmpeg executable of the
/// same FFmpeg build writes, byte for byte: for each format, with the size options, several images per shot (one ffmpeg
/// run writes them all), on sources whose depth, aspect ratio, rotation and fields reach the bytes. Only where the ffmpeg
/// beside the libraries (SHOTDETECTOR_FFMPEG_LIBS) is a shared build, so runs on those very libraries (BtbN's shared
/// build in CI); CI's main run has a static ffmpeg beside our own build, whose bytes needn't match, and skips these.
/// With SHOTDETECTOR_REQUIRE_SAME_BUILD=1 not being able to compare is a failure.
/// </summary>
public class SaveImagesInProcessTests(Clips clips) : IClassFixture<Clips>
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    static bool CanCompare()
    {
        string ffmpeg = Path.Combine(Libs ?? "", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        bool can = Libs is not null && File.Exists(ffmpeg)
            && VideoReader.Run(ffmpeg, ["-hide_banner", "-version"], default).Contains("--enable-shared", StringComparison.Ordinal);
        if (!can && Environment.GetEnvironmentVariable("SHOTDETECTOR_REQUIRE_SAME_BUILD") == "1")
            Assert.Fail($"SHOTDETECTOR_REQUIRE_SAME_BUILD is set, but {Libs} has no shared-build ffmpeg of its own.");
        return can;
    }

    public static TheoryData<string> Sources => ["three", "rotated", "interlaced", "images", "10bit", "anamorphic"];

    [Theory]
    [MemberData(nameof(Sources))]
    public void SameFilesAsTheExecutable(string source)
    {
        if (!CanCompare())
            return;
        string dir = Directory.CreateTempSubdirectory("shotdetector-images-").FullName;
        try
        {
            string path = source switch
            {
                "rotated" => clips.Rotated,
                "interlaced" => clips.Interlaced,
                "images" => clips.ThreeShotsImages,
                "10bit" => Copy(dir, "10bit.mkv", "-c:v ffv1 -pix_fmt yuv420p10le"),
                "anamorphic" => Copy(dir, "anamorphic.mkv", "-c:v ffv1 -aspect 16:9"),
                _ => clips.ThreeShots,
            };
            var options = new DetectionOptions { Detector = DetectorKind.Content, FfmpegDirectory = Libs };
            var executable = ShotDetection.Detect(path, options with { Decoder = VideoDecoder.FfmpegProcess });
            var inProcess = ShotDetection.Detect(path, options with { Decoder = VideoDecoder.InProcess });
            Assert.False(executable.Video.DecodedInProcess);
            Assert.True(inProcess.Video.DecodedInProcess);
            var cases = new ImageOptions[]
            {
                new(),
                new() { Quality = 40, Width = 161 },
                new() { Format = "png" },
                new() { Format = "png", Compression = 1, Height = 91 },
                new() { Format = "webp" },
                new() { Format = "webp", Quality = 70, Scale = 0.37 },
                new() { Format = "jpg", Width = 100, Height = 100, NumImages = 1 },
            };
            var failures = new List<string>();
            for (int c = 0; c < cases.Length; c++)
            {
                var expected = Export.SaveImages(executable, Path.Combine(dir, $"exe{c}"), cases[c]);
                var actual = Export.SaveImages(inProcess, Path.Combine(dir, $"lib{c}"), cases[c]);
                Assert.Equal(expected.Select(Path.GetFileName), actual.Select(Path.GetFileName));
                Assert.Equal(cases[c].NumImages * executable.Shots.Count, expected.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    if (!File.ReadAllBytes(expected[i]).AsSpan().SequenceEqual(File.ReadAllBytes(actual[i])))
                        failures.Add($"{source} {cases[c]}: {Path.GetFileName(expected[i])} differs");
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    string Copy(string dir, string name, string args)
    {
        string path = Path.Combine(dir, name);
        VideoReader.Run(Path.Combine(Libs!, "ffmpeg"), ["-v", "error", "-y", "-i", clips.ThreeShots, .. args.Split(' '), path], default);
        return path;
    }
}
