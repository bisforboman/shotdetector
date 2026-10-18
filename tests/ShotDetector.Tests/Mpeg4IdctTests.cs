using System.Text;
using ShotDetector;

/// <summary>
/// MPEG-4 Part 2 decodes with FFmpeg's C IDCT (as x86 does, so ARM gives x86's frames) unless FFmpeg's decoder takes the
/// Xvid IDCT, by its own rule (libavcodec/mpeg4videodec.c: decode_user_data, ff_mpeg4_workaround_bugs).
/// </summary>
public class Mpeg4IdctTests(Clips clips) : IClassFixture<Clips>
{
    /// <summary>A VOS (simple profile), visual object and VOL start code, then user data, then a VOP start code.</summary>
    static byte[] Header(string? userData, byte profileAndLevel = 0x03, string? afterVop = null)
    {
        var b = new List<byte> { 0, 0, 1, 0xB0, profileAndLevel, 0, 0, 1, 0xB5, 0x09, 0, 0, 1, 0x20, 0x00, 0xBC, 0x04 };
        if (userData is not null)
            b.AddRange([0, 0, 1, 0xB2, .. Encoding.Latin1.GetBytes(userData)]);
        b.AddRange([0, 0, 1, 0xB6, 0x10, 0x60]);
        if (afterVop is not null)
            b.AddRange([0, 0, 1, 0xB2, .. Encoding.Latin1.GetBytes(afterVop)]);
        return [.. b];
    }

    static bool Xvid(string tag, string? userData) => Mpeg4Idct.UsesXvidIdct(tag, [], Header(userData));

    [Theory]
    [InlineData("XVID", "XviD0074", true)] // libxvid's signature
    [InlineData("FMP4", "XviD0074", true)] // whatever the FourCC
    [InlineData("[0][0][0][0]", "XviD74", true)]
    [InlineData("FMP4", "XviD 12", true)] // %d skips white space
    [InlineData("FMP4", "XviD", false)] // no digits: no build
    [InlineData("XVID", "XviD", true)] // ... so no signature, and the FourCC says Xvid
    [InlineData("FMP4", "XviD-5", false)] // a build below 0
    [InlineData("XVID", "XviD-1", true)] // -1 is "none": the FourCC again
    [InlineData("FMP4", "xvid0074", false)] // case matters in the signature
    public void TheXvidSignatureSetsTheBuild(string tag, string userData, bool expected) =>
        Assert.Equal(expected, Xvid(tag, userData));

    // No signature: the FourCC, upper-cased as the decoder does (ff_toupper4), says whether it is Xvid.
    [Theory]
    [InlineData("XVID", true)]
    [InlineData("xvid", true)]
    [InlineData("XviX", true)]
    [InlineData("rmp4", true)]
    [InlineData("ZMP4", true)]
    [InlineData("SIPP", true)]
    [InlineData("FMP4", false)]
    [InlineData("DX50", false)]
    [InlineData("DIVX", false)]
    [InlineData("mp4v", false)]
    [InlineData("[0][0][0][0]", false)]
    public void WithoutASignatureTheFourCcDecides(string tag, bool expected)
    {
        Assert.Equal(expected, Xvid(tag, null));
        Assert.Equal(expected, Xvid(tag, "some encoder"));
    }

    // Any other encoder's signature means it isn't Xvid, whatever the FourCC (ffmpeg -vtag xvid on FFmpeg's encoder).
    [Theory]
    [InlineData("Lavc62.28.103")]
    [InlineData("Lavc51.40.4")]
    [InlineData("FFmpeg0.4.9-pre1b4718")] // FFmpe%*[^b]b%d
    [InlineData("FFmpeg v0.4.6 / libavcodec build: 4641")]
    [InlineData("ffmpeg")]
    [InlineData("DivX503b1393p")]
    [InlineData("DivX501Build1361")]
    [InlineData("DivX5b0")]
    public void AnotherEncodersSignatureOverridesAnXvidFourCc(string userData) =>
        Assert.False(Xvid("XVID", userData));

