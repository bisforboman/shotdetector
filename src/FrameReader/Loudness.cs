namespace FrameReader;

/// <summary>
/// A programme's loudness per EBU R128 / ITU-R BS.1770, as FFmpeg's <c>ebur128</c> filter measures it with
/// <c>peak=true+sample</c>: the figures its log's Summary prints at the end, at full precision.
/// </summary>
/// <param name="Integrated">Integrated loudness in LUFS (I); -70 when no block passes the absolute gate (silence).</param>
/// <param name="Range">Loudness range in LU (LRA).</param>
/// <param name="RangeLow">The range's low end in LUFS.</param>
/// <param name="RangeHigh">The range's high end in LUFS.</param>
/// <param name="SamplePeak">The highest sample in dBFS, over all channels; negative infinity for digital silence.</param>
/// <param name="TruePeak">The true peak in dBTP (ebur128's: the audio resampled to 192 kHz), over all channels; negative infinity for digital silence.</param>
public readonly record struct LoudnessResult(double Integrated, double Range, double RangeLow, double RangeHigh, double SamplePeak, double TruePeak);

/// <summary>
/// Measures loudness with FFmpeg's own <c>ebur128</c> filter (in the FrameReader.Native libraries) over an
/// <see cref="AudioReader"/>'s decoded audio: the numbers are <c>ffmpeg -af ebur128=peak=true+sample</c>'s.
/// </summary>
public static class Loudness
{
    /// <summary>The filter graph measured with.</summary>
    public const string Graph = "ebur128=peak=true+sample";

    /// <summary>Reads the rest of <paramref name="audio"/> (as decoded: the reader's rate and channel conversion don't apply) and measures it.</summary>
    /// <param name="audio">The audio; read to the end, not disposed.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <exception cref="FrameReaderException">Decoding failed, ebur128 isn't in these libraries, or there were no samples.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static LoudnessResult Measure(AudioReader audio, CancellationToken cancellationToken = default)
    {
        using var filter = new AudioFilter(audio, Graph);
        while (filter.TryRead(out _))
            cancellationToken.ThrowIfCancellationRequested();
        double Get(string option) => filter.Result("ebur128", option)
            ?? throw new FrameReaderException(FrameReaderError.InvalidInput, "The audio has no samples to measure.");
        return new LoudnessResult(Get("integrated"), Get("range"), Get("lra_low"), Get("lra_high"), Get("sample_peak"), Get("true_peak"));
    }

    /// <summary>As <see cref="Measure"/>, off the calling thread.</summary>
    public static Task<LoudnessResult> MeasureAsync(AudioReader audio, CancellationToken cancellationToken = default) =>
        Task.Run(() => Measure(audio, cancellationToken), cancellationToken);
}
