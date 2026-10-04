namespace ShotDetector;

/// <summary>
/// A cut detector fed frames in order. Returns the frame number of a new cut (the first frame of
/// the new shot), which may lag behind <paramref name="frame"/>, or null.
/// </summary>
public interface IDetector
{
    int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr);
}

/// <summary>
/// Port of PySceneDetect's ContentDetector: cut when content_val >= threshold, with FlashFilter in
/// MERGE mode (its default) enforcing min_scene_len.
/// Differs: min_scene_len is in frames (PySceneDetect converts to seconds; same result for CFR video).
/// SUPPRESS filter mode is not ported.
/// </summary>
public sealed class ContentDetector(ContentScorer scorer, double threshold = 27.0, int minSceneLen = 15) : IDetector
{
    int? _lastAbove, _mergeStart;
    bool _mergeEnabled, _mergeTriggered;

    public int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr) => ProcessScore(frame, scorer.Score(bgr));

    public int? ProcessScore(int frame, double score)
    {
        bool above = score >= threshold;
        if (minSceneLen <= 0)
            return above ? frame : null;

        _lastAbove ??= frame;
        bool minLengthMet = frame - _lastAbove >= minSceneLen;
        if (above)
            _lastAbove = frame;

        if (_mergeTriggered)
        {
            // Keep merging until enough frames pass below the threshold.
            if (minLengthMet && !above && _lastAbove - _mergeStart >= minSceneLen)
            {
                _mergeTriggered = false;
                return _lastAbove;
            }
            return null;
        }
        if (!above)
            return null;
        if (minLengthMet)
        {
            _mergeEnabled = true; // merging only kicks in after the first cut
            return frame;
        }
        if (_mergeEnabled)
        {
            _mergeTriggered = true;
            _mergeStart = frame;
        }
        return null;
    }
}

/// <summary>
/// Port of PySceneDetect's AdaptiveDetector: compares each frame's content_val against the mean of
/// the <c>windowWidth</c> frames on either side. Cuts are therefore reported windowWidth frames late.
/// </summary>
public sealed class AdaptiveDetector(
    ContentScorer scorer,
    double adaptiveThreshold = 3.0,
    int minSceneLen = 15,
    int windowWidth = 2,
    double minContentVal = 15.0) : IDetector
{
    readonly List<(int Frame, double Score)> _buffer = [];
    int? _lastCut;

    public int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr) => ProcessScore(frame, scorer.Score(bgr));

    public int? ProcessScore(int frame, double score)
    {
        _lastCut ??= frame;
        _buffer.Add((frame, score));
        int required = 1 + 2 * windowWidth;
        if (_buffer.Count < required)
            return null;
        if (_buffer.Count > required)
            _buffer.RemoveAt(0);

        var (targetFrame, targetScore) = _buffer[windowWidth];
        double sum = 0;
        for (int i = 0; i < _buffer.Count; i++)
            if (i != windowWidth) sum += _buffer[i].Score;
        double average = sum / (2.0 * windowWidth);

        double ratio = Math.Abs(average) < 0.00001
            ? (targetScore >= minContentVal ? 255.0 : 0.0)
            : Math.Min(targetScore / average, 255.0);

        bool thresholdMet = ratio >= adaptiveThreshold && targetScore >= minContentVal;
        // Faithful quirk: min length is measured from the *current* frame, not the target frame.
        bool minLengthMet = frame - _lastCut >= minSceneLen;
        if (thresholdMet && minLengthMet)
        {
            _lastCut = targetFrame;
            return targetFrame;
        }
        return null;
    }
}
