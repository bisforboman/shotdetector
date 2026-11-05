namespace FrameReader;

/// <summary>A gap between two frames' timestamps (<see cref="FrameSteps"/>): frames missing from the picture.</summary>
/// <param name="BeforePts">The pts of the frame before the gap.</param>
/// <param name="AfterPts">The pts of the frame after it.</param>
/// <param name="Missing">The frames missing: the step in frames, rounded, less one.</param>
public readonly record struct FrameGap(long BeforePts, long AfterPts, long Missing);

/// <summary>
/// The steps between a picture's frames in display order, at its frame rate: how many there are, how many aren't one
/// frame (within half a frame either way), and the gaps among them (a step over 1.5 frames: frames dropped). A constant
/// frame rate's steps are all one frame but for its gaps; a variable frame rate's are irregular by design.
/// </summary>
/// <param name="Steps">The steps: the timestamps less one.</param>
/// <param name="Irregular">The steps that aren't one frame, within half a frame (the gaps among them).</param>
/// <param name="Gaps">The steps over 1.5 frames, in order.</param>
public sealed record FrameSteps(int Steps, int Irregular, IReadOnlyList<FrameGap> Gaps)
{
    /// <summary>The frames missing in all the gaps.</summary>
    public long Missing => Gaps.Sum(g => g.Missing);

    /// <summary>
    /// The steps between <paramref name="pts"/> (any order: sorted here, as display order; null entries left out), in
    /// <paramref name="timeBase"/>, at <paramref name="frameRate"/>: e.g. <see cref="MediaInfo.PacketTimestamps"/> and
    /// the stream's <see cref="VideoStreamInfo.FrameRate"/> (r_frame_rate).
    /// </summary>
    /// <exception cref="ArgumentException">A time base or frame rate that isn't positive.</exception>
    public static FrameSteps Of(IEnumerable<long?> pts, Rational timeBase, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(pts);
        if (timeBase.Num <= 0 || timeBase.Den <= 0 || frameRate.Num <= 0 || frameRate.Den <= 0)
            throw new ArgumentException("The time base and frame rate must be positive.");
        var sorted = pts.OfType<long>().Order().ToList();
        double frames = (double)timeBase.Num * frameRate.Num / ((double)timeBase.Den * frameRate.Den);
        int irregular = 0;
        var gaps = new List<FrameGap>();
        for (int i = 1; i < sorted.Count; i++)
        {
            double step = (sorted[i] - sorted[i - 1]) * frames;
            if (Math.Abs(step - 1) <= 0.5)
                continue;
            irregular++;
            if (step > 1.5)
                gaps.Add(new FrameGap(sorted[i - 1], sorted[i], (long)Math.Round(step) - 1));
        }

        return new FrameSteps(Math.Max(0, sorted.Count - 1), irregular, gaps);
    }
}
