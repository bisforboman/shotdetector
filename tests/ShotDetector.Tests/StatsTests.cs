using ShotDetector;

/// <summary>
/// Stats' storage (a column per key, a frame's keys in the order set): what it gives back is what a dictionary per frame
/// gave, wherever and in whatever order frames and keys come. Its rows by position: ThresholdDetectorTests and PropertyTests.
/// </summary>
public class StatsTests
{
    static readonly Fps Fps25 = new(25, 1);

    static FrameTime At(int frame) => FrameTime.Pts(frame, 25, Fps25);

    [Fact]
    public void SparseKeysAndFramesBeforeTheFirst()
    {
        var stats = new Stats();
        stats.Set(5, "a", 1);
        stats.Set(9, "a", 2);
        stats.Set(2, "b", 3);                  // before the first frame so far
        stats.Set(7, "c", 4);                  // a key no earlier frame has
        stats.Set(5, "a", 5);                  // overwrites

        Assert.Equal(5, stats.Get(5, "a"));
        Assert.Equal(3, stats.Get(2, "b"));
        Assert.Null(stats.Get(2, "a"));        // the frame has a row, not this key
        Assert.Null(stats.Get(6, "a"));        // between rows
        Assert.Null(stats.Get(1, "a"));        // before the first
        Assert.Null(stats.Get(10, "a"));       // after the last
        Assert.Null(stats.Get(5, "d"));        // no such key
        Assert.Equal(
            "Frame Number,Timecode,a,b,c\n3,00:00:00.080,None,3.0,None\n6,00:00:00.200,5.0,None,None\n8,00:00:00.280,None,None,4.0\n10,00:00:00.360,2.0,None,None\n",
            stats.Csv(At));
    }

    [Fact]
    public void EntriesKeepEachFramesKeyOrder()
    {
        var stats = new Stats();
        stats.Set(0, "b", 1);
        stats.Set(0, "a", 2);
        stats.Set(1, "a", 3);
        stats.Set(1, "b", 4);
        stats.Set(1, "a", 5);                  // overwriting keeps a's place
        stats.Set(3, "c", 6);

        Assert.Equal([(0, "b", 1.0), (0, "a", 2.0), (1, "a", 5.0), (1, "b", 4.0), (3, "c", 6.0)], stats.Entries(0, int.MaxValue));
        Assert.Equal([(1, "a", 5.0), (1, "b", 4.0)], stats.Entries(1, 3));
        Assert.Empty(stats.Entries(4, 10));
        Assert.Empty(new Stats().Entries(0, int.MaxValue));
    }

    [Fact]
    public void RangesMergedOutOfOrderGiveTheSameStats()
    {
        var whole = new Stats();
        var parts = new[] { new Stats(), new Stats(), new Stats() };
        for (int f = 0; f < 30; f++)
        {
            whole.Set(f, "content_val", f * 0.5);
            parts[f / 10].Set(f, "content_val", f * 0.5);
            if (f % 3 == 0)
            {
                whole.Set(f, "ratio", f);
                parts[f / 10].Set(f, "ratio", f);
            }
        }

        var merged = new Stats();
        merged.Merge(parts[2], 20, int.MaxValue);
        merged.Merge(parts[0], 0, 10);
        merged.Merge(parts[1], 10, 20);

        Assert.Equal(whole.Entries(0, int.MaxValue), merged.Entries(0, int.MaxValue));
        Assert.Equal(whole.Csv(At), merged.Csv(At));
    }

    [Fact]
    public void RowsWhosePositionsGoBackAreSorted()
    {
        var stats = new Stats();
        stats.Set(0, "a", 1);
        stats.Set(1, "a", 2);
        stats.Set(2, "a", 3);
        Assert.Equal("Frame Number,Timecode,a\n1,00:00:00.000,1.0\n2,00:00:00.040,3.0\n3,00:00:00.080,2.0\n",
            stats.Csv(i => At(i == 1 ? 2 : i == 2 ? 1 : i)));
    }

    [Fact]
    public void ARepeatedPositionIsOneRow()
    {
        var stats = new Stats();
        stats.Set(0, "a", 1);
        stats.Set(1, "a", 2);
        Assert.Equal("Frame Number,Timecode,a\n1,00:00:00.000,2.0\n", stats.Csv(_ => At(0)));
    }

    // Positions equal as scenedetect compares them (one Key) though their (Seconds, FrameNum) differ and come in order:
    // still one row. Seconds compare by frame number; timestamps past 2^53 round before they divide.
    [Theory]
    [InlineData("seconds")]
    [InlineData("pts")]
    [InlineData("time base")]
    public void EqualPositionsWithDifferentSecondsShareARow(string kind)
    {
        const long Big = (1L << 53) + 1;
        FrameTime[] positions = kind switch
        {
            "seconds" => [FrameTime.FromSeconds(0, Fps25), FrameTime.FromSeconds(double.Epsilon, Fps25)], // held as their bits: 0 and 1
            "pts" => [FrameTime.Pts(Big, 1, Fps25), FrameTime.Pts(3 * Big, 3, Fps25)],
            _ => [FrameTime.Pts(3, 3 * Big, Fps25), FrameTime.Pts(1, Big, Fps25)],
        };
        Assert.Equal(positions[0].Key, positions[1].Key);
        Assert.True(positions[1].Seconds > positions[0].Seconds);

        var stats = new Stats();
        stats.Set(0, "a", 1);
        stats.Set(1, "b", 2);
        Assert.Equal(2, stats.Csv(i => positions[i]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
