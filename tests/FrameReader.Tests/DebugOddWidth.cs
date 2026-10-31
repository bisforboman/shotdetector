using System.Diagnostics;
using FrameReader;

public class DebugOddWidth
{
    [Fact]
    public void LastPixel()
    {
        string? libs = TestLibraries.Load();
        if (libs is null)
            return;
        string ffmpeg = Environment.GetEnvironmentVariable("DEBUG_FFMPEG") ?? Path.Combine(libs, "ffmpeg");
        string dir = Directory.CreateTempSubdirectory("dbg-").FullName;
        string path = Path.Combine(dir, "odd.nut");
        Run(ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.12,scale=321:240", "-c:v", "rawvideo", "-pix_fmt", "yuv420p", path);
        using var reader = new VideoFrameReader(path, new VideoFrameReaderOptions { FrameFormat = FrameFormat.Bgr24, Decoder = new FrameDecoderOptions() });
        Assert.True(reader.TryRead(out var frame));
        var ours = frame.Data.ToArray();
        string align = Environment.GetEnvironmentVariable("VFR_ALIGN") ?? "0";
        foreach (var extra in new[] { "", "-threads 1 -filter_threads 1", "-sws_flags bicubic+accurate_rnd", "-cpuflags 0" })
        {
            var cli = Run(ffmpeg, ["-v", "error", .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries), "-i", path, "-vf", "scale=iw:ih:flags=bicubic,format=bgr24", "-frames:v", "1", "-f", "rawvideo", "-"]);
            var cols = new SortedDictionary<int, int>();
            int n = 0;
            string first = "";
            for (int i = 0; i < Math.Min(cli.Length, ours.Length); i++)
            {
                if (cli[i] != ours[i])
                {
                    if (n++ == 0)
                        first = $"at {i} (row {i / 963}, col {i % 963 / 3}, channel {i % 3}): cli {cli[i]} ours {ours[i]}";
                    int c = i % 963 / 3;
                    cols[c] = cols.GetValueOrDefault(c) + 1;
                }
            }

            Console.WriteLine($"DEBUG align={align} vs cli [{extra}]: {n} bytes differ {first}; columns {string.Join(",", cols.Select(kv => $"{kv.Key}x{kv.Value}"))}; max diff {(n == 0 ? 0 : Enumerable.Range(0, Math.Min(cli.Length, ours.Length)).Max(i => Math.Abs(cli[i] - ours[i])))}");
        }
        Console.WriteLine($"DEBUG cpu: {File.ReadAllLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("Features", StringComparison.Ordinal))}");
    }

    static string Px(byte[] b, int i) => $"{b[i * 3]},{b[i * 3 + 1]},{b[i * 3 + 2]}";

    static byte[] Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return output.ToArray();
    }
}
