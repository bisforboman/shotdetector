namespace ShotDetector;

/// <summary>
/// A cut detector fed frames in order. Returns the frame number of a new cut (the first frame of
/// the new shot), which may lag behind <paramref name="frame"/>, or null.
/// </summary>
public interface IDetector
{
    int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr);

    /// <summary>Called once after the last frame (0-based index <paramref name="lastFrame"/>).</summary>
    int? PostProcess(int lastFrame) => null;

    /// <summary>When set, per-frame metrics are recorded here (like `scenedetect -s`).</summary>
    Stats? Stats { get; set; }
}

/// <summary>
/// Port of PySceneDetect's ContentDetector: cut when content_val >= threshold, with FlashFilter in
/// MERGE mode (its default) enforcing min_scene_len. SUPPRESS filter mode is not ported.
/// </summary>
public sealed class ContentDetector(ContentScorer scorer, Fps fps, double threshold = 27.0, int minSceneLen = 15) : IDetector
{
    int? _lastAbove, _mergeStart;
    bool _mergeEnabled, _mergeTriggered;
    readonly double _minSeconds = minSceneLen / fps.Value;
    public Stats? Stats { get; set; }

    // Faithful quirk: FlashFilter compares in float seconds between frame positions that OpenCV
    // reports in ms and PySceneDetect rounds to µs. A gap of exactly minSceneLen frames can then
    // fall just short (e.g. 14 frames at 24 fps: 0.583333 < 0.58333333).
    // ponytail: assumes CFR with the first frame at pts 0, which is what CAP_PROP_POS_MSEC gives for those.
    bool MinLengthMet(int frame, int since) => (Micros(frame) - Micros(since)) / 1e6 >= _minSeconds;
    long Micros(int frame) => (long)Math.Round(frame / fps.Value * 1000.0 * 1000.0);

    public int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr)
    {
        double score = scorer.Score(bgr);
        scorer.Record(Stats, frame, score);
        return ProcessScore(frame, score);
    }

    public int? ProcessScore(int frame, double score)
    {
        bool above = score >= threshold;
        if (minSceneLen <= 0)
            return above ? frame : null;

        _lastAbove ??= frame;
        bool minLengthMet = MinLengthMet(frame, _lastAbove.Value);
        if (above)
            _lastAbove = frame;

        if (_mergeTriggered)
        {
            // Keep merging until enough frames pass below the threshold.
            if (minLengthMet && !above && MinLengthMet(_lastAbove.Value, _mergeStart!.Value))
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
    readonly string _ratioKey = $"adaptive_ratio{(scorer.IsLumaOnly ? "_lum" : "")} (w={windowWidth})";
    int? _lastCut;
    public Stats? Stats { get; set; }

    public int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr)
    {
        double score = scorer.Score(bgr);
        scorer.Record(Stats, frame, score);
        return ProcessScore(frame, score);
    }

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
        Stats?.Set(targetFrame, _ratioKey, ratio);

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

/// <summary>
/// Port of PySceneDetect's ThresholdDetector (FLOOR method, as the CLI uses): detects fades to and
/// from black by comparing the mean of all B, G and R bytes against <c>threshold</c>. A cut is placed
/// between the fade-out and the following fade-in, skewed by <c>fadeBias</c> (-1 = at the fade-out,
/// 0 = midway, +1 = at the fade-in). If the video ends faded out, the fade-out frame becomes a cut
/// (the CLI's add-last-scene, which cannot be turned off there).
/// Differs: CEILING method is not ported. The scenedetect CLI passes --fade-bias through unscaled
/// even though its option range is -100..100; here it takes the detector's -1..1 meaning.
/// </summary>
public sealed class ThresholdDetector(double threshold = 12, int minSceneLen = 15, double fadeBias = 0) : IDetector
{
    readonly int _threshold = (int)threshold; // PySceneDetect truncates it to an int
    int? _lastSceneCut;
    int _lastFadeFrame;
    bool _fadedOut, _processed;
    public Stats? Stats { get; set; }

    public int? ProcessFrame(int frame, ReadOnlySpan<byte> bgr)
    {
        double average = Average(bgr);
        Stats?.Set(frame, "average_rgb", average);
        return ProcessAverage(frame, average);
    }

    /// <summary>numpy.mean over all bytes; exact, since the integer sum fits a double.</summary>
    public static double Average(ReadOnlySpan<byte> bgr)
    {
        long sum = 0;
        foreach (byte b in bgr)
            sum += b;
        return sum / (double)bgr.Length;
    }

    public int? ProcessAverage(int frame, double average)
    {
        _lastSceneCut ??= frame;
        if (!_processed)
        {
            _processed = true;
            _lastFadeFrame = frame;
            _fadedOut = average < _threshold;
            return null;
        }

        int? cut = null;
        if (!_fadedOut && average < _threshold)
        {
            _fadedOut = true;
            _lastFadeFrame = frame;
        }
        else if (_fadedOut && average >= _threshold)
        {
            if (frame - _lastSceneCut >= minSceneLen)
            {
                int duration = frame - _lastFadeFrame;
                cut = _lastFadeFrame + (int)Math.Round(duration * (1.0 + fadeBias) / 2.0);
                _lastSceneCut = frame;
            }
            _fadedOut = false;
            _lastFadeFrame = frame;
        }
        return cut;
    }

    public int? PostProcess(int lastFrame) =>
        _processed && _fadedOut && lastFrame - (_lastSceneCut ?? 0) >= minSceneLen ? _lastFadeFrame : null;
}
