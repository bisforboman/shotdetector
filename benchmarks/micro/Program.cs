// Microbenchmarks of the per-frame pixel work (README.md here): CvResize.ResizeRows from each source size to
// scenedetect's processed size, EdgeDetector.Detect on the V plane of the resized frames, and ShotDetector.FastYuv's
// yuv420p, yuv422p and yuv420p10le conversions. Per case, the best-of-N microseconds per frame and a SHA-256 of the outputs, so a change in results
// shows next to a change in speed.
// Usage: dotnet run -c Release --project benchmarks/micro -- [--hash-only] [--set all|real|synthetic]
//        [--frames N] [--rounds N] [--filter TEXT] [--films DIR] [--video FILE]...
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ShotDetector;
using ShotDetector.FastYuv;
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
Console.WriteLine($"all inputs:  {Hex(Sha([.. sources.SelectMany(src => src.Frames)]))}");
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

            // ShotDetector.FastYuv's yuv420p conversion where the resize reads (SourceRows x SourceCols, as ResizeYuv
            // asks), and of whole rows, on yuv420p planes made from the frames (any planes do: the cost doesn't depend
            // on the values, and the hash only has to be the same between builds). Then the same for yuv422p (its own
            // chroma row per row), and yuv420p10le with left-sited chroma (h264's: a 4-tap horizontal chroma filter) as
            // the pipeline converts it, all the pixels the resize reads in one call (FrameToBgr, packed).
            var yuv = s.Frames.Select(f => Yuv(f, s.Width, s.Height, (s.Height + 1) / 2, tenBit: false)).ToArray();
            var yuv422 = s.Frames.Select(f => Yuv(f, s.Width, s.Height, s.Height, tenBit: false)).ToArray();
            var yuv10 = s.Frames.Select(f => Yuv(f, s.Width, s.Height, (s.Height + 1) / 2, tenBit: true)).ToArray();
            var fast = new SwscaleYuv420();
            IYuv422Converter fast422 = fast;
            var fast10 = (IYuv420P10FrameConverter)((IYuv420P10Converter)fast).ForVideo("bt709", false, "left", s.Width, s.Height)!;
            int[] rows = resizer.SourceRows, all = Enumerable.Range(0, s.Width).ToArray();
            foreach (var (what, cols) in new[] { ("sampled", resizer.SourceCols), ("rows", all) })
            {
                if (what == "rows" && resizer.SourceCols.Length == s.Width)
                    continue; // the resize reads every column: the same case
                yield return new Case(s.Set, $"fastyuv {s.Name} {what} {rows.Length}x{cols.Length}", yuv, rows.Length * stride, (f, dst) =>
                {
                    for (int i = 0; i < rows.Length; i++)
                        fast.RowToBgr(f, s.Width, s.Height, rows[i], cols, dst.AsSpan(i * stride, stride));
                });
                yield return new Case(s.Set, $"fastyuv422 {s.Name} {what} {rows.Length}x{cols.Length}", yuv422, rows.Length * stride, (f, dst) =>
                {
                    for (int i = 0; i < rows.Length; i++)
                        fast422.RowToBgr(f, s.Width, s.Height, rows[i], cols, dst.AsSpan(i * stride, stride));
                });
                yield return new Case(s.Set, $"fastyuv10 {s.Name} {what} {rows.Length}x{cols.Length}", yuv10, rows.Length * cols.Length * 3, (f, dst) =>
                    fast10.FrameToBgr(f, s.Width, s.Height, rows, cols, dst));
            }
        }

        /// <summary>
        /// A yuv420p, yuv422p (<paramref name="chromaHeight"/> the height) or yuv420p10le frame from a BGR one: Y from all
        /// three channels, U and V from the blue and red of each chroma sample's top left pixel; 10-bit samples are the
        /// 8-bit ones scaled to 0..1023.
        /// </summary>
        static byte[] Yuv(byte[] bgr, int w, int h, int chromaHeight, bool tenBit)
        {
            int cw = (w + 1) / 2, ch = chromaHeight, size = w * h + 2 * cw * ch, step = ch == h ? 1 : 2;
            var yuv = new byte[tenBit ? 2 * size : size];
            void Set(int i, int v)
            {
                if (!tenBit)
                {
                    yuv[i] = (byte)v;
                    return;
                }

                int ten = (v << 2) | (v >> 6);
                yuv[2 * i] = (byte)ten;
                yuv[(2 * i) + 1] = (byte)(ten >> 8);
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    Set(y * w + x, (bgr[i] + 2 * bgr[i + 1] + bgr[i + 2]) >> 2);
                }
            }

            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int i = (Math.Min(step * y, h - 1) * w + Math.Min(2 * x, w - 1)) * 3;
                    Set(w * h + y * cw + x, bgr[i]);
                    Set(w * h + cw * ch + y * cw + x, bgr[i + 2]);
                }
            }

            return yuv;
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
    /// cached next to this program (frames/), not in git. Keyframes decode fast and are whole pictures, not fades; the
    /// Sintel trailer has too few (9 after its titles), so it takes every 18th frame instead.
    /// </summary>
    static class Films
    {
        public static readonly (int W, int H)[] Sizes = [(426, 240), (640, 360), (1280, 534), (1920, 1080)];

        // Name in tools/realworld.py, where to start (past the titles), and every how many frames (0: keyframes).
        static readonly (string Name, int Start, int Every)[] Known =
        [
            ("elephants_dream_426x240_h264.mp4", 60, 0),
            ("bbb_640x360_h264.mp4", 60, 0),
            ("tears_of_steel_1280x534_h264.mov", 60, 0),
            ("sintel_trailer_1920x1080_h264.mp4", 5, 18),
        ];

        static string Ffmpeg(string tool) =>
            Path.Combine(Environment.GetEnvironmentVariable("BENCH_FFMPEG") ?? "", OperatingSystem.IsWindows() ? tool + ".exe" : tool);

        public static IEnumerable<Source> Load(Options o)
        {
            var films = o.Videos.Count > 0
                ? o.Videos.Select(v => (Path: v, Start: 0, Every: 0)).ToList()
                : Known.Select(k => (Path: Path.Combine(o.FilmsDir ?? Path.Combine(RepoRoot(), "samples", "realworld"), k.Name), k.Start, k.Every)).ToList();
            var missing = films.Where(f => !File.Exists(f.Path)).Select(f => Path.GetFileName(f.Path)).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"real set: skipping {string.Join(", ", missing)} (python tools/realworld.py --download {string.Join(' ', missing)}, or --films DIR / --video FILE)");
            }

            foreach (var (path, start, every) in films.Where(f => File.Exists(f.Path)))
            {
                var (w, h) = Size(path);
                string cache = Path.Combine(AppContext.BaseDirectory, "frames", $"{Path.GetFileNameWithoutExtension(path)}-{w}x{h}-{start}s-{(every > 0 ? $"every{every}-" : "")}{o.Frames}.bgr");
                if (!File.Exists(cache))
                    Extract(path, start, every, w, h, o.Frames, cache);
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

        static void Extract(string path, int start, int every, int w, int h, int frames, string cache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            string partial = cache + ".partial";
            string[] args =
            [
                "-v", "error", "-ss", start.ToString(CultureInfo.InvariantCulture), .. every > 0 ? Array.Empty<string>() : new[] { "-skip_frame", "nokey" },
                "-i", path, "-an", "-sn",
                "-vf", (every > 0 ? $"select=not(mod(n\\,{every}))," : "") + $"scale={w}:{h}:flags=bicubic,format=bgr24", "-fps_mode", "passthrough",
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
