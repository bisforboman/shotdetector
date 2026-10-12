using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FrameReader;

/// <summary>
/// Common Encryption (CENC, AES-CTR) for <see cref="Remux.Copy"/>'s .mp4 and DASH output: FFmpeg's mp4 muxer encrypts
/// (<c>encryption_scheme=cenc-aes-ctr</c>, as ffmpeg's <c>-encryption_key</c>/<c>-encryption_kid</c>); with
/// <see cref="Widevine"/>, the DASH output also says how to get the key.
/// </summary>
public sealed record EncryptionOptions
{
    /// <summary>The key ID: 16 bytes as 32 hex digits (Shaka Packager's and pssh-box.py's key ID).</summary>
    public required string KeyId { get; init; }

    /// <summary>The content key: 16 bytes as 32 hex digits.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// Widevine signalling for DASH (<see cref="RemuxOptions.Container"/> <c>dash</c>): a Widevine <c>pssh</c> box in each
    /// init segment, the box <c>pssh-box.py --widevine-system-id --key-id</c> gives, and in each adaptation set of the MPD
    /// a <c>ContentProtection</c> for CENC (with <c>cenc:default_KID</c>) and one for Widevine (with that box as
    /// <c>cenc:pssh</c>), as Shaka Packager writes them.
    /// </summary>
    public bool Widevine { get; init; }

    /// <summary>
    /// With <see cref="Widevine"/>: the WidevinePsshData for the pssh box, as a DRM provider supplies it (with its
    /// <c>provider</c>, <c>content_id</c> and the like), written unchanged: the box
    /// <c>pssh-box.py --widevine-system-id --pssh-data</c> gives, as Shaka Packager's <c>--pssh</c> writes it. Null
    /// (default): built from <see cref="KeyId"/> alone.
    /// </summary>
    public byte[]? WidevinePsshData { get; init; }
}

/// <summary>The parts of <see cref="EncryptionOptions"/> FFmpeg doesn't write: Widevine's pssh box and the MPD's ContentProtection.</summary>
static class Cenc
{
    static readonly byte[] WidevineSystemId = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

