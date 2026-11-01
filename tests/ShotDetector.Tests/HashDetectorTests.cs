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
            135, 127, 129, 134, 132, 138, 130, 130, 128, 126, 127, 133, 129, 122, 126, 123],
            dst.Select(b => (int)b));
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
        {
            if (d.ProcessFrame(i, i < 5 ? a : b) is { } cut)
                cuts.Add(cut.FrameNum);
        }

        Assert.Equal([5], cuts);
    }

    static string Hex(bool[] bits) =>
        string.Concat(bits.Chunk(8).Select(c => Convert.ToByte(string.Concat(c.Select(b => b ? '1' : '0')), 2).ToString("x2")));

    /// <summary>
    /// A flat frame: every DCT coefficient but the first is rounding noise (~1e-31) around a median near zero, so the
    /// cosine table's last bits and the median's float32 rounding decide the bits. These are x64's; browser-wasm gave
    /// other bits on such frames while the table came from its own Math.Cos (shotdetector-web, Tears of Steel).
    /// </summary>
    [Theory]
    [InlineData(8, "d1c92ed12e2e2ed1", 32)]
    [InlineData(16, "9626294a69d9d6a261d99626962669d969d969d9962669d969d9c62a962669d9", 127)]
    public void FlatFramesHashTheSameEverywhere(int size, string expected, int ones)
    {
        var d = new HashDetector(i => FrameTime.Frame(i, new Fps(25, 1)), size: size);
        d.SetFrameSize(64, 48);
        var bits = d.Hash(Enumerable.Repeat((byte)16, 64 * 48 * 3).ToArray());
        Assert.Equal(expected, Hex(bits));
        Assert.Equal(ones, bits.Count(b => b)); // 127 at 16: two equal noise values make the median
    }

    [Fact]
    public void TheDefaultSizesUseTheCheckedInCosines()
    {
        Assert.Same(HashCosines.For(16, 8), HashDetector.CosineTable(16, 8));
        Assert.Same(HashCosines.For(32, 16), HashDetector.CosineTable(32, 16));
        Assert.Null(HashCosines.For(24, 12));
    }

    /// <summary>The checked-in tables are the formula's values with x64's Math.Cos, bit for bit (other CPUs' may differ).</summary>
    [Fact]
    public void TheCheckedInCosinesAreTheFormulaOnX64()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return;
        foreach (var (n, rows) in new[] { (16, 8), (32, 16) })
        {
            var table = HashCosines.For(n, rows)!;
            var formula = HashDetector.CosineFormula(n, rows);
            Assert.Equal(formula.Length, table.Length);
            for (int k = 0; k < rows; k++)
            {
                for (int x = 0; x < n; x++)
                    Assert.Equal(BitConverter.DoubleToInt64Bits(formula[k, x]), BitConverter.DoubleToInt64Bits(table[k, x]));
            }
        }
    }
}
