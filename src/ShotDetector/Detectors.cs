namespace ShotDetector;

/// <summary>
/// A cut detector fed frames in order (0-based decode index). Returns where a new shot starts, which
/// may lag behind the frame just fed, or null. Detectors measure lengths through
/// <c>position</c> (frame index → time, as PySceneDetect's FrameTimecode positions), so variable
/// frame rate video behaves as it does in PySceneDetect.
/// </summary>
public interface IDetector
{
    /// <summary>Feeds the next frame (packed BGR, downscaled); returns where a new shot starts, if a cut was found.</summary>
    FrameTime? ProcessFrame(int frame, byte[] bgr);

    /// <summary>Called once after the last frame; <paramref name="end"/> is the position after decoding stopped.</summary>
    FrameTime? PostProcess(FrameTime end) => null;

    /// <summary>When set, per-frame metrics are recorded here (like `scenedetect -s`).</summary>
    Stats? Stats { get; set; }
}

/// <summary>
/// Port of PySceneDetect's ContentDetector: cut when content_val >= threshold, with FlashFilter in
/// MERGE mode (its default) enforcing min_scene_len. SUPPRESS filter mode is not ported.
/// </summary>
public sealed class ContentDetector(
    ContentScorer scorer,
    Func<int, FrameTime> position,
    Fps fps,
    double threshold = 27.0,
    int minSceneLen = 15) : IDetector
{
    int? _lastAbove, _mergeStart;
    bool _mergeEnabled, _mergeTriggered;
    readonly double _minSeconds = minSceneLen / fps.Value;
    /// <inheritdoc/>
    public Stats? Stats { get; set; }

    // FlashFilter compares the time between positions in float seconds against min_scene_len / fps.
    // Faithful quirk: positions are µs-rounded, so a gap of exactly minSceneLen frames can fall just
    // short (e.g. 14 frames at 24 fps: 0.583333 < 0.58333333).
    bool MinLengthMet(int frame, int since) => position(frame).Minus(position(since)).Seconds >= _minSeconds;

    /// <inheritdoc/>
    public FrameTime? ProcessFrame(int frame, byte[] bgr)
    {
        double score = scorer.Score(bgr);
        scorer.Record(Stats, frame, score);
        return ProcessScore(frame, score) is int cut ? position(cut) : null;
    }

    /// <summary>Returns the frame index of a cut.</summary>
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
    Func<int, FrameTime> position,
    double adaptiveThreshold = 3.0,
    int minSceneLen = 15,
    int windowWidth = 2,
    double minContentVal = 15.0) : IDetector
{
    readonly List<(int Frame, double Score)> _buffer = [];
    readonly string _ratioKey = $"adaptive_ratio{(scorer.IsLumaOnly ? "_lum" : "")} (w={windowWidth})";
    int? _lastCut;
    /// <inheritdoc/>
    public Stats? Stats { get; set; }

    /// <inheritdoc/>
    public FrameTime? ProcessFrame(int frame, byte[] bgr)
    {
        double score = scorer.Score(bgr);
        scorer.Record(Stats, frame, score);
        return ProcessScore(frame, score) is int cut ? position(cut) : null;
    }

    /// <summary>Returns the frame index of a cut.</summary>
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
        // Faithful quirk: min length is measured from the *current* frame, not the target frame,
        // as a frame count derived from the time between them (round(seconds * fps)).
        bool minLengthMet = position(frame).Minus(position(_lastCut.Value)).FrameNum >= minSceneLen;
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
/// In 0.6.4 mode the split frame is int((fade_in + fade_out + int(bias * (fade_in - fade_out))) / 2),
/// as 0.6.4 computes it, instead of 0.7.1's fade_out + round((fade_in - fade_out) * (1 + bias) / 2).
/// </summary>
public sealed class ThresholdDetector(
    Func<int, FrameTime> position,
    Fps fps,
    double threshold = 12,
    int minSceneLen = 15,
    double fadeBias = 0,
    PySceneDetectVersion version = PySceneDetectVersion.V0_7_1) : IDetector
{
    readonly int _threshold = (int)threshold; // PySceneDetect truncates it to an int
    int? _lastSceneCut;
    int _lastFadeFrame;
    bool _fadedOut, _processed;
    /// <inheritdoc/>
    public Stats? Stats { get; set; }

    /// <inheritdoc/>
    public FrameTime? ProcessFrame(int frame, byte[] bgr)
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

    /// <summary>Feeds the next frame's mean pixel level (see <see cref="Average"/>); returns a cut, if any.</summary>
    public FrameTime? ProcessAverage(int frame, double average)
    {
        _lastSceneCut ??= frame;
        if (!_processed)
        {
            _processed = true;
            _lastFadeFrame = frame;
            _fadedOut = average < _threshold;
            return null;
        }

        FrameTime? cut = null;
        if (!_fadedOut && average < _threshold)
        {
            _fadedOut = true;
            _lastFadeFrame = frame;
        }
        else if (_fadedOut && average >= _threshold)
        {
            if (position(frame).Minus(position(_lastSceneCut.Value)).FrameNum >= minSceneLen)
            {
                // The split is computed as a bare frame number from the (time-derived) frame numbers.
                long fadeOut = position(_lastFadeFrame).FrameNum, fadeIn = position(frame).FrameNum;
                cut = version == PySceneDetectVersion.V0_6_4
                    ? FrameTime.Frame064((long)((fadeIn + fadeOut + (long)(fadeBias * (fadeIn - fadeOut))) / 2.0), fps)
                    : FrameTime.Frame(fadeOut + (long)Math.Round((fadeIn - fadeOut) * (1.0 + fadeBias) / 2.0), fps);
                _lastSceneCut = frame;
            }
            _fadedOut = false;
            _lastFadeFrame = frame;
        }
        return cut;
    }

    /// <inheritdoc/>
    public FrameTime? PostProcess(FrameTime end) =>
        _processed && _fadedOut && end.Minus(position(_lastSceneCut ?? 0)).FrameNum >= minSceneLen
            ? position(_lastFadeFrame)
            : null;
}