    // Signatures that the decoder's sscanf patterns don't match leave the FourCC to decide.
    [Theory]
    [InlineData("Lavc62")] // Lavc%d.%d.%d needs all three numbers
    [InlineData("Lavc62.28")]
    [InlineData("ffmpeg ")] // exactly "ffmpeg"
    [InlineData("FFmpegb")] // %*[^b] needs a character before the b, and %d a number after it
    [InlineData("DivX5")] // DivX%db%d needs the build
    [InlineData("FFmpeXb-1")] // FFmpe, one character, b, -1: a "build" of -1 is none
    public void PartialSignaturesAreNoSignature(string userData) =>
        Assert.True(Xvid("XVID", userData));

    // The signature is a C string: it ends at a NUL, and at the next start code or zero run (at most 255 bytes).
    [Fact]
    public void UserDataEndsWhereTheDecodersDoes()
    {
        Assert.True(Xvid("FMP4", "XviD12\0Lavc62.28.103"));
        Assert.False(Xvid("XVID", "Lavc62.28.103\0XviD12"));

        // Two user data sections: both read, each setting its own field.
        byte[] both = [0, 0, 1, 0xB2, .. Encoding.Latin1.GetBytes("Lavc62.28.103"), 0, 0, 1, 0xB2, .. Encoding.Latin1.GetBytes("XviD9"), 0, 0, 1, 0xB6];
        Assert.True(Mpeg4Idct.UsesXvidIdct("FMP4", [], both));
    }

    // glibc's %d: a number past long's range is long's limit, then cut to an int (LONG_MAX: -1, which is "none").
    [Fact]
    public void AHugeBuildIsGlibcs()
    {
        Assert.False(Xvid("FMP4", "XviD" + new string('9', 30)));
        Assert.True(Xvid("XVID", "XviD" + new string('9', 30)));
        Assert.True(Xvid("FMP4", "XviD4294967296")); // 2^32: 0 as an int
    }

    // Only the header before the first VOP is read; extradata (mp4, mkv, avi) as well as the first packet.
    [Fact]
    public void SignaturesComeFromTheExtradataAndTheHeaderBeforeTheFirstVop()
    {
        Assert.False(Mpeg4Idct.UsesXvidIdct("FMP4", [], Header(null, afterVop: "XviD0074")));
        Assert.True(Mpeg4Idct.UsesXvidIdct("[0][0][0][0]", Header("XviD0074")[..^6], Header(null)));
        Assert.False(Mpeg4Idct.UsesXvidIdct("XVID", Header("Lavc62.28.103")[..^6], Header(null)));
        Assert.True(Mpeg4Idct.UsesXvidIdct("XVID", [], []));
        Assert.False(Mpeg4Idct.UsesXvidIdct("FMP4", [], []));
    }

    // The simple studio profile (14) at levels 1-8 never takes the Xvid IDCT.
    [Theory]
    [InlineData(0xE1, false)]
    [InlineData(0xE8, false)]
    [InlineData(0xE0, true)]
    [InlineData(0xE9, true)]
    [InlineData(0xF5, true)]
    public void TheStudioProfileHasNoXvidIdct(int profileAndLevel, bool expected) =>
        Assert.Equal(expected, Mpeg4Idct.UsesXvidIdct("FMP4", [], Header("XviD0074", (byte)profileAndLevel)));

    [Theory]
    [InlineData("DivX503b1393p", "DivX%db%d%c", 3, "503 1393 112")]
    [InlineData("DivX503b1393", "DivX%db%d%c", 2, "503 1393")]
    [InlineData("FFmpeg v0.4.6 / libavcodec build: 4641", "FFmpeg v%d.%d.%d / libavcodec build: %d", 4, "0 4 6 4641")]
    [InlineData("FFmpeg v0.4.6/libavcodec build:4641", "FFmpeg v%d.%d.%d / libavcodec build: %d", 4, "0 4 6 4641")]
    [InlineData("FFmpeg0.4.9-pre1b4718", "FFmpe%*[^b]b%d", 1, "4718")]
    [InlineData("FFmpeb", "FFmpe%*[^b]b%d", 0, "")]
    [InlineData("Lavc62.28", "Lavc%d.%d.%d", 2, "62 28")]
    [InlineData("XviD+7x", "XviD%d", 1, "7")]
    [InlineData("XviD -", "XviD%d", 0, "")]
    public void ScanIsCsSscanf(string input, string format, int count, string values)
    {
        Assert.Equal(count, Mpeg4Idct.Scan(input, format, out var v));
        Assert.Equal(values, string.Join(' ', v));
    }

