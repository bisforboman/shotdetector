// ShotDetector and FrameReader end to end: describe a video, find its shots (with scenedetect's results), look for
// line-up tone, and write its waveform for a viewer such as peaks.js.
//
//   dotnet run --project examples/ShotDetector.Example -- <video>
//
// Needs FFmpeg 8's shared libraries (add FrameReader.Native.<rid>) or ffmpeg on PATH.
using FrameReader;
using ShotDetector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ShotDetector.Example <video>");
    return 2;
}
string video = args[0];

// 1. What the file is: from its headers, without decoding.
var info = ShotDetection.Probe(video);
Console.WriteLine($"{info.Container}: {info.Codec} {info.Width}x{info.Height} at {info.FrameRate.Value:0.###} fps, {info.Duration}");
foreach (var stream in info.Streams ?? [])
    Console.WriteLine($"  #{stream.Index} {stream.Type} {stream.Codec} {stream.Language}");

// 2. Shots, as scenedetect's detect-adaptive finds them; DetectionOptions has the other detectors and their settings.
var result = ShotDetection.Detect(video, new DetectionOptions
{
    Progress = new Progress<DetectionProgress>(p => Console.Error.Write($"\r{p.Fraction:P0}  ")),
});
Console.Error.WriteLine();
foreach (var shot in result.Shots)
    Console.WriteLine($"Shot {shot.Number}: {shot.Start} - {shot.End} ({shot.Duration.Seconds:0.00} s)");
File.WriteAllText(Path.ChangeExtension(video, ".shots.csv"), Shots.Csv(result.Shots));

// 3. Reference tone (1 kHz line-up tone), searched in the first three minutes only.
if (info.HasAudio == true)
{
    var tone = ShotDetection.FindReferenceTone(video, new DetectionOptions { ReferenceTone = new() { SearchDuration = TimeSpan.FromMinutes(3) } });
    Console.WriteLine(tone.Count == 0 ? "No reference tone" : $"Reference tone at {tone[0].Start} - {tone[0].End}, {tone[0].Level:0.0} dBFS");

    // 4. FrameReader directly: the audio's waveform as BBC audiowaveform's .dat, 50 points a second.
    using var audio = new AudioReader(video);
    var waveform = WaveformData.Read(audio, new WaveformOptions { PixelsPerSecond = 50 });
    using var dat = File.Create(Path.ChangeExtension(video, ".dat"));
    waveform.Save(dat);
    Console.WriteLine($"Waveform: {waveform.Length} points");
}
return 0;
