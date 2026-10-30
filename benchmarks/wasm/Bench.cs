// The frame-source path's benchmark (benchmark.yml, README.md here), shared by the WebAssembly build (Wasm/) and the
// native one (Native/): raw yuv420p frames read from a file, ShotDetection.Detect(YuvVideo, frames) with FastYuv's
// converter, the content and adaptive detectors and content with stats, best of N runs, one JSON line on stdout. The
// CSVs' SHA-256 lets the job check the WebAssembly run's results against the native run's on the same frames. Before
// the runs, the other frame-source entry points on each input's first frames must give Detect's CSVs (CheckPaths); being
// here also keeps them in the trimmed WebAssembly bundle, which ci.yml's wasm-trim job checks for FFmpeg's code.
//
// Usage: <host> [--runs N] name=path,width,height,num/den,frames,colorspace ...
// (the raw file holds at least `frames` frames of width x height yuv420p; colorspace as ffprobe names it, or "")
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShotDetector.FastYuv;

namespace ShotDetector.WasmBench;

/// <summary>Reads <c>buffer.Length</c> bytes of a file at <paramref name="position"/>: how many it read, fewer at its end.</summary>
/// <param name="position">The offset in the file.</param>
/// <param name="buffer">Where the bytes go.</param>
/// <returns>The bytes read.</returns>
delegate int ReadAt(long position, Span<byte> buffer);

/// <summary>A host's access to a file of raw frames.</summary>
/// <param name="Open">Opens a file by its path: a handle.</param>
/// <param name="Read">Reads part of an open file.</param>
/// <param name="Close">Closes a handle.</param>
sealed record Host(Func<string, int> Open, Func<int, ReadAt> Read, Action<int> Close);

/// <summary>The benchmark itself, whatever the host.</summary>
static class Bench
{
    static readonly (string Detector, DetectorKind Kind, bool Stats)[] Cases =
    [
        ("content", DetectorKind.Content, false),
        ("adaptive", DetectorKind.Adaptive, false),
        ("content", DetectorKind.Content, true),
    ];

    /// <summary>Runs every case on every input and prints the results as one line of JSON.</summary>
    /// <param name="args">The command line (above).</param>
    /// <param name="host">How the host reads files.</param>
    /// <param name="hostName">"native" or "wasm".</param>
    /// <param name="heapBytes">The WebAssembly memory's size, or null natively.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, Host host, string hostName, Func<long?> heapBytes)
    {
        int runs = 3;
        var inputs = new List<Input>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--runs")
                runs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else
                inputs.Add(Input.Parse(args[i]));
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("Usage: [--runs N] name=path,width,height,num/den,frames,colorspace ...");
            return 2;
        }

        var cases = new List<Result>();
        foreach (var input in inputs)
        {
            int handle = host.Open(input.Path);
            try
            {
                var read = host.Read(handle);
                CheckPaths(input, read);
                foreach (var (detector, kind, stats) in Cases)
                    cases.Add(Measure(input, read, detector, kind, stats, runs));
            }
            finally
            {
                host.Close(handle);
            }
        }

        // Written by hand: reflection-based serialization is trimmed away on WebAssembly.
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("host", hostName);
            json.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            json.WriteString("arch", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            json.WriteNumber("runs", runs);
            if (heapBytes() is { } heap)
                json.WriteNumber("heap_bytes", heap);
            else
                json.WriteNull("heap_bytes");
            json.WriteStartArray("cases");
            foreach (var r in cases)
                r.Write(json);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        return 0;
    }

    static Result Measure(Input input, ReadAt read, string detector, DetectorKind kind, bool stats, int runs)
    {
        var video = new YuvVideo
        {
            Width = input.Width,
            Height = input.Height,
            FrameRate = input.Rate,
            ColorSpace = input.ColorSpace,
            FrameCount = input.Frames,
        };
        var options = new DetectionOptions { Detector = kind, CollectStats = stats, Yuv420Converter = new SwscaleYuv420() };
        var walls = new List<double>();
        double bestRead = 0;
        long gcPeak = 0;
        string? sceneHash = null, statsHash = null;
        int shots = 0;
        long frames = 0;
        for (int run = 0; run < runs; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var counters = new Counters();
            long start = Stopwatch.GetTimestamp();
            var result = ShotDetection.Detect(video, Frames(input, read, counters), options);
            double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (walls.Count == 0 || wall < walls.Min())
                bestRead = Stopwatch.GetElapsedTime(0, counters.ReadTicks).TotalMilliseconds;
            walls.Add(wall);
            gcPeak = Math.Max(gcPeak, counters.GcPeak);
            frames = counters.Frames;
            string scene = Hash(Shots.Csv(result.Shots, cuts: result.Cuts));
            string? statsCsv = result.Stats is { } s ? Hash(s.Csv(result.Video.Position)) : null;
            if (sceneHash is not null && (scene != sceneHash || statsCsv != statsHash))
                throw new InvalidOperationException($"{input.Name} {detector}: run {run + 1}'s CSVs differ from run 1's.");
            sceneHash = scene;
            statsHash = statsCsv;
            shots = result.Shots.Count;
        }

        double best = walls.Min();
        return new Result(input, detector, stats, frames, best, walls, bestRead, gcPeak, shots, sceneHash!, statsHash);
    }

