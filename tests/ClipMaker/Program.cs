// ShotDetector.Tests' clips (the Clips fixture), made in-process with FrameReader but in a process of their own:
// loaded into the test process, FFmpeg's libraries would make ShotDetector's Auto decoder use them in every test.
// Usage: ClipMaker <FFmpeg 8 libraries folder> <output folder>
using FrameReader;

FFmpegLibraries.Load(args[0]);
string dir = args[1];
string At(string name) => Path.Combine(dir, name);

var three = new TestMediaOptions
{
    Video = ["testsrc2=d=2", "smptebars=d=2", "mandelbrot,trim=duration=2"],
    VideoEncoder = "mpeg4",
    VideoQuality = 3,
};
TestMedia.Write(At("three.mp4"), three);
TestMedia.Write(At("long.mp4"), three with { Video = ["testsrc2=d=300"], Size = (640, 360) });
Remux.Copy(At("three.mp4"), At("rotated.mp4"), new() { DisplayRotation = 90 });
Remux.Copy(At("three.mp4"), At("three.mkv"));
Remux.Copy(At("three.mp4"), At("three-faststart.mp4"), new() { MuxerOptions = new Dictionary<string, string> { ["movflags"] = "+faststart" } });
TestMedia.Write(At("three.mpg"), three with { VideoEncoder = "mpeg2video" });
TestMedia.Write(At("interlaced.mpg"), three with { VideoEncoder = "mpeg2video", FieldOrder = FieldOrder.TopFirst });
Directory.CreateDirectory(At("frames"));
TestMedia.Write(At(Path.Combine("frames", "%04d.png")), three with { VideoEncoder = "png", VideoQuality = null });

// Packets 60, 61 and 120 damaged: the decoder drops those frames.
var positions = MediaProbe.Probe(At("three.mp4"), new() { PacketTimestamps = true }).PacketPositions!;
var bytes = File.ReadAllBytes(At("three.mp4"));
foreach (int k in new[] { 60, 61, 120 })
    bytes.AsSpan((int)positions[k]!.Value, 4).Fill(0xff);
File.WriteAllBytes(At("dropped.mp4"), bytes);
