using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using FFMpegCore;
using FFMpegCore.Pipes;
using FrameReader;

BenchmarkSwitcher.FromAssembly(typeof(ReadFrames).Assembly).Run(args);

/// <summary>
/// Reading the first <see cref="Frames"/> frames of a video three ways, each giving the same bytes: FrameReader
/// in-process, FFMpegCore's pipe (an ffmpeg process, rawvideo to a stream), and the ffmpeg executable's stdout read
/// directly. Two shapes: whole frames as Bgr24, and 160x90 Gray8 (the analysis case: scaled before it reaches .NET).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class ReadFrames
{
    const int Frames = 500;

    sealed class Config : ManualConfig
    {
        // Each operation decodes hundreds of frames (seconds): a few iterations, one invocation each.
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(5).WithInvocationCount(1).WithUnrollFactor(1));
    }

    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
    static readonly string FfmpegDir = Environment.GetEnvironmentVariable("BENCH_FFMPEG") ?? "";

    public static IEnumerable<string> Videos() =>
        Environment.GetEnvironmentVariable("BENCH_VIDEOS")?.Split(';', StringSplitOptions.RemoveEmptyEntries)
        ?? ["samples/realworld/sintel_trailer_1920x1080_h264.mp4", "samples/realworld/tears_of_steel_1280x534_h264.mov", "samples/realworld/bbb_640x360_h264.mp4"];

    [ParamsSource(nameof(Videos))]
    public string Video { get; set; } = "";

    [Params("Bgr24", "Gray8-160x90")]
    public string Shape { get; set; } = "";

    bool Small => Shape != "Bgr24";

    string Filter => Small ? "scale=160:90:flags=bicubic,format=gray" : "scale=iw:ih:flags=bicubic,format=bgr24";

    string Ffmpeg => Path.Combine(FfmpegDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    [GlobalSetup]
    public void Setup()
    {
        if (FfmpegDir.Length > 0)
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = FfmpegDir });
        FFmpegLibraries.Load(Libs);
    }

    [Benchmark(Baseline = true)]
    public long FrameReader()
    {
        using var reader = new VideoFrameReader(Video, new VideoFrameReaderOptions
        {
            Width = Small ? 160 : null, Height = Small ? 90 : null,
            FrameFormat = Small ? FrameFormat.Gray8 : FrameFormat.Bgr24,
        });
        long sum = 0;
        for (int n = 0; n < Frames && reader.TryRead(out var frame); n++)
            sum += frame.Data[frame.Data.Length / 2];
        return sum;
    }

    [Benchmark]
    public long FFMpegCorePipe()
    {
        var sink = new CountingStream();
        FFMpegArguments.FromFileInput(Video)
            .OutputToPipe(new StreamPipeSink(sink), o => o.WithCustomArgument($"-frames:v {Frames} -vf {Filter}").ForceFormat("rawvideo"))
            .ProcessSynchronously();
        return sink.Bytes;
    }

    [Benchmark]
    public long FfmpegExecutable()
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = false };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", Video, "-frames:v", $"{Frames}", "-vf", Filter, "-f", "rawvideo", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var buffer = new byte[1 << 20];
        long bytes = 0;
        int n;
        while ((n = p.StandardOutput.BaseStream.Read(buffer)) > 0)
            bytes += n;
        p.WaitForExit();
        return bytes;
    }

    /// <summary>Takes what's written and keeps only its length.</summary>
    sealed class CountingStream : Stream
    {
        public long Bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Bytes += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Bytes += buffer.Length;
    }
}
