using System.Runtime.Intrinsics;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// ShotDetector.FastYuv's 8-bit row conversion (SwscaleYuv420.RowToBgr): its Vector128 blocks give the bytes of its
/// tables, a pixel at a time, for every (Y, U, V) and every colour matrix and range, at any width and for any set of
/// columns, and leave the pixels not asked for untouched; and it reads without range checks, yet refuses a column
/// outside the row.
/// </summary>
public class FastYuvRowTests
{
    const int Side = 4096; // a 4096x4096 frame holds every (Y, U, V) once

    static readonly Lazy<byte[]> EveryTriple = new(() =>
    {
        // Chroma sample c (of 2048 x 2048) holds (U, V) = (c & 255, (c >> 8) & 255), the 64th part c >> 16 of the luma
        // values; its 2x2 pixels the four of them: every (U, V) has its 64 samples' 256 luma values.
        int chroma = Side / 2;
        var yuv = new byte[IYuv420Converter.FrameSize(Side, Side)];
        for (int cy = 0; cy < chroma; cy++)
        {
            for (int cx = 0; cx < chroma; cx++)
            {
                int c = cy * chroma + cx, part = c >> 16;
                yuv[Side * Side + c] = (byte)c;
                yuv[Side * Side + chroma * chroma + c] = (byte)(c >> 8);
                for (int k = 0; k < 4; k++)
                    yuv[(2 * cy + (k >> 1)) * Side + 2 * cx + (k & 1)] = (byte)(part * 4 + k);
            }
        }

        return yuv;
    });

    // Every set of coefficients FastYuv has (bt470bg and smpte170m are unknown's BT.601, bt2020c is bt2020nc's), in
    // both ranges.
    public static TheoryData<string, bool> Colours => new()
    {
        { "unknown", false }, { "unknown", true }, { "bt709", false }, { "bt709", true }, { "fcc", false }, { "fcc", true },
        { "smpte240m", false }, { "smpte240m", true }, { "bt2020nc", false }, { "bt2020nc", true },
    };

    [Theory]
    [MemberData(nameof(Colours))]
    public void BlocksGiveTheTablesBytesForEveryYuv(string colorSpace, bool fullRange)
    {
        var (vector, scalar) = Converters(colorSpace, fullRange);
        byte[] yuv = EveryTriple.Value;
        int[] all = Enumerable.Range(0, Side).ToArray(), most = all.Where(x => x % 7 != 3).ToArray();
        var a = new byte[Side * 3];
        var b = new byte[Side * 3];
        for (int y = 0; y < Side; y++)
        {
            // Every pixel (full blocks), and on every fourth row most of them (partial blocks, masked).
            foreach (int[] cols in y % 4 == 0 ? new[] { all, most } : [all])
            {
                scalar.RowToBgr(yuv, Side, Side, y, cols, a);
                vector.RowToBgr(yuv, Side, Side, y, cols, b);
                if (!a.AsSpan().SequenceEqual(b))
                    Assert.Fail($"row {y} ({cols.Length} columns): the blocks' bytes differ from the tables'");
            }
        }
    }

    // Noise planes at widths around the block size and the tail, with the columns a resize reads, all, random subsets
    // of many densities, and shuffled ones with repeats: the same bytes both ways, nothing written outside the columns.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(47)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(257)]
    [InlineData(426)]
    [InlineData(640)]
    [InlineData(1001)]
    [InlineData(1921)]
    [InlineData(4100)]
    public void SameBytesAndUntouchedPixelsAtAnyWidth(int width)
    {
        var rnd = new Random(width);
        int height = 3 + rnd.Next(4);
        var yuv = new byte[IYuv420Converter.FrameSize(width, height)];
        rnd.NextBytes(yuv);
        foreach (var (colorSpace, fullRange) in new[] { ("unknown", false), ("bt709", true) })
        {
            var (vector, scalar) = Converters(colorSpace, fullRange);
            foreach (int[] cols in ColumnSets(width, rnd))
            {
                for (int y = 0; y < height; y++)
                {
                    // A BGR row with room after it, both filled with a pattern the conversion must keep where it doesn't write.
                    var a = Sentinel(width * 3 + 7);
                    var b = Sentinel(width * 3 + 7);
                    scalar.RowToBgr(yuv, width, height, y, cols, a);
                    vector.RowToBgr(yuv, width, height, y, cols, b);
                    Assert.Equal(a, b);
                    var asked = cols.ToHashSet();
                    var untouched = Sentinel(width * 3 + 7);
                    for (int i = 0; i < b.Length; i++)
                    {
                        if (i >= width * 3 || !asked.Contains(i / 3))
                            Assert.True(b[i] == untouched[i], $"width {width}, row {y}: byte {i} isn't a column's but changed");
                    }
                }
            }
        }
    }

