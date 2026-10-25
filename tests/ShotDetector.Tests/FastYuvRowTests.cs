using ShotDetector;
using ShotDetector.FastYuv;

/// <summary>
/// ShotDetector.FastYuv's 8-bit row conversion (SwscaleYuv420.RowToBgr) reads its planes and tables without range checks:
/// a column outside the row, or outside the BGR row it writes, is still refused.
/// </summary>
public class FastYuvRowTests
{
    [Theory]
    [InlineData(8, 24, 8)] // a column past the row
    [InlineData(8, 24, -1)]
    [InlineData(8, 12, 4)] // a pixel past the BGR row
    public void RefusesAColumnOutsideTheRow(int width, int bgrBytes, int column)
    {
        var yuv = new byte[IYuv420Converter.FrameSize(width, 2)];
        var bgr = new byte[bgrBytes];
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwscaleYuv420().RowToBgr(yuv, width, 2, 0, [0, column], bgr));
    }
}