    /// <summary>
    /// DetectAsync, ScoreRangeAsync and ScoreRange in two ranges (one through its bytes) and Merge, on the first frames of
    /// <paramref name="input"/>: each must give Detect's scene and stats CSVs, with nothing decoded in-process.
    /// </summary>
    static void CheckPaths(Input input, ReadAt read)
    {
        long frames = Math.Min(input.Frames, 300);
        int half = (int)(frames / 2);
        var video = new YuvVideo
        {
            Width = input.Width,
            Height = input.Height,
            FrameRate = input.Rate,
            ColorSpace = input.ColorSpace,
            FrameCount = frames,
        };
        var options = new DetectionOptions { Detector = DetectorKind.Content, CollectStats = true, Yuv420Converter = new SwscaleYuv420() };
        string Csvs(DetectionResult r) => Shots.Csv(r.Shots, cuts: r.Cuts) + r.Stats!.Csv(r.Video.Position);
        string expected = Csvs(ShotDetection.Detect(video, Frames(input, read, new Counters(), 0, frames), options));
        var detected = ShotDetection.DetectAsync(video, Frames(input, read, new Counters(), 0, frames).ToAsyncEnumerable(), options).GetAwaiter().GetResult();
        var first = ShotDetection.ScoreRangeAsync(video, Frames(input, read, new Counters(), 0, half + 1).ToAsyncEnumerable(), 0, options).GetAwaiter().GetResult();
        var second = ShotDetection.ScoreRange(video, Frames(input, read, new Counters(), half - 1, frames), half, options);
        var merged = ShotDetection.Merge(video, [RangeScores.FromBytes(first.ToBytes()), second], options);
        foreach (var (name, result) in new[] { ("DetectAsync", detected), ("ScoreRange and Merge", merged) })
        {
            if (Csvs(result) != expected || result.Video.DecodedInProcess)
                throw new InvalidOperationException($"{input.Name}: {name}'s CSVs differ from Detect's on the first {frames} frames, or it says it decoded in-process.");
        }
    }

    // One buffer for all frames: Detect reads a frame's planes before it asks for the next.
    static IEnumerable<YuvFrame> Frames(Input input, ReadAt read, Counters counters) => Frames(input, read, counters, 0, input.Frames);

    /// <summary>Frames <paramref name="from"/> to <paramref name="to"/> - 1 of the input.</summary>
    static IEnumerable<YuvFrame> Frames(Input input, ReadAt read, Counters counters, long from, long to)
    {
        int w = input.Width, h = input.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        int luma = w * h, chroma = cw * ch;
        var buffer = new byte[luma + (2 * chroma)];
        for (long i = from; i < to; i++)
        {
            long start = Stopwatch.GetTimestamp();
            int n = 0;
            while (n < buffer.Length)
            {
                int got = read((i * buffer.Length) + n, buffer.AsSpan(n));
                if (got <= 0)
                    throw new EndOfStreamException($"{input.Path}: frame {i} is past the file's end.");
                n += got;
            }

            counters.ReadTicks += Stopwatch.GetTimestamp() - start;
            counters.GcPeak = Math.Max(counters.GcPeak, GC.GetTotalMemory(false));
            counters.Frames++;
            yield return new YuvFrame(buffer.AsMemory(0, luma), w, buffer.AsMemory(luma, chroma), cw, buffer.AsMemory(luma + chroma, chroma), cw);
        }
    }

    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    sealed record Result(Input Input, string Detector, bool Stats, long Frames, double WallMs, List<double> RunsMs, double ReadMs, long GcPeak, int Shots, string SceneHash, string? StatsHash)
    {
        public void Write(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteString("film", Input.Name);
            json.WriteString("detector", Detector);
            json.WriteBoolean("stats", Stats);
            json.WriteNumber("width", Input.Width);
            json.WriteNumber("height", Input.Height);
            json.WriteNumber("frames", Frames);
            json.WriteNumber("wall_ms", Math.Round(WallMs, 1));
            json.WriteStartArray("runs_ms");
            foreach (double ms in RunsMs)
                json.WriteNumberValue(Math.Round(ms, 1));
            json.WriteEndArray();
            json.WriteNumber("read_ms", Math.Round(ReadMs, 1));

            // Detection alone: a raw 1080p frame is 3 MB, and reading it from the file takes a large share of the time
            // (natively too), where a decoder in the same process would hand its planes over.
            json.WriteNumber("detect_ms", Math.Round(WallMs - ReadMs, 1));
            json.WriteNumber("ns_per_pixel", Math.Round((WallMs - ReadMs) * 1e6 / ((double)Frames * Input.Width * Input.Height), 3));
            json.WriteNumber("gc_peak_bytes", GcPeak);
            json.WriteNumber("shots", Shots);
            json.WriteString("scene_csv_sha256", SceneHash);
            if (StatsHash is null)
                json.WriteNull("stats_csv_sha256");
            else
                json.WriteString("stats_csv_sha256", StatsHash);
            json.WriteEndObject();
        }
    }

    sealed class Counters
    {
        public long ReadTicks { get; set; }

        public long GcPeak { get; set; }

        public long Frames { get; set; }
    }

    sealed record Input(string Name, string Path, int Width, int Height, Fps Rate, long Frames, string ColorSpace)
    {
        public static Input Parse(string arg)
        {
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            string[] p = arg[(eq + 1)..].Split(',');
            if (eq <= 0 || p.Length != 6)
                throw new ArgumentException($"Not name=path,width,height,num/den,frames,colorspace: {arg}");
            string[] rate = p[3].Split('/');
            var c = CultureInfo.InvariantCulture;
            return new Input(arg[..eq], p[0], int.Parse(p[1], c), int.Parse(p[2], c), new Fps(int.Parse(rate[0], c), int.Parse(rate[1], c)), long.Parse(p[4], c), p[5]);
        }
    }
}
