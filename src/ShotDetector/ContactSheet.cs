using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ShotDetector;

/// <summary>
/// The CLI's contact sheet (issue #649; docs/decisions/2026-10-31-contact-sheet.md): a row per shot, its number, start
/// timecode and length, then its images, the frames save-images picks (<see cref="Export.SaveImages"/>, which makes
/// them) at a thumbnail width. A PNG (labels in a small built-in bitmap font: our FFmpeg build has no drawtext) or a
/// PDF (A4 pages, Helvetica, the JPEGs embedded as they are), by the file's extension. Not a scenedetect output.
/// </summary>
internal static class ContactSheet
{
    /// <summary>A shot's row: its label's lines and its images' files (PNG for <see cref="Png"/>, JPEG for <see cref="Pdf"/>).</summary>
    internal sealed record Row(IReadOnlyList<string> Label, IReadOnlyList<byte[]> Images);

    /// <summary>A decoded image: 8-bit RGB, rows top to bottom.</summary>
    internal sealed record Rgb(int Width, int Height, byte[] Pixels);

    /// <summary>A shot's label: "Shot N", its start timecode and its length in seconds.</summary>
    internal static string[] Label(Shot shot) =>
        [$"Shot {shot.Number.ToString(CultureInfo.InvariantCulture)}", shot.Start.Timecode(), shot.Duration.SecondsText() + " s"];

