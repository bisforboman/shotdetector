using ShotDetector;

// Expected values from cv2 (cvtColor BGR2GRAY, resize INTER_AREA, dct) on a deterministic image.
public class HashDetectorTests
{
    /// <summary>A pseudo-random BGR image from a 31-bit LCG, reproducible in the reference Python.</summary>
    static byte[] LcgImage(int width, int height, long seed = 12345)
    {
        var img = new byte[width * height * 3];
        long v = seed;
        for (int i = 0; i < img.Length; i++)
        {
            v = (v * 1103515245 + 12345) & 0x7fffffff;
            img[i] = (byte)((v >> 16) & 255);
        }
        return img;
    }

    static byte[] Gray(byte[] bgr)
    {
        var gray = new byte[bgr.Length / 3];
        HashDetector.Gray(bgr, gray);
        return gray;
    }

    [Fact]
    public void GrayMatchesOpenCv()
    {
        var gray = Gray(LcgImage(64, 48));
        Assert.Equal([58, 89, 121, 135, 65, 116, 114, 134, 57, 38, 82, 120], gray.Take(12).Select(b => (int)b));
        Assert.Equal(394805, gray.Sum(b => (int)b));
    }

    [Fact]
    public void AreaResizeFastPathMatchesOpenCv()
    {
        var dst = new byte[4 * 4];
        AreaResize.Resize(Gray(LcgImage(64, 48)), 64, 48, dst, 4, 4); // 16x12 blocks
        Assert.Equal([132, 130, 127, 126, 124, 129, 129, 126, 133, 132, 132, 127, 127, 134, 120, 128], dst.Select(b => (int)b));
    }

    [Fact]
    public void AreaResizeGeneralPathMatchesOpenCv()
    {
        var gray = Gray(LcgImage(64, 48));
        var cropped = new byte[60 * 48]; // the left 60 columns
        for (int y = 0; y < 48; y++)
            Array.Copy(gray, y * 64, cropped, y * 60, 60);
        var dst = new byte[7 * 5];
        AreaResize.Resize(cropped, 60, 48, dst, 7, 5);
        Assert.Equal([124, 143, 132, 135, 121, 124, 121, 119, 131, 131, 130, 120, 124, 127, 126, 132, 130, 124, 127,
            135, 127, 129, 134, 132, 138, 130, 130, 128, 126, 127, 133, 129, 122, 126, 123], dst.Select(b => (int)b));
    }

    [Fact]
    public void HashMatchesOpenCv()
    {
        var d = new HashDetector(i => FrameTime.Frame(i, new Fps(25, 1)));
        d.SetFrameSize(64, 48);
        var bits = d.Hash(LcgImage(64, 48));
        string hex = string.Concat(bits.Chunk(8).Select(c => Convert.ToByte(string.Concat(c.Select(b => b ? '1' : '0')), 2).ToString("x2")));
        Assert.Equal("c0cb227a64a1d55e57d086e006bea74926d19aa3f74d90e20f859dd693b76ce5", hex);
        Assert.Equal(128, bits.Count(b => b));
    }

    [Fact]
    public void CutsWhenTheHashChanges()
    {
        var d = new HashDetector(i => FrameTime.Frame(i, new Fps(25, 1)), minSceneLen: 2);
        d.SetFrameSize(64, 48);
        var a = LcgImage(64, 48);
        var b = LcgImage(64, 48, seed: 777);
        var cuts = new List<long>();
        for (int i = 0; i < 10; i++)
            if (d.ProcessFrame(i, i < 5 ? a : b) is { } cut) cuts.Add(cut.FrameNum);
        Assert.Equal([5], cuts);
    }
}
