using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ShotDetector;

/// <summary>
/// The contact sheet (issue #649): save-images' frames in a row per shot, as a PNG (our own encoder and decoder, checked
/// against ffmpeg's) or a PDF (checked for its structure, and rendered with poppler's pdftoppm where it is installed).
/// </summary>
public partial class ContactSheetTests(Clips clips) : IClassFixture<Clips>
{
    static ContactSheet.Rgb Noise(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 3];
        new Random(seed).NextBytes(pixels);
        return new(width, height, pixels);
    }

    static byte[] Ffmpeg(byte[] input, params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["-v", "error", .. args])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
        var err = p.StandardError.ReadToEndAsync();
        p.StandardInput.BaseStream.Write(input);
        p.StandardInput.Close();
        copy.Wait();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return output.ToArray();
    }

    [Fact]
    public void OurPngsDecodeAsWrittenHereAndInFfmpeg()
    {
        var image = Noise(37, 23, 1);
        byte[] png = ContactSheet.EncodePng(image);
        Assert.Equal(image.Pixels, ContactSheet.DecodePng(png).Pixels);
        Assert.Equal(image.Pixels, Ffmpeg(png, "-f", "png_pipe", "-i", "-", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("sub")]
    [InlineData("up")]
    [InlineData("avg")]
    [InlineData("paeth")]
    [InlineData("mixed")]
    public void FfmpegsPngsDecodeWithEveryRowFilter(string pred)
    {
        var image = Noise(41, 19, 2);
        byte[] png = Ffmpeg(image.Pixels, "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "41x19", "-i", "-", "-pred", pred, "-f", "image2pipe", "-c:v", "png", "-");
        var decoded = ContactSheet.DecodePng(png);
        Assert.Equal((41, 19), (decoded.Width, decoded.Height));
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void LabelsAreTheShotsNumberStartAndLength()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        Assert.Equal(["Shot 2", "00:00:02.000", "2.000 s"], ContactSheet.Label(r.Shots[1]));
    }

    [Fact]
    public void EveryLabelCharacterHasAGlyph()
    {
        foreach (char c in "0123456789:.Shots")
        {
            var canvas = new byte[12 * 14 * 3];
            Array.Fill(canvas, (byte)255);
            ContactSheet.DrawText(canvas, 12, 0, 0, c.ToString());
            Assert.True(canvas.Any(b => b != 255), $"'{c}' drew nothing");
        }
    }

    [Fact]
    public void PdfTextIsEscapedWinAnsi()
    {
        Assert.Equal(@"(a\(b\)c\\d)", ContactSheet.PdfText(@"a(b)c\d"));
        Assert.Equal(@"(caf\351 ?)", ContactSheet.PdfText("café ✓"));
    }

    [Fact]
    public void ThePngHoldsSaveImagesFramesInARowPerShot()
    {
        string dir = Directory.CreateTempSubdirectory("sheet-png-").FullName;
        try
        {
            var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
            string sheet = Path.Combine(dir, "sheet.png");
            ContactSheet.Write(r, sheet, new ImageOptions(), 160);
            var image = ContactSheet.DecodePng(File.ReadAllBytes(sheet));

            // 8 + a 160-pixel label column + 3 x (160 + 8) wide; 8 + 3 x (120 + 8) high (320x240 at 160 wide).
            Assert.Equal((8 + 160 + (3 * 168), 8 + (3 * 128)), (image.Width, image.Height));

            // Each thumbnail is the PNG save-images writes at that width, pixel for pixel.
            var files = Export.SaveImages(r, Path.Combine(dir, "images"), new ImageOptions { Width = 160, Format = "png", PixelFormat = "rgb24" });
            Assert.Equal(9, files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                var thumb = ContactSheet.DecodePng(File.ReadAllBytes(files[i]));
                int x0 = 8 + 160 + (i % 3 * 168), y0 = 8 + (i / 3 * 128);
                for (int y = 0; y < thumb.Height; y++)
                {
                    Assert.True(thumb.Pixels.AsSpan(y * thumb.Width * 3, thumb.Width * 3)
                        .SequenceEqual(image.Pixels.AsSpan((((y0 + y) * image.Width) + x0) * 3, thumb.Width * 3)),
                        $"image {i}, row {y}");
                }
            }

            // The labels are drawn in the label column, and the gaps stay white.
            Assert.Contains(image.Pixels.AsSpan(((16 * image.Width) + 16) * 3, 140 * 3).ToArray(), b => b < 255);
            Assert.True(image.Pixels.AsSpan(132 * image.Width * 3, image.Width * 3).ToArray().All(b => b == 255), "the gap under the first row");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [GeneratedRegex(@"(?<id>\d+) 0 obj\n")]
    private static partial Regex ObjectStart();

    [Fact]
    public void ThePdfEmbedsSaveImagesJpegsAsTheyAre()
    {
        string dir = Directory.CreateTempSubdirectory("sheet-pdf-").FullName;
        try
        {
            var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
            string sheet = Path.Combine(dir, "sheet.pdf");
            ContactSheet.Write(r, sheet, new ImageOptions { NumImages = 2 }, 160);
            byte[] pdf = File.ReadAllBytes(sheet);
            string text = Encoding.Latin1.GetString(pdf);
            Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
            Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

            // The cross-reference table points at each object.
            int xref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith("xref\n", text[xref..], StringComparison.Ordinal);
            var entries = Regex.Matches(text[xref..], @"(\d{10}) 00000 n ").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(ObjectStart().Matches(text).Count, entries.Count);
            for (int id = 1; id <= entries.Count; id++)
                Assert.StartsWith($"{id} 0 obj\n", text[entries[id - 1]..], StringComparison.Ordinal);

            // The images are save-images' JPEGs at that width, byte for byte, in order.
            var files = Export.SaveImages(r, Path.Combine(dir, "images"), new ImageOptions { Width = 160, NumImages = 2 });
            int at = 0;
            foreach (string file in files)
            {
                byte[] jpeg = File.ReadAllBytes(file);
                int found = pdf.AsSpan(at).IndexOf(jpeg);
                Assert.True(found >= 0, $"{file} isn't in the PDF");
                at += found + jpeg.Length;
            }

            Assert.Contains("(Shot 3) Tj", Contents(pdf), StringComparison.Ordinal);
            Assert.Contains("/Count 1 >>", text, StringComparison.Ordinal);
            Pdftoppm(sheet);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LongSheetsTurnThePage()
    {
        // 40 rows of 4:3 images at three a row: about 119 pt each, six to an A4 page.
        byte[] jpeg = Ffmpeg(Noise(64, 48, 3).Pixels, "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "64x48", "-i", "-", "-f", "image2pipe", "-c:v", "mjpeg", "-");
        var rows = Enumerable.Range(1, 40).Select(i => new ContactSheet.Row([$"Shot {i}"], [jpeg, jpeg, jpeg])).ToList();
        string text = Encoding.Latin1.GetString(ContactSheet.Pdf(rows, "long"));
        Assert.Contains("/Count 7 >>", text, StringComparison.Ordinal);
        Assert.Equal(120, Regex.Matches(text, "/Subtype /Image").Count);
    }

    [Fact]
    public void ThePngOrPdfIsChosenByTheExtension()
    {
        var r = ShotDetection.Detect(clips.ThreeShots, new() { Detector = DetectorKind.Content });
        Assert.Throws<ArgumentException>(() => ContactSheet.Write(r, "sheet.jpg", new ImageOptions(), 160));
    }

    /// <summary>The PDF's page contents (its FlateDecode streams), inflated and joined.</summary>
    static string Contents(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(text, @"<< /Filter /FlateDecode /Length (\d+) >>\nstream\n"))
        {
            int start = m.Index + m.Length, length = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, length), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(z, Encoding.Latin1);
            sb.Append(reader.ReadToEnd());
        }

        return sb.ToString();
    }

    /// <summary>Renders the PDF with poppler's pdftoppm, when it is installed: it reports a broken file as an error.</summary>
    static void Pdftoppm(string pdf)
    {
        ProcessStartInfo psi = new("pdftoppm") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in (string[])["-r", "20", "-png", pdf, Path.Combine(Path.GetDirectoryName(pdf)!, "page")])
            psi.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        using (p)
        {
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0 && err.Length == 0, err);
        }
    }
}