    /// <summary>
    /// Writes the contact sheet of <paramref name="result"/> to <paramref name="path"/>, a .png or .pdf file. The images
    /// are save-images' (<paramref name="images"/>'s number per shot and frame margin), <paramref name="width"/> pixels
    /// wide.
    /// </summary>
    internal static void Write(DetectionResult result, string path, ImageOptions images, int width, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".pdf"))
            throw new ArgumentException($"The contact sheet is a .png or a .pdf file, not \"{Path.GetFileName(path)}\".", nameof(path));
        if (width < 16)
            throw new ArgumentException("The contact sheet's thumbnails are 16 pixels wide or more.", nameof(width));
        if (images.NumImages < 1)
            throw new ArgumentException("The contact sheet needs one image per shot or more.", nameof(images));
        var shots = result.Shots;
        if (shots.Count == 0)
            throw new InvalidOperationException("No shots for a contact sheet.");
        bool png = extension == ".png";
        var thumbs = images with
        {
            Width = width,
            Height = null,
            Scale = null,
            Format = png ? "png" : "jpg",
            Quality = png ? null : images.Quality,
            Compression = png ? 1 : null,
            PixelFormat = png ? "rgb24" : null,
            FileName = "$SCENE_NUMBER-$IMAGE_NUMBER",
        };
        string temp = Directory.CreateTempSubdirectory("shotdetector-sheet-").FullName;
        try
        {
            var files = Export.SaveImages(result, temp, thumbs, cancellationToken);
            var rows = files.Chunk(thumbs.NumImages)
                .Select((group, i) => new Row(Label(shots[i]), [.. group.Select(File.ReadAllBytes)]))
                .ToList();
            File.WriteAllBytes(path, png ? Png(rows) : Pdf(rows, Timeline.VideoName(result)));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // ---- PNG ----

    const int Pad = 8, Scale = 2, Advance = (5 + 1) * Scale, LineHeight = (7 + 3) * Scale;

    /// <summary>The label column's width: the widest label line (a timecode, 12 characters) and a margin each side.</summary>
    const int LabelWidth = (12 * Advance) + (2 * Pad);

    /// <summary>
    /// The sheet as a PNG: a white canvas, a row per shot (label, then its images left to right, 8 pixels apart), each
    /// row as tall as its tallest image or label. The images are 8-bit RGB PNGs (<see cref="DecodePng"/>).
    /// </summary>
    internal static byte[] Png(IReadOnlyList<Row> rows)
    {
        var decoded = rows.Select(r => r.Images.Select(DecodePng).ToList()).ToList();
        int width = Pad + LabelWidth + decoded.Max(r => r.Sum(i => i.Width + Pad));
        int[] heights = [.. decoded.Select((r, i) => Math.Max(r.Max(img => img.Height), rows[i].Label.Count * LineHeight))];
        int height = Pad + heights.Sum(h => h + Pad);
        var canvas = new byte[checked(width * height * 3)];
        Array.Fill(canvas, (byte)255);
        int y = Pad;
        for (int r = 0; r < rows.Count; r++)
        {
            for (int line = 0; line < rows[r].Label.Count; line++)
                DrawText(canvas, width, Pad + Pad, y + (line * LineHeight), rows[r].Label[line]);
            int x = Pad + LabelWidth;
            foreach (var image in decoded[r])
            {
                for (int row = 0; row < image.Height; row++)
                    Buffer.BlockCopy(image.Pixels, row * image.Width * 3, canvas, (((y + row) * width) + x) * 3, image.Width * 3);
                x += image.Width + Pad;
            }

            y += heights[r] + Pad;
        }

        return EncodePng(new Rgb(width, height, canvas));
    }

    /// <summary>The 5x7 glyphs of the labels' characters ('#' a pixel), drawn at twice their size.</summary>
    static readonly Dictionary<char, string[]> Glyphs = new()
    {
        [' '] = ["     ", "     ", "     ", "     ", "     ", "     ", "     "],
        ['0'] = [" ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### "],
        ['1'] = ["  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "],
        ['2'] = [" ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####"],
        ['3'] = ["#####", "   # ", "  #  ", "   # ", "    #", "#   #", " ### "],
        ['4'] = ["   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # "],
        ['5'] = ["#####", "#    ", "#### ", "    #", "    #", "#   #", " ### "],
        ['6'] = ["  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### "],
        ['7'] = ["#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   "],
        ['8'] = [" ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### "],
        ['9'] = [" ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  "],
        [':'] = ["     ", " ##  ", " ##  ", "     ", " ##  ", " ##  ", "     "],
        ['.'] = ["     ", "     ", "     ", "     ", "     ", " ##  ", " ##  "],
        ['S'] = [" ####", "#    ", "#    ", " ### ", "    #", "    #", "#### "],
        ['h'] = ["#    ", "#    ", "# ## ", "##  #", "#   #", "#   #", "#   #"],
        ['o'] = ["     ", "     ", " ### ", "#   #", "#   #", "#   #", " ### "],
        ['t'] = [" #   ", " #   ", "###  ", " #   ", " #   ", " #  #", "  ## "],
        ['s'] = ["     ", "     ", " ####", "#    ", " ### ", "    #", "#### "],
    };

    /// <summary>Draws <paramref name="text"/> in dark grey, its top left corner at (x, y); characters without a glyph are left out.</summary>
    internal static void DrawText(byte[] canvas, int width, int x, int y, string text)
    {
        foreach (char c in text)
        {
            if (Glyphs.TryGetValue(c, out var glyph))
            {
                for (int gy = 0; gy < 7; gy++)
                {
                    for (int gx = 0; gx < 5; gx++)
                    {
                        if (glyph[gy][gx] != '#')
                            continue;
                        for (int sy = 0; sy < Scale; sy++)
                        {
                            int at = (((y + (gy * Scale) + sy) * width) + x + (gx * Scale)) * 3;
                            canvas.AsSpan(at, Scale * 3).Fill(32);
                        }
                    }
                }
            }

            x += Advance;
        }
    }

    /// <summary>An 8-bit RGB PNG (colour type 2), each row with the Sub filter, zlib at its best compression.</summary>
    internal static byte[] EncodePng(Rgb image)
    {
        int stride = image.Width * 3;
        var raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int src = y * stride, dst = y * (stride + 1);
            raw[dst] = 1;
            for (int i = 0; i < stride; i++)
                raw[dst + 1 + i] = (byte)(image.Pixels[src + i] - (i >= 3 ? image.Pixels[src + i - 3] : 0));
        }

        using var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, image.Width);
        BigEndian(header, 4, image.Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Decodes an 8-bit RGB, non-interlaced PNG (what ffmpeg's encoder writes after <c>format=rgb24</c>), any row filter.</summary>
    internal static Rgb DecodePng(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G')
            throw new InvalidDataException("Not a PNG file.");
        int pos = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int length = ReadBigEndian(png, pos);
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var body = png.AsSpan(pos + 8, length);
            if (type == "IHDR")
            {
                (width, height) = (ReadBigEndian(png, pos + 8), ReadBigEndian(png, pos + 12));
                if (body[8] != 8 || body[9] != 2 || body[12] != 0)
                    throw new InvalidDataException($"An 8-bit RGB, non-interlaced PNG expected (depth {body[8]}, colour type {body[9]}, interlace {body[12]}).");
            }
            else if (type == "IDAT")
            {
                idat.Write(body);
            }
            else if (type == "IEND")
            {
                break;
            }

            pos += 12 + length;
        }

        int stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);
        var pixels = new byte[stride * height];
        var none = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            var line = raw.AsSpan((y * (stride + 1)) + 1, stride);
            var output = pixels.AsSpan(y * stride, stride);
            var above = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : none;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 3 ? output[i - 3] : 0, b = above[i], c = i >= 3 ? above[i - 3] : 0;
                output[i] = (byte)(line[i] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG row filter {filter}."),
                });
            }
        }

        return new Rgb(width, height, pixels);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void Chunk(Stream png, string type, byte[] body)
    {
        var head = new byte[8];
        BigEndian(head, 0, body.Length);
        Encoding.ASCII.GetBytes(type, head.AsSpan(4));
        png.Write(head);
        png.Write(body);
        uint crc = Crc32(Crc32(0xFFFFFFFF, head.AsSpan(4)), body) ^ 0xFFFFFFFF;
        var tail = new byte[4];
        BigEndian(tail, 0, (int)crc);
        png.Write(tail);
    }

    static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    })];

    /// <summary>PNG's CRC-32 (ISO 3309), continued from <paramref name="crc"/> (start at 0xFFFFFFFF, invert at the end).</summary>
    internal static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    static void BigEndian(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)(value >> 24);
        bytes[at + 1] = (byte)(value >> 16);
        bytes[at + 2] = (byte)(value >> 8);
        bytes[at + 3] = (byte)value;
    }

    static int ReadBigEndian(byte[] bytes, int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];

    // ---- PDF ----

    const double PageWidth = 595.28, PageHeight = 841.89, Margin = 36, LabelColumn = 96, Gap = 6, TitleHeight = 24, FontSize = 9;

    /// <summary>
    /// The sheet as a PDF: A4 portrait pages, the video's name and the page number at the top of each, then as many rows
    /// as fit: the label in Helvetica, the images (JPEG files, embedded as they are) scaled to share the width.
    /// </summary>
    internal static byte[] Pdf(IReadOnlyList<Row> rows, string title)
    {
        int perRow = rows.Max(r => r.Images.Count);
        double imageWidth = (PageWidth - (2 * Margin) - LabelColumn - ((perRow - 1) * Gap)) / perRow;
        var sizes = rows.Select(r => r.Images.Select(JpegSize).ToList()).ToList();
        double[] heights = [.. sizes.Select(r => Math.Max(r.Max(s => imageWidth * s.Height / s.Width), 3.4 * FontSize))];
        var pages = new List<List<int>> { new() };
        double used = 0, room = PageHeight - (2 * Margin) - TitleHeight;
        for (int r = 0; r < rows.Count; r++)
        {
            if (pages[^1].Count > 0 && used + heights[r] > room)
            {
                pages.Add([]);
                used = 0;
            }

            pages[^1].Add(r);
            used += heights[r] + Gap;
        }

        var pdf = new PdfWriter();
        int catalog = pdf.Reserve(), pageTree = pdf.Reserve(), font = pdf.Reserve(), bold = pdf.Reserve();
        pdf.Object(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        pdf.Object(bold, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var pageIds = new List<int>();
        for (int p = 0; p < pages.Count; p++)
        {
            var content = new StringBuilder();
            var images = new StringBuilder();
            content.Append(Invariant($"BT /F2 11 Tf {Margin} {PageHeight - Margin - 11} Td {PdfText(title)} Tj ET\n"));
            string pageText = $"{p + 1} / {pages.Count}";
            content.Append(Invariant($"BT /F1 {FontSize} Tf {PageWidth - Margin - (pageText.Length * 0.556 * FontSize):0.###} {PageHeight - Margin - 11} Td {PdfText(pageText)} Tj ET\n"));
            double top = PageHeight - Margin - TitleHeight;
            foreach (int r in pages[p])
            {
                for (int line = 0; line < rows[r].Label.Count; line++)
                    content.Append(Invariant($"BT /{(line == 0 ? "F2" : "F1")} {FontSize} Tf {Margin} {top - ((line + 1) * FontSize * 1.2):0.###} Td {PdfText(rows[r].Label[line])} Tj ET\n"));
                double x = Margin + LabelColumn;
                for (int i = 0; i < rows[r].Images.Count; i++)
                {
                    var (w, h) = sizes[r][i];
                    string colour = JpegComponents(rows[r].Images[i]) == 1 ? "DeviceGray" : "DeviceRGB";
                    int id = pdf.Stream(Invariant($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /{colour} /BitsPerComponent 8 /Filter /DCTDecode"), rows[r].Images[i]);
                    images.Append(Invariant($"/Im{id} {id} 0 R "));
                    double height = imageWidth * h / w;
                    content.Append(Invariant($"q {imageWidth:0.###} 0 0 {height:0.###} {x:0.###} {top - height:0.###} cm /Im{id} Do Q\n"));
                    x += imageWidth + Gap;
                }

                top -= heights[r] + Gap;
            }

            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(Encoding.ASCII.GetBytes(content.ToString()));
            int contents = pdf.Stream("<< /Filter /FlateDecode", compressed.ToArray());
            int page = pdf.Reserve();
            pdf.Object(page, Invariant($"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R >> /XObject << {images}>> >> /Contents {contents} 0 R >>"));
            pageIds.Add(page);
        }

        pdf.Object(pageTree, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");
        pdf.Object(catalog, $"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        return pdf.Finish(catalog);
    }

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>A PDF string in WinAnsi (Latin-1 for the characters labels and names use; '?' for the rest), escaped.</summary>
    internal static string PdfText(string text)
    {
        var sb = new StringBuilder("(");
        foreach (char c in text)
        {
            char ch = c is (>= ' ' and <= '~') or (>= ' ' and <= 'ÿ') ? c : '?';
            if (ch is '(' or ')' or '\\')
                sb.Append('\\');
            if (ch > '~')
                sb.Append('\\').Append(Convert.ToString(ch, 8));
            else
                sb.Append(ch);
        }

        return sb.Append(')').ToString();
    }

    /// <summary>A JPEG's width and height, from its start-of-frame segment.</summary>
    internal static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        int at = StartOfFrame(jpeg);
        return ((jpeg[at + 7] << 8) | jpeg[at + 8], (jpeg[at + 5] << 8) | jpeg[at + 6]);
    }

    static int JpegComponents(byte[] jpeg) => jpeg[StartOfFrame(jpeg) + 9];

    static int StartOfFrame(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG file.");
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw new InvalidDataException("A JPEG segment expected.");
            byte marker = jpeg[pos + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return pos;
            pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
        }

        throw new InvalidDataException("A JPEG without a start of frame.");
    }

    /// <summary>A PDF 1.4 file's objects, written in order, and its cross-reference table.</summary>
    sealed class PdfWriter
    {
        readonly MemoryStream _file = new();
        readonly Dictionary<int, long> _offsets = [];
        int _count;

        public PdfWriter() => Write("%PDF-1.4\n%âãÏÓ\n");

        public int Reserve() => ++_count;

        public void Object(int id, string body)
        {
            _offsets[id] = _file.Position;
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        public int Stream(string dictionary, byte[] data)
        {
            int id = Reserve();
            _offsets[id] = _file.Position;
            Write($"{id} 0 obj\n{dictionary} /Length {data.Length} >>\nstream\n");
            _file.Write(data);
            Write("\nendstream\nendobj\n");
            return id;
        }

        public byte[] Finish(int root)
        {
            long xref = _file.Position;
            var sb = new StringBuilder($"xref\n0 {_count + 1}\n0000000000 65535 f \n");
            for (int id = 1; id <= _count; id++)
                sb.Append(CultureInfo.InvariantCulture, $"{_offsets[id]:0000000000} 00000 n \n");
            sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(sb.ToString());
            return _file.ToArray();
        }

        void Write(string text) => _file.Write(Encoding.Latin1.GetBytes(text));
    }
}
