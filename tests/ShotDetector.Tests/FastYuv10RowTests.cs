using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// ShotDetector.FastYuv's 10-bit conversion (SwscaleYuv420P10) with Vector128 against its scalar code: the same bytes
/// for every 16-bit luma word, every colour matrix and range and chroma siting, the 10-bit extremes, any even width and
/// any set of columns, a frame at a time (packed) or a row at a time, and nothing written where no column is.
/// </summary>
public class FastYuv10RowTests
{
    // Every set of coefficients FastYuv has, in both ranges.
    public static TheoryData<string, bool> Colours => new()
    {
        { "unknown", false }, { "unknown", true }, { "bt709", false }, { "bt709", true }, { "fcc", false }, { "fcc", true },
        { "smpte240m", false }, { "smpte240m", true }, { "bt2020nc", false }, { "bt2020nc", true },
    };

    // Each row of a 65536-wide frame holds every 16-bit luma word (shifted along the row from row to row), over chroma
    // planes of the 10-bit extremes, limited range's 64 and 940, and noise: the pixels of every row, a frame at a time
    // and a row at a time, with chroma left (a 4-tap horizontal filter), center (1 tap) and topleft, the horizontal
    // filter with this CPU's vectors and with portable ones. The last two rows take swscale's C output, the others the
    // MMXEXT one the vectors do. Except on x86's horizontal filter (pmaddwd: signed words, as before), any 16-bit chroma
    // words give the scalar code's bytes too.
    [Theory]
    [MemberData(nameof(Colours))]
    public void EveryLumaWordGivesTheScalarsBytes(string colorSpace, bool fullRange)
    {
        const int Width = 65536, Height = 12;
        int[] all = Enumerable.Range(0, Width).ToArray(), rows = Enumerable.Range(0, Height).ToArray();
        foreach (string chroma in new[] { "zero", "max", "limited", "noise", "words" })
        {
            var yuv = Frame(Width, Height, chroma, new Random(chroma.Length));
            var luma = MemoryMarshal.Cast<byte, ushort>(yuv.AsSpan(0, Width * Height * 2));
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    luma[y * Width + x] = (ushort)(x + y * 4099);
            }

            foreach (var (siting, portable) in new[] { ("left", false), ("left", true), ("center", false), ("topleft", false), ("topleft", true) })
            {
                var (vector, scalar) = Converters(colorSpace, fullRange, siting, Width, Height, portable);
                if (chroma == "words" && vector.HorizontalVectors(4) == SwscaleYuv420P10.Horizontal.X86)
                    continue;
                var a = new byte[Height * Width * 3];
                var b = new byte[Height * Width * 3];
                scalar.FrameToBgr(yuv, Width, Height, rows, all, a);
                vector.FrameToBgr(yuv, Width, Height, rows, all, b);
                Same(a, b, $"{chroma} chroma, {siting}, portable {portable}, a frame at a time");
                foreach (int y in new[] { 0, 5, Height - 1 })
                {
                    Array.Clear(b);
                    vector.RowToBgr(yuv, Width, Height, y, all, b.AsSpan(0, Width * 3));
                    Same(a.AsSpan(y * Width * 3, Width * 3), b.AsSpan(0, Width * 3), $"{chroma} chroma, {siting}, portable {portable}, row {y}");
                }
            }
        }
    }

    // Noise planes at even widths (from the narrowest every siting takes) around the block size and the tail, with the
    // columns a resize reads, all, random subsets of many densities, shuffled ones with repeats, all twice, backwards and
    // with each pair swapped: the same bytes both ways (the horizontal filter with this CPU's vectors and with portable
    // ones), packed and a row at a time, and nothing written outside the columns' pixels.
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(30)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(46)]
    [InlineData(64)]
    [InlineData(66)]
    [InlineData(100)]
    [InlineData(258)]
    [InlineData(426)]
    [InlineData(640)]
    [InlineData(1002)]
    [InlineData(1920)]
    [InlineData(4100)]
    public void SameBytesAndUntouchedPixelsAtAnyWidth(int width)
    {
        var rnd = new Random(width);
        int height = 9 + rnd.Next(6);
        var yuv = Frame(width, height, "noise", rnd);
        foreach (var (colorSpace, fullRange, siting, portable) in new[]
        {
            ("unknown", false, "center", false), ("bt709", true, "left", false), ("bt709", true, "left", true), ("bt2020nc", false, "topleft", true),
        })
        {
            var (vector, scalar) = Converters(colorSpace, fullRange, siting, width, height, portable);
            foreach (int[] cols in ColumnSets(width, rnd))
            {
                // A frame at a time: some rows (the last two among them), packed, with room after them that stays as it was.
                int[] rows = [.. Enumerable.Range(0, height).Where(y => y % 3 != 1)];
                int packed = rows.Length * cols.Length * 3;
                var a = Sentinel(packed + 50);
                var b = Sentinel(packed + 50);
                scalar.FrameToBgr(yuv, width, height, rows, cols, a);
                vector.FrameToBgr(yuv, width, height, rows, cols, b);
                Same(a, b, $"width {width}, {cols.Length} columns, packed");
                Assert.True(b.AsSpan(packed).SequenceEqual(Sentinel(packed + 50).AsSpan(packed)), $"width {width}: written past the packed rows");

                // A row at a time: only the columns' pixels change.
                var asked = cols.ToHashSet();
                var untouched = Sentinel(width * 3 + 50);
                for (int y = 0; y < height; y++)
                {
                    a = Sentinel(width * 3 + 50);
                    b = Sentinel(width * 3 + 50);
                    scalar.RowToBgr(yuv, width, height, y, cols, a);
                    vector.RowToBgr(yuv, width, height, y, cols, b);
                    Same(a, b, $"width {width}, {cols.Length} columns, row {y}");
                    for (int i = 0; i < b.Length; i++)
                    {
                        if (i >= width * 3 || !asked.Contains(i / 3))
                            Assert.True(b[i] == untouched[i], $"width {width}, row {y}: byte {i} isn't a column's but changed");
                    }
                }
            }
        }
    }

    // The pipeline's own calls: the rows and columns the resize reads at the benchmark's four sizes (426 and 640 wide
    // read runs of columns: direct and copied blocks), a frame at a time, also in row chunks on several cores.
    [Theory]
    [InlineData(426, 240)]
    [InlineData(640, 360)]
    [InlineData(1280, 534)]
    [InlineData(1920, 1080)]
    public void TheResizesColumnsGiveTheScalarsBytes(int width, int height)
    {
        var (dw, dh) = VideoReader.DownscaledSize(width, height);
        var resizer = new CvResize(width, height, dw, dh);
        int[] rows = resizer.SourceRows, cols = resizer.SourceCols;
        var yuv = Frame(width, height, "noise", new Random(width));
        foreach (bool portable in new[] { false, true })
        {
            var (vector, scalar) = Converters("bt709", false, "left", width, height, portable);
            int n = cols.Length * 3;
            var a = new byte[rows.Length * n];
            var b = new byte[rows.Length * n];
            scalar.FrameToBgr(yuv, width, height, rows, cols, a);
            vector.FrameToBgr(yuv, width, height, rows, cols, b);
            Same(a, b, $"{width}x{height}, portable {portable}");
            Array.Clear(b);
            Parallel.For(0, 4, p =>
            {
                int first = p * rows.Length / 4, end = (p + 1) * rows.Length / 4;
                vector.FrameToBgr(yuv, width, height, rows.AsSpan(first, end - first), cols, b.AsSpan(first * n, (end - first) * n));
            });
            Same(a, b, $"{width}x{height}, portable {portable}, in chunks");
        }
    }

    // Blocks: 16 pixels from an even column inside the row, over 8 consecutive chroma samples; direct ones where the
    // columns ask for exactly those pixels in order; each column in one place (a direct block, a copy or a pixel).
    [Theory]
    [InlineData(16, "all")]
    [InlineData(18, "all")]
    [InlineData(40, "all")]
    [InlineData(258, "all")]
    [InlineData(640, "resize")]
    [InlineData(640, "repeats")]
    [InlineData(300, "gaps")]
    public void BlocksStayWithinTheRowAndCoverEachColumnOnce(int width, string kind)
    {
        int[] cols = kind switch
        {
            "all" => [.. Enumerable.Range(0, width)],
            "resize" => new CvResize(width, 360, 256, 144).SourceCols,
            "repeats" => [.. Enumerable.Range(0, width), .. Enumerable.Range(100, 40)],
            _ => [.. Enumerable.Range(0, width).Where(x => x % 16 != 7 && x % 50 > 3)],
        };
        int[] chromaCols = [.. cols.Select(x => x >> 1).Where((c, j) => j == 0 || c != cols[j - 1] >> 1)];
        var plan = new SwscaleYuv420P10.PixelPlan(cols, chromaCols);
        var seen = new int[cols.Length];
        for (int i = 0; i < plan.Direct.Length; i += 3)
        {
            int x0 = plan.Direct[i], j0 = plan.Direct[i + 1], k0 = plan.Direct[i + 2];
            Block(x0, k0);
            for (int m = 0; m < 16; m++)
            {
                Assert.Equal(x0 + m, cols[j0 + m]);
                seen[j0 + m]++;
            }
        }

        for (int i = 0; i < plan.Copied.Length; i += 4)
        {
            int x0 = plan.Copied[i], k0 = plan.Copied[i + 1];
            Block(x0, k0);
            for (int c = plan.Copied[i + 2]; c < plan.Copied[i + 3]; c++)
            {
                int j = plan.Copies[2 * c];
                Assert.Equal(cols[j] - x0, plan.Copies[(2 * c) + 1]);
                seen[j]++;
            }
        }

        foreach (int j in plan.Singles)
            seen[j]++;
        Assert.All(seen, s => Assert.Equal(1, s));
        Assert.True(plan.HasBlocks);
        if (kind == "all")
        {
            Assert.Empty(plan.Copied);
            Assert.Equal(width / 16 * 16, plan.Direct.Length / 3 * 16);
        }

        void Block(int x0, int k0)
        {
            Assert.True(x0 % 2 == 0 && x0 >= 0 && x0 + 16 <= width, $"block at {x0}");
            Assert.True(k0 + 8 <= chromaCols.Length, $"block at {x0}: chroma from {k0}");
            for (int i = 0; i < 8; i++)
                Assert.Equal((x0 >> 1) + i, chromaCols[k0 + i]);
        }
    }

    // A block needs its 8 chroma samples, consecutive: without one of them (neither of its two pixels asked for), or
    // with fewer than SwscaleYuv420.MinColumnsPerBlock of its pixels asked for, its columns go a pixel at a time.
    [Fact]
    public void BlocksNeedTheirChromaAndEnoughColumns()
    {
        int[] cols = [.. Enumerable.Range(0, 64).Where(x => x is not (20 or 21))];
        int[] chromaCols = [.. cols.Select(x => x >> 1).Distinct()];
        var plan = new SwscaleYuv420P10.PixelPlan(cols, chromaCols);
        for (int i = 0; i < plan.Copied.Length; i += 4)
            Assert.False(plan.Copied[i] <= 20 && plan.Copied[i] + 16 > 20, $"a block at {plan.Copied[i]} over chroma sample 10, which isn't there");
        for (int i = 0; i < plan.Direct.Length; i += 3)
            Assert.False(plan.Direct[i] <= 20 && plan.Direct[i] + 16 > 20, $"a block at {plan.Direct[i]} over chroma sample 10, which isn't there");

        // Every chroma sample, but 8 of a block's 16 pixels.
        int[] sparse = [.. Enumerable.Range(0, 64).Where(x => x % 2 == 0)];
        var none = new SwscaleYuv420P10.PixelPlan(sparse, [.. sparse.Select(x => x >> 1).Distinct()]);
        Assert.False(none.HasBlocks);
        Assert.Equal(sparse.Length, none.Singles.Length);
    }

    // One converter for every frame keeps the plan it made for the first frame's columns; columns changed in place get
    // their own, and the same columns at another width work with the same plan (it depends on the columns alone).
    [Fact]
    public void ColumnsChangedInPlaceOrAnotherWidthGetTheirOwnPlan()
    {
        const int Width = 96, Height = 12;
        var yuv = Frame(Width + 32, Height, "noise", new Random(7));
        int[] cols = Enumerable.Range(0, Width).ToArray(), rows = [3, 4];
        var vector = Vectorized(SwscaleYuv420P10.For("bt709", false, "left", Width, Height)!, true);
        void Check(int width, int[] columns)
        {
            var (_, scalar) = Converters("bt709", false, "left", width, Height);
            var a = new byte[rows.Length * columns.Length * 3];
            var b = new byte[rows.Length * columns.Length * 3];
            scalar.FrameToBgr(yuv, width, Height, rows, columns, a);
            vector.FrameToBgr(yuv, width, Height, rows, columns, b);
            Assert.Equal(a, b);
        }

        Check(Width, cols);
        for (int i = 0; i < cols.Length; i += 3)
            cols[i] = Width - 1 - cols[i]; // same array, other columns
        Check(Width, cols);
        Check(Width + 32, cols); // same columns, another width (other planes' offsets)
        Check(Width, [.. Enumerable.Range(0, Width).Where(x => x % 5 != 0)]);
        Check(Width + 32, [.. Enumerable.Range(Width - 16, 48)]); // columns only the wider row has
    }

    // CvResize converts row chunks on several threads with one converter; alternating sets of columns make the threads
    // replace each other's plans meanwhile.
    [Fact]
    public void ConcurrentFramesWithChangingColumns()
    {
        const int Width = 640, Height = 64;
        var yuv = Frame(Width, Height, "noise", new Random(3));
        var (vector, scalar) = Converters("smpte170m", false, "left", Width, Height);
        int[][] sets = [[.. Enumerable.Range(0, Width)], [.. Enumerable.Range(0, Width).Where(x => x % 5 != 1)], [.. Enumerable.Range(100, 300)]];
        var expected = new byte[sets.Length][];
        for (int s = 0; s < sets.Length; s++)
        {
            expected[s] = new byte[Height * sets[s].Length * 3];
            scalar.FrameToBgr(yuv, Width, Height, [.. Enumerable.Range(0, Height)], sets[s], expected[s]);
        }

        for (int round = 0; round < 10; round++)
        {
            var actual = sets.Select(s => new byte[Height * s.Length * 3]).ToArray();
            Parallel.For(0, Height * sets.Length, i =>
            {
                int s = i % sets.Length, y = i / sets.Length, n = sets[s].Length * 3;
                vector.FrameToBgr(yuv, Width, Height, [y], sets[s], actual[s].AsSpan(y * n, n));
            });
            for (int s = 0; s < sets.Length; s++)
                Assert.Equal(expected[s], actual[s]);
        }
    }

    // Converters take the vectors wherever Vector128 is accelerated (CI's x64 and ARM runners), so the tests above
    // compare two paths there. The horizontal filter: x86's blocks and SSSE3 on x86 (4 taps), portable vectors on ARM64
    // and wasm (and where tests ask for them), else scalar.
    [Fact]
    public void ConvertersUseTheVectorsWhereAccelerated()
    {
        var converter = SwscaleYuv420P10.For("bt709", true, "left", 64, 16)!;
        Assert.Equal(Vector128.IsHardwareAccelerated, converter.Vectorized);
        Assert.False(converter.PortableHorizontal);
        var expected = !Vector128.IsHardwareAccelerated ? SwscaleYuv420P10.Horizontal.Scalar
            : Sse2.IsSupported ? SwscaleYuv420P10.Horizontal.X86
            : AdvSimd.Arm64.IsSupported ? SwscaleYuv420P10.Horizontal.Portable
            : SwscaleYuv420P10.Horizontal.Scalar;
        Assert.Equal(expected, converter.HorizontalVectors(4));
        Assert.Equal(expected == SwscaleYuv420P10.Horizontal.X86 ? SwscaleYuv420P10.Horizontal.Scalar : expected, converter.HorizontalVectors(1));
        converter.PortableHorizontal = true;
        Assert.Equal(Vector128.IsHardwareAccelerated ? SwscaleYuv420P10.Horizontal.Portable : SwscaleYuv420P10.Horizontal.Scalar, converter.HorizontalVectors(4));
        converter.Vectorized = false;
        Assert.Equal(SwscaleYuv420P10.Horizontal.Scalar, converter.HorizontalVectors(4));
    }

    static (SwscaleYuv420P10 Vector, SwscaleYuv420P10 Scalar) Converters(string colorSpace, bool fullRange, string siting, int width, int height, bool portable = false)
    {
        var vector = Vectorized(SwscaleYuv420P10.For(colorSpace, fullRange, siting, width, height)!, true);
        vector.PortableHorizontal = portable;
        return (vector, Vectorized(SwscaleYuv420P10.For(colorSpace, fullRange, siting, width, height)!, false));
    }

    static SwscaleYuv420P10 Vectorized(SwscaleYuv420P10 converter, bool on)
    {
        converter.Vectorized = on;
        return converter;
    }

    /// <summary>
    /// A yuv420p10le frame: luma noise, chroma 0, 1023, limited range's 64 and 940 (alternating), 10-bit noise, or any
    /// 16-bit words.
    /// </summary>
    static byte[] Frame(int width, int height, string chroma, Random rnd)
    {
        var yuv = new byte[IYuv420P10Converter.FrameSize(width, height)];
        var samples = MemoryMarshal.Cast<byte, ushort>(yuv.AsSpan());
        int luma = width * height;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(i < luma ? rnd.Next(1024) : chroma switch
            {
                "zero" => 0,
                "max" => 1023,
                "limited" => (i / 3) % 2 == 0 ? 64 : 940,
                "words" => rnd.Next(65536),
                _ => rnd.Next(1024),
            });
        }

        return yuv;
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
        yield return [.. Enumerable.Range(0, width), .. Enumerable.Range(0, width)];
        yield return [.. Enumerable.Range(0, width).Reverse()];
        yield return [.. Enumerable.Range(0, width).Select(x => x ^ 1)];
        yield return [];
    }

    static void Same(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        int i = expected.SequenceEqual(actual) ? -1 : expected.CommonPrefixLength(actual);
        Assert.True(i < 0, $"{what}: byte {i} differs ({(i < 0 ? 0 : expected[i])} scalar, {(i < 0 ? 0 : actual[i])} vector)");
    }

    static byte[] Sentinel(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11))];
}
