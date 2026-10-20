using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using ShotDetector;

/// <summary>The shotdetect CLI, run as a process (built with the tests, like ClipMaker).</summary>
public class CliTests(Clips clips) : IClassFixture<Clips>
{
    static (int ExitCode, string Out, string Err) Run(params string[] args)
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string tests = bin.Parent!.Parent!.Parent!.Parent!.FullName;
        string cli = Path.Combine(Path.GetDirectoryName(tests)!, "src", "ShotDetector.Cli", "bin", bin.Parent.Name, bin.Name, "shotdetect.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(cli);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.Result);
    }

    // A warning goes to stderr as soon as it is known (--parallel on a short clip: before the first frame, whichever
    // decoder reads it), so before the summary line printed after detection, and only once.
    [Fact]
    public void WarningsArePrintedOnceAsTheyHappen()
    {
        var (exitCode, output, err) = Run("-i", clips.ThreeShots, "--parallel");
        Assert.True(exitCode == 0, err);
        var warning = Assert.Single(Regex.Matches(err, "^Warning: .*$", RegexOptions.Multiline));
        Assert.StartsWith("Warning: read sequentially, not in chunks: ", warning.Value);
        int summary = err.IndexOf(" frames, processed at ", StringComparison.Ordinal);
        Assert.True(summary > warning.Index, err);
        Assert.DoesNotContain("Warning", output, StringComparison.Ordinal);
        Assert.Contains("Scene", output, StringComparison.Ordinal);
    }

    // --version prints the package's version (the library's: both take ShotDetectorVersion), without the "+commit".
    [Fact]
    public void VersionPrintsTheVersion()
    {
        var (exitCode, output, err) = Run("--version");
        Assert.True(exitCode == 0, err);
        string expected = typeof(ShotDetection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(expected, output.TrimEnd());
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", output.TrimEnd());
        Assert.Empty(err);
    }
}
