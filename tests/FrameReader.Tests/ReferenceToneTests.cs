using System.Diagnostics;
using FrameReader;

/// <summary>
/// ReferenceTone on clips made with ffmpeg's aevalsrc: line-up tone at the start, the end, interrupted, and programme
/// sounds that must not count (a beep, held notes). Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's
/// folder with ffmpeg); without it these do nothing.
/// </summary>
public class ReferenceToneTests
{
    static readonly string? Libs = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");

    const string Tone = "0.1259*sin(2*PI*1000*t)"; // -18 dBFS, EBU line-up
    // Programme stand-in: a chord with harmonics, swelling, over noise.
    const string Content = "(0.2*sin(2*PI*220*t)+0.1*sin(2*PI*440*t)+0.1*sin(2*PI*660*t)+0.08*sin(2*PI*1000*t))*(0.6+0.4*sin(2*PI*0.3*t))+0.05*(random(0)-0.5)";

    static List<ToneSegment> Find(string channels, double seconds, string name = "clip.m4a")
    {
        string dir = Directory.CreateTempSubdirectory("framereader-tone-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
                { RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"aevalsrc={channels}:s=48000:d={seconds}", path })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!)
            {
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, stderr);
            }
            using var audio = new AudioReader(path, new AudioReaderOptions { LibraryDirectory = Libs });
            return [.. ReferenceTone.Find(audio)];
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static void Segment(ToneSegment s, double start, double end)
    {
        Assert.InRange(s.Start.TotalSeconds, start - 0.1, start + 0.1);
        Assert.InRange(s.End.TotalSeconds, end - 0.1, end + 0.1);
        Assert.InRange(s.Level, -18.5, -17.5);
    }

    [Theory]
    [InlineData("clip.m4a")]
    [InlineData("clip.wav")]
    public void LeadingTone(string name)
    {
        if (Libs is null)
            return;
        string ch = $"'if(lt(t,10),{Tone},{Content})'";
        Segment(Assert.Single(Find($"{ch}|{ch}", 20, name)), 0, 10);
    }

    [Fact]
    public void TrailingTone()
    {
        if (Libs is null)
            return;
        string ch = $"'if(lt(t,12),{Content},{Tone})'";
        Segment(Assert.Single(Find($"{ch}|{ch}", 20)), 12, 20);
    }

    [Fact]
    public void ToneAfterSilenceAndBeforeContent()
    {
        if (Libs is null)
            return;
        string ch = $"'if(lt(t,2),0,if(lt(t,32),{Tone},{Content}))'";
        Segment(Assert.Single(Find(ch, 40)), 2, 32);
    }

    [Fact]
    public void InterruptedToneIsOneSegment()
    {
        if (Libs is null)
            return;
        // EBU stereo line-up: the left channel cut for 250 ms every 3 s (from 1 s), the right continuous.
        string left = $"'if(lt(t,20),if(lt(mod(t+2,3),0.25),0,{Tone}),{Content})'", right = $"'if(lt(t,20),{Tone},{Content})'";
        Segment(Assert.Single(Find($"{left}|{right}", 30)), 0, 20);
        // Both channels cut (a mono feed of it): the breaks are bridged.
        Segment(Assert.Single(Find($"{left}|{left}", 30)), 0, 20);
    }

    [Fact]
    public void NoTone()
    {
        if (Libs is null)
            return;
        string ch = $"'{Content}'";
        Assert.Empty(Find($"{ch}|{ch}", 20));
        Assert.Empty(Find("0|0", 10)); // silence
    }

    [Theory]
    [InlineData("0.3*sin(2*PI*1000*t)", 3)] // a beep: too short
    [InlineData("0.3*(sin(2*PI*1000*t)+0.4*sin(2*PI*2000*t)+0.2*sin(2*PI*3000*t))", 8)] // a held note with harmonics
    [InlineData("0.5*exp(-0.3*(t-5))*sin(2*PI*1000*t)", 8)] // a pure note that decays
    public void ProgrammeSoundsAreNotTone(string sound, double length)
    {
        if (Libs is null)
            return;
        string ch = $"'if(between(t,5,{5 + length}),{sound},{Content})'";
        Assert.Empty(Find($"{ch}|{ch}", 20));
    }
}
