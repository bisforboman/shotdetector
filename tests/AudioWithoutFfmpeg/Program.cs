// FrameReader.Tests' check that the tone and waveform from samples never touch FFmpeg (issue #494: a browser decodes
// the audio itself): run in a process of its own (the test process has FFmpeg.AutoGen loaded by other tests) with no
// ffmpeg on PATH and no FFmpeg libraries named, it feeds ReferenceToneFinder and WaveformBuilder generated samples (a
// 1 kHz tone with a break, then silence), then fails if FFmpeg.AutoGen or any of FFmpeg's libraries is loaded.
// Usage: AudioWithoutFfmpeg (prints the segments and points; exit code 0 when nothing of FFmpeg was loaded)
using FrameReader;

const int Rate = 48000, Channels = 2, Chunk = 1024;

// -18 dBFS 1 kHz for 0-6 s, a 0.5 s break, again to 10 s, then silence to 12 s; in chunks as a decoder gives them.
var samples = new float[12 * Rate * Channels];
for (int i = 0; i < samples.Length / Channels; i++)
{
    double t = (double)i / Rate;
    float x = t < 6 || (t >= 6.5 && t < 10) ? (float)(0.1259 * Math.Sin(2 * Math.PI * 1000 * t)) : 0;
    for (int c = 0; c < Channels; c++)
        samples[i * Channels + c] = x;
}

int reports = 0;
var progress = new Progress(() => reports++);
var finder = new ReferenceToneFinder(Rate, Channels, new ReferenceToneOptions { Progress = progress });
var builder = new WaveformBuilder(Rate, Channels, new WaveformOptions { PixelsPerSecond = 10, Progress = progress });
for (int at = 0; at < samples.Length; at += Chunk * Channels)
{
    var chunk = samples.AsSpan(at, Math.Min(Chunk * Channels, samples.Length - at));
    var time = TimeSpan.FromSeconds((double)at / Channels / Rate);
    finder.Add(chunk, time);
    builder.Add(chunk, time);
}

var segments = finder.Finish();
var waveform = builder.Finish();
foreach (var s in segments)
    Console.WriteLine($"Tone {s.Start.TotalSeconds:0.00}-{s.End.TotalSeconds:0.00} s at {s.Level:0.0} dBFS");
Console.WriteLine($"Waveform: {waveform.Length} points, {waveform.Max(0, 0)} peak");
var saved = new MemoryStream();
waveform.Save(saved);

var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n is not null && n.StartsWith("FFmpeg", StringComparison.Ordinal)).ToList();
var libraries = File.Exists("/proc/self/maps")
    ? File.ReadAllLines("/proc/self/maps").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
        .Where(p => Path.GetFileName(p) is { } f && (f.StartsWith("libav", StringComparison.Ordinal) || f.StartsWith("libsw", StringComparison.Ordinal) || f.StartsWith("libpostproc", StringComparison.Ordinal)))
        .Distinct()
        .ToList()
    : [];
if (segments.Count != 1 || Math.Abs(segments[0].End.TotalSeconds - 10) > 0.001 || waveform.Length != 120 || reports < 2)
{
    Console.Error.WriteLine($"Expected one segment to 10 s, 120 points and progress; got {segments.Count} segments, {waveform.Length} points, {reports} reports.");
    return 1;
}

foreach (var name in loaded.Concat(libraries))
    Console.Error.WriteLine($"Loaded: {name}");
return loaded.Count + libraries.Count == 0 ? 0 : 1;

// Counts reports at once, on the calling thread (Progress<T> posts to the thread pool).
sealed class Progress(Action report) : IProgress<MediaProgress>
{
    public void Report(MediaProgress value) => report();
}
