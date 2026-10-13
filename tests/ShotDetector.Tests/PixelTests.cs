using System.Security.Cryptography;
using ShotDetector;

/// <summary>
/// Where fixed fixtures and whole videos may miss edge cases: the pixel operations (CvResize, AreaResize, Hsv, the
/// grayscale conversion, EdgeDetector, ContentScorer) on 1,800 generated images against cv2 and scenedetect
/// (Data/pixels.txt, written by tools/pixel_oracle.py: sizes from 1x1 to 720x480, noise, a palette of the extremes,
/// gradients and flat images).
/// </summary>
public class PixelTests
{
    [Fact]
    public void SameAsOpenCv()
    {
        var wrong = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "pixels.txt"))
            .Select(line => line.Split('\t'))
            .Select(c => (Question: c[0], Expected: c[1], Actual: Answer(c[0])))
            .Where(c => c.Actual != c.Expected)
            .Select(c => $"{c.Question}\n  cv2:  {c.Expected}\n  ours: {c.Actual}")
            .ToList();
        Assert.True(wrong.Count == 0, $"{wrong.Count} differ:\n" + string.Join('\n', wrong.Take(10)));
    }

    // What tools/pixel_oracle.py asks cv2 or scenedetect, answered by ShotDetector.
    static string Answer(string question)
    {
        string[] q = question.Split(' ');
        int w = int.Parse(q[1]), h = int.Parse(q[2]);
        switch (q[0])
        {
            case "L":
                {
                    int dw = int.Parse(q[3]), dh = int.Parse(q[4]);
                    var dst = new byte[dw * dh * 3];
                    CvResize.Linear(Image(q, 5, w, h, 3), w, h, dst, dw, dh);
                    return Digest(dst);
                }

            case "A":
                {
                    int dw = int.Parse(q[3]), dh = int.Parse(q[4]);
                    var dst = new byte[dw * dh];
                    AreaResize.Resize(Image(q, 5, w, h, 1), w, h, dst, dw, dh);
                    return Digest(dst);
                }

            case "H":
                {
                    byte[] hsv = new byte[w * h * 3];
                    var planes = hsv.AsSpan();
                    Hsv.Convert(Image(q, 3, w, h, 3), planes[..(w * h)], planes.Slice(w * h, w * h), planes[(2 * w * h)..]);
                    return Digest(hsv);
                }

            case "G":
                {
                    var gray = new byte[w * h];
                    HashDetector.Gray(Image(q, 3, w, h, 3), gray);
                    return Digest(gray);
                }

            case "E":
                {
                    var edges = new byte[w * h];
                    new EdgeDetector(w, h).Detect(Image(q, 3, w, h, 1), edges);
                    return Digest(edges);
                }

            default:
                {
                    var scorer = new ContentScorer(1, 1, 1, 1) { Edges = new EdgeDetector(w, h) };
                    scorer.Score(Image(q, 3, w, h, 3));
                    double score = scorer.Score(Image(q, q[3] is "n" or "p" ? 5 : 7, w, h, 3));
                    return ((ulong)BitConverter.DoubleToInt64Bits(score)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
        }
    }

    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];

    static readonly byte[] Palette = [0, 1, 127, 128, 254, 255];

    /// <summary>The image tools/pixel_oracle.py's image() makes from the spec at q[at..], packed, c channels.</summary>
    static byte[] Image(string[] q, int at, int w, int h, int c)
    {
        var img = new byte[w * h * c];
        switch (q[at])
        {
            case "n" or "p":
                ulong seed = ulong.Parse(q[at + 1]);
                for (int i = 0; i < img.Length; i++)
                {
                    ulong v = Mix(unchecked(seed + ((ulong)i + 1) * 0x9E3779B97F4A7C15));
                    img[i] = q[at] == "n" ? (byte)v : Palette[(int)(v % 6)];
                }

                break;
            case "g":
                int a = int.Parse(q[at + 1]), b = int.Parse(q[at + 2]), k = int.Parse(q[at + 3]);
                for (int y = 0, i = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        for (int ch = 0; ch < c; ch++)
                            img[i++] = (byte)(x * a + y * b + ch * k);
                    }
                }

                break;
            default:
                for (int i = 0; i < img.Length; i++)
                    img[i] = byte.Parse(q[at + 1 + i % c]);
                break;
        }

        return img;
    }

    static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }
}
