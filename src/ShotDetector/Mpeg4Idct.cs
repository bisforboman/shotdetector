namespace ShotDetector;

/// <summary>
/// Whether FFmpeg's MPEG-4 Part 2 decoder (idct=auto) decodes a stream with the Xvid IDCT, by its own rule
/// (libavcodec/mpeg4videodec.c, FFmpeg 8.1: decode_user_data and ff_mpeg4_workaround_bugs). That IDCT is the same
/// code on every platform; every other MPEG-4 stream gets the platform's SIMD IDCT, ARM's rounding differently from
/// x86's, so ShotDetector decodes those with the C IDCT (<see cref="VideoReader.IdctOptions"/>), which x86's matches.
/// </summary>
/// <remarks>
/// The decoder reads the encoder's signature from user data (start code 00 00 01 B2) in the extradata and in each
/// packet before its first VOP; this reads the extradata and the first packet, so a signature that first appears
/// later (the decoder would switch then) isn't seen.
/// </remarks>
internal static class Mpeg4Idct
{
    /// <summary>The FourCCs (upper-cased, as the decoder compares them) taken for Xvid when the stream has no signature.</summary>
    static readonly string[] XvidTags = ["XVID", "XVIX", "RMP4", "ZMP4", "SIPP"];

    /// <param name="codecTag">The stream's FourCC as ffprobe's codec_tag_string (e.g. <c>xvid</c>, <c>FMP4</c>; <c>[0][0][0][0]</c> for none).</param>
    /// <param name="extradata">The codec's extradata (headers), empty if none.</param>
    /// <param name="firstPacket">The stream's first packet.</param>
    internal static bool UsesXvidIdct(string codecTag, ReadOnlySpan<byte> extradata, ReadOnlySpan<byte> firstPacket)
    {
        var s = new Signatures();
        s.Read(extradata);
        s.Read(firstPacket);

        // No signature at all: the FourCC decides (ff_toupper4: ASCII letters upper-cased).
        if (s.XvidBuild == -1 && s.DivxVersion == -1 && s.LavcBuild == -1 && Array.IndexOf(XvidTags, codecTag.ToUpperInvariant()) >= 0)
            s.XvidBuild = 0;
        return s.XvidBuild >= 0 && !s.Studio;
    }

    /// <summary>What the decoder's header parsing keeps: -1 until a signature sets it, exactly as there.</summary>
    sealed class Signatures
    {
        public int XvidBuild = -1;
        public int DivxVersion = -1;
        public int LavcBuild = -1;
        public bool Studio;

        /// <summary>The start codes in <paramref name="data"/> up to the first VOP, as ff_mpeg4_parse_picture_header goes through them.</summary>
        public void Read(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                    continue;
                byte code = data[i + 3];
                int body = i + 4;
                if (code == 0xB6)
                    return; // the VOP: the header ends
                if (code == 0xB2)
                {
                    UserData(data[body..]);
                }
                else if (code == 0xB0 && body < data.Length)
                {
                    // Visual object sequence: the simple studio profile (14) at levels 1-8 decodes without the Xvid IDCT.
                    int profile = data[body] >> 4, level = data[body] & 15;
                    if (profile == 14 && level is > 0 and < 9)
                        Studio = true;
                }

                i += 3;
            }
        }

        /// <summary>decode_user_data: the text up to the next start code (or zero run), at most 255 bytes, as a C string.</summary>
        void UserData(ReadOnlySpan<byte> data)
        {
            // show_bits(gb, 23) == 0: two zero bytes and a third below 2 (bits past the end read as zeros).
            int n = 0;
            while (n < 255 && n < data.Length && !(At(data, n) == 0 && At(data, n + 1) == 0 && At(data, n + 2) < 2))
                n++;
            var bytes = data[..n];
            int nul = bytes.IndexOf((byte)0);
            string text = System.Text.Encoding.Latin1.GetString(nul >= 0 ? bytes[..nul] : bytes);

            // DivX.
            int e = Scan(text, "DivX%dBuild%d%c", out var v);
            if (e < 2)
                e = Scan(text, "DivX%db%d%c", out v);
            if (e >= 2)
                DivxVersion = v[0];

            // libavcodec.
            e = Scan(text, "FFmpe%*[^b]b%d", out v) + 3;
            int build = e == 4 ? v[0] : 0;
            if (e != 4)
            {
                e = Scan(text, "FFmpeg v%d.%d.%d / libavcodec build: %d", out v);
                if (e == 4)
                    build = v[3];
            }

            if (e != 4)
            {
                e = Scan(text, "Lavc%d.%d.%d", out v) + 1;
                if (e == 4)
                    build = ((v[0] & 0xFF) << 16) + ((v[1] & 0xFF) << 8) + (v[2] & 0xFF);
            }

            if (e != 4 && text == "ffmpeg")
                LavcBuild = 4600;
            if (e == 4)
                LavcBuild = build;

            // Xvid.
            if (Scan(text, "XviD%d", out v) == 1)
                XvidBuild = v[0];
        }

        static byte At(ReadOnlySpan<byte> data, int k) => k < data.Length ? data[k] : (byte)0;
    }

    /// <summary>
    /// C's sscanf for the formats above: the number of values assigned (where C would give EOF, -1, this gives 0: the
    /// decoder only compares the counts that mean a match). Literal characters, white space (any amount of it, none
    /// included), <c>%d</c> (leading white space, a sign, digits), <c>%c</c> and <c>%*[^x]</c> (one or more characters
    /// other than x, skipped).
    /// </summary>
    internal static int Scan(string input, string format, out int[] values)
    {
        var result = new List<int>();
        int i = 0;
        for (int f = 0; f < format.Length; f++)
        {
            char fc = format[f];
            if (char.IsWhiteSpace(fc))
            {
                while (i < input.Length && IsCSpace(input[i]))
                    i++;
            }
            else if (fc != '%')
            {
                if (i >= input.Length || input[i] != fc)
                    break;
                i++;
            }
            else if (format[++f] == 'd')
            {
                while (i < input.Length && IsCSpace(input[i]))
                    i++;
                bool negative = i < input.Length && input[i] == '-';
                if (i < input.Length && input[i] is '+' or '-')
                    i++;
                int digits = i;
                Int128 number = 0;
                while (i < input.Length && input[i] is >= '0' and <= '9')
                    number = Int128.Min((number * 10) + (input[i++] - '0'), (Int128)long.MaxValue + 1);
                if (i == digits)
                    break;

                // glibc reads %d as a long (strtol: LONG_MAX or LONG_MIN past its range) and stores the low 32 bits.
                var value = (long)Int128.Clamp(negative ? -number : number, long.MinValue, long.MaxValue);
                result.Add(unchecked((int)value));
            }
            else if (format[f] == 'c')
            {
                if (i >= input.Length)
                    break;
                result.Add(input[i++]);
            }
            else if (format.AsSpan(f).StartsWith("*[^"))
            {
                char stop = format[f + 3];
                f += 4; // the ']'
                int from = i;
                while (i < input.Length && input[i] != stop)
                    i++;
                if (i == from)
                    break;
            }
            else
            {
                throw new FormatException($"Unsupported conversion in \"{format}\".");
            }
        }

        values = [.. result];
        return values.Length;
    }

    // C's isspace in the "C" locale.
    static bool IsCSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}