    // One converter for every row of every frame takes the plan it made for the first row's columns; columns changed
    // in place, other columns and another width each get their own.
    [Fact]
    public void ColumnsChangedInPlaceOrAnotherWidthGetTheirOwnPlan()
    {
        const int Width = 96, Height = 4;
        var yuv = new byte[IYuv420Converter.FrameSize(Width + 32, Height)];
        new Random(7).NextBytes(yuv);
        var (vector, scalar) = Converters("bt709", false);
        int[] cols = Enumerable.Range(0, Width).ToArray();
        void Check(int width, int[] columns)
        {
            var a = Sentinel(width * 3);
            var b = Sentinel(width * 3);
            scalar.RowToBgr(yuv, width, Height, 1, columns, a);
            vector.RowToBgr(yuv, width, Height, 1, columns, b);
            Assert.Equal(a, b);
        }

        Check(Width, cols);
        for (int i = 0; i < cols.Length; i += 3)
            cols[i] = Width - 1 - cols[i]; // same array, other columns
        Check(Width, cols);
        Check(Width + 32, cols); // same columns, another width (other planes' offsets)
        Check(Width, [.. Enumerable.Range(0, Width).Where(x => x % 5 != 0)]);

        // Columns a wider row has, then a narrower row: refused there.
        int[] wide = [.. Enumerable.Range(0, Width + 32)];
        var bgr = new byte[(Width + 32) * 3];
        vector.RowToBgr(yuv, Width + 32, Height, 1, wide, bgr);
        Assert.Throws<ArgumentOutOfRangeException>(() => vector.RowToBgr(yuv, Width, Height, 1, wide, bgr));
    }

