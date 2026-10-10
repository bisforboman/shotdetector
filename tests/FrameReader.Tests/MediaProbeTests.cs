using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FrameReader;

/// <summary>
/// MediaProbe's values against ffprobe's for the same file. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared
/// build's folder, which also has ffmpeg and ffprobe); without it these do nothing.
/// </summary>
public class MediaProbeTests
{
    static readonly string? Libs = TestLibraries.Load();

    static string Tool(string name) => Path.Combine(Libs!, OperatingSystem.IsWindows() ? name + ".exe" : name);

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Tool(tool)) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    [Theory]
    [InlineData("plain.mp4", "-c:v mpeg4", "")]
    [InlineData("rotated.mp4", "-c:v mpeg4", "-display_rotation 90")]
    [InlineData("interlaced.mkv", "-c:v mpeg2video -flags +ilme+ildct -top 1 -c:a aac", "")]
    [InlineData("tagged.mkv", "-c:v ffv1 -pix_fmt yuv422p10le -colorspace bt709 -color_range pc", "")]
    public void ValuesAreFfprobes(string name, string encode, string display)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-probe-").FullName;
        try
        {
            string plain = Path.Combine(dir, "source" + Path.GetExtension(name)), path = Path.Combine(dir, name);
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-f", "lavfi", "-i", "sine=d=2",
                .. encode.Split(' '), .. encode.Contains("-c:a") ? Array.Empty<string>() : ["-an"], plain]);
            if (display.Length > 0)
                Run("ffmpeg", ["-v", "error", "-y", .. display.Split(' '), "-i", plain, "-c", "copy", path]);
            else
                File.Move(plain, path);

            var info = MediaProbe.Probe(path, new ProbeOptions { PacketTimestamps = true });
            using var json = JsonDocument.Parse(Run("ffprobe", "-v", "error", "-show_entries",
                "stream=index,codec_type,codec_name,width,height,pix_fmt,color_range,color_space,field_order,r_frame_rate,avg_frame_rate,time_base,start_pts,duration_ts,nb_frames:stream_side_data=rotation:format=format_name,duration",
                "-of", "json", path));
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
            var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
            string? S(string key) => video.TryGetProperty(key, out var v) ? v.ToString() : null;
            var v = info.Video!;
            Assert.Equal(S("index"), v.Index.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(S("codec_name"), v.Codec);
            Assert.Equal((S("width"), S("height")), (v.Width.ToString(CultureInfo.InvariantCulture), v.Height.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(S("pix_fmt"), v.PixelFormat);
            Assert.Equal(S("color_range"), v.ColorRange);
            Assert.Equal(S("color_space"), v.ColorSpace);
            Assert.Equal(S("r_frame_rate"), v.FrameRate.ToString());
            Assert.Equal(S("avg_frame_rate"), v.AverageFrameRate.ToString());
            Assert.Equal(S("time_base"), v.TimeBase.ToString());
            Assert.Equal(S("start_pts"), v.StartPts?.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(S("duration_ts"), v.DurationPts?.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(S("nb_frames"), v.FrameCount?.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(S("field_order") ?? "unknown", v.FieldOrder switch
            {
                FieldOrder.Progressive => "progressive", FieldOrder.TopFirst => "tt", FieldOrder.BottomFirst => "bb",
                FieldOrder.TopCodedBottomFirst => "tb", FieldOrder.BottomCodedTopFirst => "bt", _ => "unknown",
            });
            int? rotation = video.TryGetProperty("side_data_list", out var sd)
                ? sd.EnumerateArray().Where(e => e.TryGetProperty("rotation", out _)).Select(e => (int?)e.GetProperty("rotation").GetInt32()).FirstOrDefault()
                : null;
            Assert.Equal(rotation, v.Rotation is { } r ? (int)r : null);
            var format = json.RootElement.GetProperty("format");
            Assert.Equal(format.GetProperty("format_name").GetString(), info.Container);
            Assert.Equal(format.GetProperty("duration").GetString(), (info.DurationMicroseconds!.Value / 1e6).ToString("F6", CultureInfo.InvariantCulture));
            Assert.Equal(streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"), info.HasAudio);
            var packets = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "csv=p=0", path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(packets, info.PacketTimestamps!.Select(p => p?.ToString(CultureInfo.InvariantCulture) ?? "N/A"));

            // The same bytes as a stream (mkv: headers first): the same headers, read as from a pipe.
            if (path.EndsWith(".mkv"))
            {
                using var stream = File.OpenRead(path);
                var piped = MediaProbe.Probe(stream, new ProbeOptions());
                Assert.Equal((v.Codec, v.Width, v.Height, v.PixelFormat, v.AverageFrameRate, v.FieldOrder), (piped.Video!.Codec, piped.Video.Width, piped.Video.Height, piped.Video.PixelFormat, piped.Video.AverageFrameRate, piped.Video.FieldOrder));
                Assert.Equal(info.Container, piped.Container);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UnreadableInputHasAReason()
    {
        if (Libs is null)
            return;
        string path = Path.Combine(Path.GetTempPath(), $"framereader-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "not a video");
        try
        {
            var e = Assert.Throws<FrameReaderException>(() => MediaProbe.Probe(path, new ProbeOptions()));
            Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