    // MPEG-1/2 and MJPEG always decode with the C IDCT, MPEG-4 Part 2 unless Xvid (or not known), others with their default.
    [Theory]
    [InlineData("mpeg1video", null, "-idct simple")]
    [InlineData("mpeg2video", null, "-idct simple")]
    [InlineData("mjpeg", null, "-idct simple")]
    [InlineData("mpeg4", false, "-idct simple")]
    [InlineData("mpeg4", true, "")]
    [InlineData("mpeg4", null, "")]
    [InlineData("h264", null, "")]
    [InlineData("h264", false, "")]
    public void TheCIdctUnlessXvid(string codec, bool? xvid, string expected) =>
        Assert.Equal(expected, string.Join(' ', VideoReader.IdctOptions(codec, xvid)));

    // ffprobe's hex dumps (-show_data) read back as bytes: the codec tag, the extradata and the first packet.
    [Fact]
    public void FfprobesDumpsReadBack()
    {
        string probed = """
            data=
            00000000: 0000 01b0 0300 0001 b509 0000 0100 0000  ................
            00000010: 0000 01b2 5876 6944 3030 3734 0000 01b6  ....XviD0074....

            codec_tag_string=FMP4
            extradata=
            00000000: 0000 01b2 4c61 7663 3632 2e32 382e 3130  ....Lavc62.28.10
            00000010: 33                                       3

            """;
        Assert.True(VideoReader.XvidIdct(probed)); // the packet's signature comes after the extradata's
        Assert.False(VideoReader.XvidIdct(probed.Replace("5876 6944 3030 3734", "4646 4646 4646 4646")));
        Assert.True(VideoReader.XvidIdct(probed.Replace("\n", "\r\n")));
        Assert.Null(VideoReader.XvidIdct("codec_tag_string=XVID\nextradata=\n\n"));
    }

    // Real clips: FFmpeg's encoder (a Lavc signature) with and without -vtag XVID, and with +bitexact (no signature),
    // in avi, mkv and mp4; through ffprobe and FFmpeg's libraries, from a file and from a Stream's first bytes.
    [Theory]
    [InlineData("avi", "", false)]
    [InlineData("avi", "-vtag XVID", false)]
    [InlineData("avi", "-vtag XVID -flags +bitexact", true)]
    [InlineData("avi", "-vtag xvid -flags +bitexact", true)]
    [InlineData("avi", "-vtag FMP4 -flags +bitexact", false)]
    [InlineData("mkv", "-flags +bitexact", false)]
    [InlineData("mp4", "", false)]
    [InlineData("mp4", "-flags +bitexact", false)]
    public void ClipsGetTheDecodersChoice(string container, string args, bool xvid)
    {
        string clip = Path.Combine(Path.GetTempPath(), $"shotdetector-mpeg4-{Guid.NewGuid():N}.{container}");
        VideoReader.Run("ffmpeg", ["-v", "error", "-y", "-i", clips.ThreeShots, "-frames:v", "30", "-c:v", "mpeg4", .. args.Split(' ', StringSplitOptions.RemoveEmptyEntries), clip], default);
        try
        {
            Assert.Equal(xvid, new VideoReader(clip, new() { Decoder = VideoDecoder.FfmpegProcess }).UsesXvidIdct);
            if (container == "mkv")
            {
                using var stream = File.OpenRead(clip);
                Assert.Equal(xvid, new VideoReader(stream, new() { Decoder = VideoDecoder.FfmpegProcess }).UsesXvidIdct);
            }

            if (Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS") is { } libs)
            {
                Assert.Equal(xvid, new VideoReader(clip, new() { FfmpegDirectory = libs, Decoder = VideoDecoder.InProcess }).UsesXvidIdct);
                if (container == "mkv")
                {
                    using var stream = File.OpenRead(clip);
                    Assert.Equal(xvid, new VideoReader(stream, new() { FfmpegDirectory = libs, Decoder = VideoDecoder.InProcess }).UsesXvidIdct);
                }
            }

            // The headers alone (ShotDetection.Probe) don't need it, and other codecs never ask.
            Assert.Null(VideoReader.Headers(clip, new() { Decoder = VideoDecoder.FfmpegProcess }, default).UsesXvidIdct);
            Assert.Null(new VideoReader(clips.ThreeShotsMpegPs, new() { Decoder = VideoDecoder.FfmpegProcess }).UsesXvidIdct);
        }
        finally
        {
            File.Delete(clip);
        }
    }
}
