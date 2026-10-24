// ShotDetector.Tests' check that detection on frames from the caller never touches FFmpeg: run in a process of its own
// (the test process has FFmpeg.AutoGen loaded by other tests) with no ffmpeg on PATH and no FFmpeg libraries named, it
// detects on generated frames, then fails if FFmpeg.AutoGen or any of FFmpeg's libraries is loaded.
// Usage: FramesWithoutFfmpeg (prints the shots; exit code 0 when nothing of FFmpeg was loaded)
using System.Diagnostics;
using ShotDetector;
using ShotDetector.FastYuv;

const int Width = 320, Height = 240, ChromaWidth = Width / 2, ChromaHeight = Height / 2;
var video = new YuvVideo { Width = Width, Height = Height, FrameRate = new(25, 1), TimeBase = new(1, 12800), FrameCount = 100, Codec = "h264", Container = "mov,mp4,m4a,3gp,3g2,mj2" };

// Two shots of noise, a cut at frame 50.
IEnumerable<YuvFrame> Frames()
{
    for (int i = 0; i < 100; i++)
    {
        var random = new Random(i < 50 ? 1 : 2);
        byte[] Plane(int size)
        {
            var plane = new byte[size];
            random.NextBytes(plane);
            return plane;
        }

        yield return new YuvFrame(Plane(Width * Height), Width, Plane(ChromaWidth * ChromaHeight), ChromaWidth, Plane(ChromaWidth * ChromaHeight), ChromaWidth) { Pts = i * 512 };
    }
}

// A listener and progress too: the span's tags and the progress reports read the reader's decoder and pipeline.
using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded };
ActivitySource.AddActivityListener(listener);
int reports = 0;
var options = new DetectionOptions
{
    Detector = DetectorKind.Content,
    CollectStats = true,
    Yuv420Converter = new SwscaleYuv420(),
    Mode = DetectionMode.Parallel,
    Progress = new Progress<DetectionProgress>(_ => Interlocked.Increment(ref reports)),
};
var result = ShotDetection.Detect(video, Frames(), options);
Console.Write(Shots.Csv(result.Shots, cuts: result.Cuts));
_ = result.Stats!.Csv(result.Video.Position);

var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n is not null && n.StartsWith("FFmpeg", StringComparison.Ordinal)).ToList();
var libraries = File.Exists("/proc/self/maps")
    ? File.ReadAllLines("/proc/self/maps").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
        .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("libav", StringComparison.Ordinal) || f.StartsWith("libsw", StringComparison.Ordinal) || f.StartsWith("libpostproc", StringComparison.Ordinal)))
        .Distinct()
        .ToList()
    : [];
if (result.Shots.Count != 2)
{
    Console.Error.WriteLine($"Expected 2 shots, got {result.Shots.Count}.");
    return 1;
}

foreach (string name in loaded.Concat(libraries))
    Console.Error.WriteLine($"Loaded: {name}");
return loaded.Count + libraries.Count == 0 ? 0 : 1;