    // A block takes 16 pixels from an even column within the row (its chroma: 8 whole samples), so near the row's end
    // and in short rows the columns go a pixel at a time.
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(255)]
    public void BlocksStayWithinTheRow(int width)
    {
        var plan = new SwscaleYuv420.Plan([.. Enumerable.Range(0, width)], width);
        Assert.All(plan.Full.Concat(plan.Partial), x0 => Assert.True(x0 % 2 == 0 && x0 + 16 <= width, $"block at {x0}"));
        Assert.Equal(Enumerable.Range(0, width), plan.Full.SelectMany(x0 => Enumerable.Range(x0, 16)).Concat(plan.Singles).Order());
        Assert.Empty(plan.Partial);
    }

    // CvResize converts rows on several threads with one converter; alternating sets of columns make the threads
    // replace each other's plans meanwhile.
    [Fact]
    public void ConcurrentRowsWithChangingColumns()
    {
        const int Width = 640, Height = 64;
        var yuv = new byte[IYuv420Converter.FrameSize(Width, Height)];
        new Random(3).NextBytes(yuv);
        var (vector, scalar) = Converters("smpte170m", false);
        int[][] sets = [[.. Enumerable.Range(0, Width)], [.. Enumerable.Range(0, Width).Where(x => x % 5 != 1)], [.. Enumerable.Range(100, 300)]];
        var expected = new byte[sets.Length][];
        for (int s = 0; s < sets.Length; s++)
        {
            expected[s] = new byte[Height * Width * 3];
            for (int y = 0; y < Height; y++)
                scalar.RowToBgr(yuv, Width, Height, y, sets[s], expected[s].AsSpan(y * Width * 3, Width * 3));
        }

        for (int round = 0; round < 20; round++)
        {
            var actual = sets.Select(_ => new byte[Height * Width * 3]).ToArray();
            Parallel.For(0, Height * sets.Length, i =>
            {
                int s = i % sets.Length, y = i / sets.Length;
                vector.RowToBgr(yuv, Width, Height, y, sets[s], actual[s].AsSpan(y * Width * 3, Width * 3));
            });
            for (int s = 0; s < sets.Length; s++)
                Assert.Equal(expected[s], actual[s]);
        }
    }

    // 4:2:2 takes the same blocks, with a chroma row per row.
    [Fact]
    public void Yuv422BlocksGiveTheTablesBytes()
    {
        const int Width = 333, Height = 6;
        var yuv = new byte[IYuv422Converter.FrameSize(Width, Height)];
        new Random(422).NextBytes(yuv);
        var (vector, scalar) = Converters("bt709", false);
        int[] cols = Enumerable.Range(0, Width).ToArray();
        for (int y = 0; y < Height; y++)
        {
            var a = new byte[Width * 3];
            var b = new byte[Width * 3];
            ((IYuv422Converter)scalar).RowToBgr(yuv, Width, Height, y, cols, a);
            ((IYuv422Converter)vector).RowToBgr(yuv, Width, Height, y, cols, b);
            Assert.Equal(a, b);
        }
    }

    // Converters take the vectors wherever Vector128 is accelerated (CI's x64 and ARM runners), so the tests above
    // compare two paths there.
    [Fact]
    public void ConvertersUseTheVectorsWhereAccelerated()
    {
        Assert.Equal(Vector128.IsHardwareAccelerated, new SwscaleYuv420().Vectorized);
        Assert.Equal(Vector128.IsHardwareAccelerated, ((SwscaleYuv420)new SwscaleYuv420().ForColor("bt709", true)!).Vectorized);
    }

    [Theory]
    [InlineData(8, 24, 8)] // a column past the row
    [InlineData(8, 24, -1)]
    [InlineData(8, 12, 4)] // a pixel past the BGR row
    [InlineData(64, 192, 64)] // ... where the columns would make blocks
    [InlineData(64, 192, -2)]
    [InlineData(64, 96, 40)]
    public void RefusesAColumnOutsideTheRow(int width, int bgrBytes, int column)
    {
        var yuv = new byte[IYuv420Converter.FrameSize(width, 2)];
        int[] cols = [.. Enumerable.Range(0, Math.Min(width, bgrBytes / 3)), column];
        var bgr = new byte[bgrBytes + 256]; // room past the row, so code that didn't check would write there, not past the array
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwscaleYuv420().RowToBgr(yuv, width, 2, 0, cols, bgr.AsSpan(0, bgrBytes)));
    }

    static (SwscaleYuv420 Vector, SwscaleYuv420 Scalar) Converters(string colorSpace, bool fullRange)
    {
        var vector = (SwscaleYuv420)new SwscaleYuv420().ForColor(colorSpace, fullRange)!;
        var scalar = (SwscaleYuv420)new SwscaleYuv420().ForColor(colorSpace, fullRange)!;
        vector.Vectorized = true;
        scalar.Vectorized = false;
        return (vector, scalar);
    }

    static IEnumerable<int[]> ColumnSets(int width, Random rnd)
    {
        yield return [.. Enumerable.Range(0, width)];
        if (width > 4)
        {
            var (dw, _) = VideoReader.DownscaledSize(width, 360);
            yield return new CvResize(width, 360, Math.Max(1, Math.Min(dw, width * 2 / 5)), 144).SourceCols;
        }

        foreach (double density in new[] { 0.3, 0.65, 0.8, 0.95 })
            yield return [.. Enumerable.Range(0, width).Where(_ => rnd.NextDouble() < density)];
        int[] shuffled = [.. Enumerable.Range(0, width).Where(_ => rnd.NextDouble() < 0.9), .. Enumerable.Range(0, width / 3)];
        rnd.Shuffle(shuffled);
        yield return shuffled;
        yield return [];
    }

    static byte[] Sentinel(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11))];
}
