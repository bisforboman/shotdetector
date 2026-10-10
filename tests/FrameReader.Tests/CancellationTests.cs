using System.Diagnostics;
using FrameReader;

/// <summary>
/// The long one-call operations stop when asked: <see cref="Remux.Copy"/>, <see cref="WaveformData.Read"/> and
/// <see cref="VideoFrameReader.ReadAt"/> with a cancelled token. Needs SHOTDETECTOR_FFMPEG_LIBS; without it, nothing.
/// </summary>
public class CancellationTests
{
    static readonly string? Libs = TestLibraries.Load();

    [Fact]
    public void LongOperationsStopWhenCancelled()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-cancel-").FullName;
        try
        {
            string path = Path.Combine(dir, "clip.mkv");
            var psi = new ProcessStartInfo(Path.Combine(Libs, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
                { RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x90:d=2", "-f", "lavfi", "-i", "sine=d=2",
                         "-c:v", "mpeg4", "-c:a", "flac", path })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!)
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            var cancelled = new CancellationToken(canceled: true);

            Assert.ThrowsAny<OperationCanceledException>(() => Remux.Copy(path, Path.Combine(dir, "out.mkv"), null, cancelled));
            using (var audio = new AudioReader(path))
                Assert.ThrowsAny<OperationCanceledException>(() => WaveformData.Read(audio, null, cancelled));
            Assert.ThrowsAny<OperationCanceledException>(() =>
                VideoFrameReader.ReadAt(path, [TimeSpan.Zero, TimeSpan.FromSeconds(1)], null, 1, (_, _) => { }, cancelled));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
