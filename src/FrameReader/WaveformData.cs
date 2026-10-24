namespace FrameReader;

/// <summary>The resolution of a saved .dat (audiowaveform's <c>-b</c>).</summary>
public enum WaveformBits
{
    /// <summary>8-bit values: each 16-bit one divided by 256, toward zero.</summary>
    Eight = 8,

    /// <summary>16-bit values, as computed.</summary>
    Sixteen = 16,
}

/// <summary>How <see cref="WaveformData.Read"/> summarises the audio: audiowaveform's options.</summary>
public sealed record WaveformOptions
{
    /// <summary>Input samples per point, at least 2 (audiowaveform's <c>--zoom</c>; default 256).</summary>
    public int SamplesPerPixel { get; init; } = 256;

    /// <summary>
    /// Points per second instead (<c>--pixels-per-second</c>): <see cref="SamplesPerPixel"/> becomes the sample rate
    /// divided by it, rounded down. Null: use <see cref="SamplesPerPixel"/>.
    /// </summary>
    public int? PixelsPerSecond { get; init; }

    /// <summary>One waveform per channel (<c>--split-channels</c>); false: the channels averaged into one.</summary>
    public bool SplitChannels { get; init; }

    /// <summary>Receives progress (<see cref="MediaProgress"/>): about 10 times a second, and once at the end.</summary>
    public IProgress<MediaProgress>? Progress { get; init; }
}

/// <summary>
/// Waveform peaks as BBC's audiowaveform computes them: per point (a run of <see cref="SamplesPerPixel"/> samples) and
/// channel, the minimum and maximum 16-bit sample. <see cref="Save"/> writes its binary .dat format (versions 1 and 2),
/// which peaks.js and other waveform viewers load. The values equal audiowaveform's where both read the same samples
/// (WAV, FLAC and other integer PCM); for lossy codecs the two decoders can differ slightly. For samples decoded
/// elsewhere (no FFmpeg), <see cref="WaveformBuilder"/> computes the same points a chunk at a time.
/// </summary>
public sealed class WaveformData
{
    readonly short[] _data; // per point, per channel: min, max

    internal WaveformData(int sampleRate, int samplesPerPixel, int channels, short[] data) =>
        (SampleRate, SamplesPerPixel, Channels, _data) = (sampleRate, samplesPerPixel, channels, data);

    /// <summary>The audio's sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Input samples per point.</summary>
    public int SamplesPerPixel { get; }

    /// <summary>Waveforms: 1, or the audio's channels with <see cref="WaveformOptions.SplitChannels"/>.</summary>
    public int Channels { get; }

    /// <summary>Points per channel.</summary>
    public int Length => _data.Length / (2 * Channels);

    /// <summary>The lowest sample of a point.</summary>
    /// <param name="channel">The waveform, from 0.</param>
    /// <param name="index">The point, from 0.</param>
    public short Min(int channel, int index) => _data[(index * Channels + channel) * 2];

    /// <summary>The highest sample of a point.</summary>
    /// <param name="channel">The waveform, from 0.</param>
    /// <param name="index">The point, from 0.</param>
    public short Max(int channel, int index) => _data[(index * Channels + channel) * 2 + 1];

    /// <summary>Reads the rest of <paramref name="audio"/> (at its rate and channels) into waveform points.</summary>
    /// <param name="audio">The samples; read to the end, not disposed.</param>
    /// <param name="options">Points' size and channels; null: 256 samples per point, channels averaged.</param>
    /// <param name="cancellationToken">Stops between chunks (<see cref="OperationCanceledException"/>).</param>
    /// <exception cref="ArgumentException">Fewer than 2 samples per point, or more than 24 channels to split.</exception>
    /// <exception cref="FrameReaderException">Decoding failed.</exception>
    public static WaveformData Read(AudioReader audio, WaveformOptions? options = null, CancellationToken cancellationToken = default)
    {
        var builder = new WaveformBuilder(audio.SampleRate, audio.Channels, options, audio.Duration, integerSamples: !audio.DecodesFloat);
        while (audio.TryRead(out var chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(chunk.Samples, chunk.Time);
        }

        return builder.Finish();
    }

    /// <summary>As <see cref="Read"/>, off the calling thread.</summary>
    public static Task<WaveformData> ReadAsync(AudioReader audio, WaveformOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(audio, options, cancellationToken), cancellationToken);

    /// <summary>
    /// A float sample as the 16-bit one audiowaveform reads through libsndfile: integer sources (exact in float for up to
    /// 24 bits) shifted down to 16 bits, rounding toward minus infinity as libsndfile's shift does; float sources scaled
    /// by 32767, truncated and cast to a short as audiowaveform does, so beyond full scale they wrap around rather than
    /// clip (static_cast&lt;short&gt; keeps the low 16 bits).
    /// </summary>
    internal static int ToShort(float sample, bool fromFloat) => fromFloat
        ? (short)(int)Math.Clamp(MathF.Truncate(sample * 32767f), int.MinValue, int.MaxValue)
        : (int)Math.Clamp(Math.Floor(sample * 32768.0), short.MinValue, short.MaxValue);

    /// <summary>
    /// Writes audiowaveform's binary .dat: version 1 for one waveform, 2 (with the channel count) for several;
    /// little-endian.
    /// </summary>
    /// <param name="output">Where to write; not disposed.</param>
    /// <param name="bits">Sixteen (default), or Eight: each value divided by 256, toward zero, as audiowaveform does.</param>
    public void Save(Stream output, WaveformBits bits = WaveformBits.Sixteen)
    {
        if (bits is not (WaveformBits.Eight or WaveformBits.Sixteen))
            throw new ArgumentException("Bits must be Eight or Sixteen.", nameof(bits));
        using var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        int version = Channels == 1 ? 1 : 2;
        w.Write(version);
        w.Write(bits == WaveformBits.Eight ? 1U : 0U);
        w.Write(SampleRate);
        w.Write(SamplesPerPixel);
        w.Write((uint)Length);
        if (version == 2)
            w.Write(Channels);
        foreach (short v in _data)
        {
            if (bits == WaveformBits.Eight)
                w.Write((sbyte)(v / 256));
            else
                w.Write(v);
        }
    }
}