    /// <summary>16 bytes from 32 hex digits, or an <see cref="ArgumentException"/> naming <paramref name="what"/>.</summary>
    internal static byte[] Bytes16(string hex, string what)
    {
        try
        {
            if (hex.Length == 32)
                return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException($"{what} must be 16 bytes as 32 hex digits, not \"{hex}\".", nameof(EncryptionOptions));
    }

    /// <summary>
    /// A version 0 <c>pssh</c> box with Widevine's system ID and the given WidevinePsshData (<c>pssh-box.py --pssh-data</c>),
    /// or else WidevinePsshData holding the key ID (field 2, bytes), as <c>pssh-box.py --widevine-system-id --key-id</c>
    /// writes it.
    /// </summary>
    internal static byte[] WidevinePssh(byte[] keyId, byte[]? psshData = null)
    {
        byte[] data = psshData ?? [0x12, (byte)keyId.Length, .. keyId];
        var box = new byte[32 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        "pssh"u8.CopyTo(box.AsSpan(4));

        // version 0, flags 0 (bytes 8-11), the system ID, then the data's size and the data
        WidevineSystemId.CopyTo(box, 12);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(28), (uint)data.Length);
        data.CopyTo(box, 32);
        return box;
    }

    /// <summary>The key ID as a UUID string, as <c>cenc:default_KID</c> takes it.</summary>
    static string Uuid(byte[] keyId)
    {
        string h = Convert.ToHexStringLower(keyId);
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    /// <summary>Adds Widevine's pssh box to each init segment the MPD names and the ContentProtection elements to the MPD.</summary>
    internal static void SignalWidevine(string mpd, byte[] keyId, byte[]? psshData)
    {
        byte[] pssh = WidevinePssh(keyId, psshData);
        string text = File.ReadAllText(mpd);
        string dir = Path.GetDirectoryName(Path.GetFullPath(mpd))!;

        foreach (string init in InitSegments(text))
            AddToMoov(Path.Combine(dir, init), pssh);

        if (!text.Contains("xmlns:cenc=", StringComparison.Ordinal))
            text = Regex.Replace(text, "<MPD\\b", "<MPD xmlns:cenc=\"urn:mpeg:cenc:2013\"", RegexOptions.None, TimeSpan.FromSeconds(1));
        string base64 = Convert.ToBase64String(pssh);
        text = Regex.Replace(text,
            "^([ \\t]*)(<AdaptationSet\\b[^>]*>)",
            m =>
            {
                string inner = m.Groups[1].Value + "\t";
                return m.Value
                    + "\n" + inner + $"<ContentProtection value=\"cenc\" schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\" cenc:default_KID=\"{Uuid(keyId)}\"/>"
                    + "\n" + inner + "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\">"
                    + "\n" + inner + "\t<cenc:pssh>" + base64 + "</cenc:pssh>"
                    + "\n" + inner + "</ContentProtection>";
            },
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));
        File.WriteAllText(mpd, text, new UTF8Encoding(false));
    }

    /// <summary>Each representation's init segment, from its SegmentTemplate's <c>initialization</c>.</summary>
    static IEnumerable<string> InitSegments(string mpd)
    {
        var timeout = TimeSpan.FromSeconds(1);
        foreach (Match rep in Regex.Matches(mpd, "<Representation\\b[^>]*>.*?</Representation>", RegexOptions.Singleline, timeout))
        {
            string id = Regex.Match(rep.Value, "\\bid=\"([^\"]*)\"", RegexOptions.None, timeout).Groups[1].Value;
            string bandwidth = Regex.Match(rep.Value, "\\bbandwidth=\"([^\"]*)\"", RegexOptions.None, timeout).Groups[1].Value;
            var init = Regex.Match(rep.Value, "\\binitialization=\"([^\"]*)\"", RegexOptions.None, timeout);
            if (!init.Success)
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"Representation {id} in the MPD has no initialization segment to put the pssh box in.");
            string name = init.Groups[1].Value.Replace("$RepresentationID$", id, StringComparison.Ordinal).Replace("$Bandwidth$", bandwidth, StringComparison.Ordinal);
            if (name.Contains('$'))
                throw new FrameReaderException(FrameReaderError.InvalidInput, $"The init segment name \"{name}\" has a template part other than $RepresentationID$ and $Bandwidth$.");
            yield return name;
        }
    }

    /// <summary>Appends a box to the file's top-level moov (an init segment: nothing after it moves).</summary>
    static void AddToMoov(string path, byte[] box)
    {
        byte[] b = File.ReadAllBytes(path);
        for (int at = 0; at + 8 <= b.Length;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
            if (size < 8 || at + size > b.Length)
                break;
            if (b.AsSpan(at + 4, 4).SequenceEqual("moov"u8))
            {
                int end = at + (int)size;
                byte[] patched = [.. b.AsSpan(0, end), .. box, .. b.AsSpan(end)];
                BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(at), (uint)(size + box.Length));
                File.WriteAllBytes(path, patched);
                return;
            }

            at += (int)size;
        }

        throw new FrameReaderException(FrameReaderError.WriteFailed, $"\"{path}\" has no moov box to put the pssh box in.");
    }

    /// <summary>FFmpeg's mp4 muxer options for CENC, as ffmpeg's <c>-encryption_scheme cenc-aes-ctr -encryption_key K -encryption_kid I</c>.</summary>
    internal static string MuxerOptions(EncryptionOptions e) => string.Create(CultureInfo.InvariantCulture,
        $"encryption_scheme=cenc-aes-ctr:encryption_key={e.Key.ToLowerInvariant()}:encryption_kid={e.KeyId.ToLowerInvariant()}");
}
