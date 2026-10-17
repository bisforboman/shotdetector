// Microbenchmarks of the per-frame pixel work (README.md here): CvResize.ResizeRows from each source size to
// scenedetect's processed size, and EdgeDetector.Detect on the V plane of the resized frames. Per case, the best-of-N
// microseconds per frame and a SHA-256 of the outputs, so a change in results shows next to a change in speed.
// Usage: dotnet run -c Release --project benchmarks/micro -- [--hash-only] [--set all|real|synthetic]
//        [--frames N] [--rounds N] [--filter TEXT] [--films DIR] [--video FILE]...
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ShotDetector;
using ShotDetector.Micro;

var o = Options.Parse(args);
var sources = new List<Source>();
if (o.Set is "all" or "synthetic")
{
    foreach (var (w, h) in Films.Sizes)
        sources.Add(Synthetic.Make(w, h, o.Frames));
}

if (o.Set is "all" or "real")
    sources.AddRange(Films.Load(o));

var cases = sources.SelectMany(Case.For).Where(c => o.Filter is null || c.Name.Contains(o.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
if (cases.Count == 0)
{
    Console.Error.WriteLine("nothing to run (no films found, or --filter matched no case)");
    return 1;
}

foreach (var s in sources)
    Console.WriteLine($"inputs  {s.Set,-9} {s.Name,-36} {s.Frames.Length,3} frames  {Hex(Sha(s.Frames))}");
Console.WriteLine();

// One pass each: the hash, and the warm-up (tier-1 code before the timed rounds).
var total = new List<byte>();
foreach (var c in cases)
{
    c.Hash = Hex(c.Run(hash: true));
    for (int k = 0; k < 3 && !o.HashOnly; k++)
        c.Run(hash: false);
}

// Rounds interleave the cases, so a burst of noise on the machine doesn't land on one case's every round.
for (int r = 0; r < o.Rounds && !o.HashOnly; r++)
{
    foreach (var c in cases)
    {
        long t = Stopwatch.GetTimestamp();
        c.Run(hash: false);
        double us = Stopwatch.GetElapsedTime(t).TotalMicroseconds / c.Inputs.Length;
        c.Best = Math.Min(c.Best, us);
    }
}

Console.WriteLine(o.HashOnly ? $"{"set",-9} {"case",-48} outputs (SHA-256)" : $"{"set",-9} {"case",-48} {"us/frame",9}  outputs (SHA-256)");
foreach (var c in cases)
{
    string time = o.HashOnly ? "" : $" {c.Best,9:F1} ";
    Console.WriteLine($"{c.Set,-9} {c.Name,-48}{time} {c.Hash[..16]}");
    total.AddRange(System.Text.Encoding.ASCII.GetBytes(c.Name + c.Hash));
}

Console.WriteLine();
Console.WriteLine($"all outputs: {Hex(SHA256.HashData(total.ToArray()))}");
if (!o.HashOnly)
    Console.WriteLine($"best of {o.Rounds} rounds, {Environment.ProcessorCount} CPUs, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; timings hold for this machine only");
return 0;

static byte[] Sha(byte[][] items)
{
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (byte[] item in items)
        sha.AppendData(item);
    return sha.GetHashAndReset();
}

static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

namespace ShotDetector.Micro
{
    /// <summary>Frames to run the cases on: packed BGR at the source size.</summary>
    sealed record Source(string Set, string Name, int Width, int Height, byte[][] Frames);

    /// <summary>One timed operation over a set of inputs; Run applies it to each and can hash the outputs.</summary>
    sealed class Case(string set, string name, byte[][] inputs, int outputBytes, Action<byte[], byte[]> op)
    {
        readonly byte[] _out = new byte[outputBytes];

        public string Set => set;

        public string Name => name;

        public byte[][] Inputs => inputs;

        public string Hash { get; set; } = "";

        public double Best { get; set; } = double.MaxValue;

        public byte[] Run(bool hash)
        {
            using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            foreach (byte[] input in inputs)
            {
                op(input, _out);
                sha?.AppendData(_out);
            }

            return sha?.GetHashAndReset() ?? [];
        }

        /// <summary>
        /// The resize to scenedetect's processed size (VideoReader.DownscaledSize), and the edge map of the resized
        /// frames' V plane at that size: the in-process pipeline's two steps (FrameWriter, ContentScorer).
        /// </summary>
        public static IEnumerable<Case> For(Source s)
        {
            var (dw, dh) = VideoReader.DownscaledSize(s.Width, s.Height);
            var resizer = new CvResize(s.Width, s.Height, dw, dh);
            int stride = s.Width * 3;
            yield return new Case(s.Set, $"resize {s.Name} -> {dw}x{dh}", s.Frames, dw * dh * 3, (f, dst) => resizer.ResizeRows(f, stride, 0, 0, dst));

            var lum = s.Frames.Select(f =>
            {
                byte[] bgr = new byte[dw * dh * 3], h = new byte[dw * dh], sat = new byte[dw * dh], v = new byte[dw * dh];
                resizer.ResizeRows(f, stride, 0, 0, bgr);
                Hsv.Convert(bgr, h, sat, v);
                return v;
            }).ToArray();
            var edges = new EdgeDetector(dw, dh);
            yield return new Case(s.Set, $"edges  {s.Name} at {dw}x{dh}", lum, dw * dh, (v, dst) => edges.Detect(v, dst));
        }
    }

    sealed class Options
    {
        public string Set { get; private set; } = "all";

        public int Frames { get; private set; } = 60;

        public int Rounds { get; private set; } = 15;

        public bool HashOnly { get; private set; }

        public string? Filter { get; private set; }

        public string? FilmsDir { get; private set; }

        public List<string> Videos { get; } = [];

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--hash-only":
                        o.HashOnly = true;
                        break;
                    case "--set":
                        o.Set = Next() is "all" or "real" or "synthetic" ? args[i] : throw new ArgumentException("--set: all, real or synthetic");
                        break;
                    case "--frames":
                        o.Frames = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--rounds":
                        o.Rounds = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--filter":
                        o.Filter = Next();
                        break;
                    case "--films":
                        o.FilmsDir = Next();
                        break;
                    case "--video":
                        o.Videos.Add(Next());
                        break;
                    default:
                        throw new ArgumentException($"unknown option {args[i]} (see benchmarks/micro/README.md)");
                }
            }

            return o;
        }
    }

    /// <summary>
    /// Real frames: keyframes from the public-domain films tools/realworld.py downloads into samples/realworld (one per
    /// source size), converted by ffmpeg as the pipelines convert them (scale=W:H:flags=bicubic,format=bgr24), and
    /// cached next to this program (frames/), not in git. Keyframes decode fast and are whole pictures, not fades.
    /// </summary>
    static class Films
    {
        public static readonly (int W, int H)[] Sizes = [(426, 240), (640, 360), (1280, 534), (1920, 1080)];

        // Name in tools/realworld.py, and where to start (past the titles).
        static readonly (string Name, int Start)[] Known =
        [
            ("elephants_dream_426x240_h264.mp4", 60),
            ("bbb_640x360_h264.mp4", 60),
            ("tears_of_steel_1280x534_h264.mov", 60),
            ("sintel_trailer_1920x1080_h264.mp4", 5),
        ];

        static string Ffmpeg(string tool) =>
            Path.Combine(Environment.GetEnvironmentVariable("BENCH_FFMPEG") ?? "", OperatingSystem.IsWindows() ? tool + ".exe" : tool);

        public static IEnumerable<Source> Load(Options o)
        {
            var films = o.Videos.Count > 0
                ? o.Videos.Select(v => (Path: v, Start: 0)).ToList()
                : Known.Select(k => (Path: Path.Combine(o.FilmsDir ?? Path.Combine(RepoRoot(), "samples", "realworld"), k.Name), k.Start)).ToList();
            var missing = films.Where(f => !File.Exists(f.Path)).Select(f => Path.GetFileName(f.Path)).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"real set: skipping {string.Join(", ", missing)} (python tools/realworld.py --download {string.Join(' ', missing)}, or --films DIR / --video FILE)");
            }

            foreach (var (path, start) in films.Where(f => File.Exists(f.Path)))
            {
                var (w, h) = Size(path);
                string cache = Path.Combine(AppContext.BaseDirectory, "frames", $"{Path.GetFileNameWithoutExtension(path)}-{w}x{h}-{start}s-{o.Frames}.bgr");
                if (!File.Exists(cache))
                    Extract(path, start, w, h, o.Frames, cache);
                byte[] all = File.ReadAllBytes(cache);
                int size = w * h * 3;
                if (all.Length == 0 || all.Length % size != 0)
                    throw new InvalidOperationException($"{cache}: not whole {w}x{h} frames; delete it to extract again");
                var frames = Enumerable.Range(0, all.Length / size).Select(i => all.AsSpan(i * size, size).ToArray()).ToArray();
                yield return new Source("real", $"{Path.GetFileName(path)} {w}x{h}", w, h, frames);
            }
        }

        static (int W, int H) Size(string path)
        {
            string text = Run(Ffmpeg("ffprobe"), ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path]).Trim();
            string[] wh = text.Split(',');
            return (int.Parse(wh[0], CultureInfo.InvariantCulture), int.Parse(wh[1], CultureInfo.InvariantCulture));
        }

        static void Extract(string path, int start, int w, int h, int frames, string cache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            string partial = cache + ".partial";
            string[] args =
            [
                "-v", "error", "-ss", start.ToString(CultureInfo.InvariantCulture), "-skip_frame", "nokey", "-i", path, "-an", "-sn",
                "-vf", $"scale={w}:{h}:flags=bicubic,format=bgr24", "-fps_mode", "passthrough",
                "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-y", partial,
            ];
            Run(Ffmpeg("ffmpeg"), args);
            File.Move(partial, cache, overwrite: true);
        }

        static string Run(string exe, string[] args)
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"can't start {exe}");
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: exit {p.ExitCode}\n{err.Result}");
            return output;
        }

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ShotDetector.slnx")))
                    return dir.FullName;
            }

            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>
    /// A deterministic set (SplitMix64, fixed seeds), for runs without the films: shaded gradients with overlapping
    /// rectangles and discs that move between frames, plus a little noise, so it has edges and flat areas like a picture.
    /// It understates the edge detector's cost (real frames have more weak edges); compare like with like.
    /// </summary>
    static class Synthetic
    {
        public static Source Make(int w, int h, int count)
        {
            ulong state = (ulong)(w * 7919 + h);
            ulong Next()
            {
                ulong z = state += 0x9E3779B97F4A7C15;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
                return z ^ (z >> 31);
            }

            int Rand(int n) => (int)(Next() % (ulong)n);
            var shapes = Enumerable.Range(0, 24).Select(_ => (X: Rand(w), Y: Rand(h), R: 4 + Rand(Math.Max(5, h / 4)), Disc: Rand(2) == 0,
                B: (byte)Rand(256), G: (byte)Rand(256), Rr: (byte)Rand(256), Dx: Rand(9) - 4, Dy: Rand(9) - 4)).ToArray();
            var frames = new byte[count][];
            for (int f = 0; f < count; f++)
            {
                byte[] img = frames[f] = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 3;
                        (img[i], img[i + 1], img[i + 2]) = ((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x + y + f * 3) & 255));
                    }
                }

                foreach (var s in shapes)
                {
                    int cx = s.X + s.Dx * f, cy = s.Y + s.Dy * f;
                    for (int y = Math.Max(0, cy - s.R); y < Math.Min(h, cy + s.R); y++)
                    {
                        for (int x = Math.Max(0, cx - s.R); x < Math.Min(w, cx + s.R); x++)
                        {
                            if (s.Disc && (x - cx) * (x - cx) + (y - cy) * (y - cy) > s.R * s.R)
                                continue;
                            int i = (y * w + x) * 3;
                            (img[i], img[i + 1], img[i + 2]) = (s.B, s.G, s.Rr);
                        }
                    }
                }

                for (int i = 0; i < img.Length; i++)
                    img[i] = (byte)Math.Clamp(img[i] + Rand(13) - 6, 0, 255);
            }

            return new Source("synthetic", $"{w}x{h}", w, h, frames);
        }
    }
}
