namespace FrameReader;

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
}

/// <summary>
/// Waveform peaks as BBC's audiowaveform computes them: per point (a run of <see cref="SamplesPerPixel"/> samples) and
/// channel, the minimum and maximum 16-bit sample. <see cref="Save"/> writes its binary .dat format (versions 1 and 2),
/// which peaks.js and other waveform viewers load. The values equal audiowaveform's where both read the same samples
/// (WAV, FLAC and other integer PCM); for lossy codecs the two decoders can differ slightly.
/// </summary>
public sealed class WaveformData
{
    const int MaxChannels = 24; // audiowaveform's WaveformBuffer::MAX_CHANNELS

    readonly short[] _data; // per point, per channel: min, max

    WaveformData(int sampleRate, int samplesPerPixel, int channels, short[] data) =>
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
    /// <exception cref="ArgumentException">Fewer than 2 samples per point, or more than 24 channels to split.</exception>
    /// <exception cref="FrameReaderException">Decoding failed.</exception>
    public static WaveformData Read(AudioReader audio, WaveformOptions? options = null)
    {
        var o = options ?? new WaveformOptions();
        int rate = audio.SampleRate, inChannels = audio.Channels;
        int perPixel = o.PixelsPerSecond is { } pps
            ? (pps > 0 ? rate / pps : throw new ArgumentException("PixelsPerSecond must be positive.", nameof(options)))
            : o.SamplesPerPixel;
        if (perPixel < 2)
            throw new ArgumentException("A point needs at least 2 samples (audiowaveform's minimum zoom).", nameof(options));
        if (inChannels > MaxChannels)
            throw new ArgumentException($"audiowaveform handles at most {MaxChannels} channels.", nameof(options));
        int channels = o.SplitChannels ? inChannels : 1;
        bool fromFloat = audio.DecodesFloat;

        var data = new List<short>();
        var min = new int[channels];
        var max = new int[channels];
        Array.Fill(min, short.MaxValue);
        Array.Fill(max, short.MinValue);
        int count = 0;
        Span<int> frame = stackalloc int[inChannels];
        while (audio.TryRead(out var chunk))
        {
            var samples = chunk.Samples;
            for (int i = 0; i < chunk.Length; i++)
            {
                for (int c = 0; c < inChannels; c++)
                    frame[c] = ToShort(samples[i * inChannels + c], fromFloat);
                if (channels == 1)
                {
                    // The channels' mean, truncated toward zero, as audiowaveform mixes them.
                    int sum = 0;
                    foreach (int s in frame)
                        sum += s;
                    int sample = sum / inChannels;
                    min[0] = Math.Min(min[0], sample);
                    max[0] = Math.Max(max[0], sample);
                }
                else
                    for (int c = 0; c < channels; c++)
                    {
                        min[c] = Math.Min(min[c], frame[c]);
                        max[c] = Math.Max(max[c], frame[c]);
                    }
                if (++count == perPixel)
                    Flush();
            }
        }
        if (count > 0)
            Flush(); // the last, partial point
        return new WaveformData(rate, perPixel, channels, [.. data]);

        void Flush()
        {
            for (int c = 0; c < channels; c++)
            {
                data.Add((short)min[c]);
                data.Add((short)max[c]);
                min[c] = short.MaxValue;
                max[c] = short.MinValue;
            }
            count = 0;
        }
    }

    /// <summary>
    /// A float sample as the 16-bit one audiowaveform reads through libsndfile: integer sources (exact in float for up to
    /// 24 bits) shifted down to 16 bits, rounding toward minus infinity as libsndfile's shift does; float sources scaled
    /// by 32767 and truncated, as audiowaveform scales them.
    /// </summary>
    internal static int ToShort(float sample, bool fromFloat)
    {
        double scaled = fromFloat ? Math.Truncate(sample * 32767f) : Math.Floor(sample * 32768.0);
        return (int)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Writes audiowaveform's binary .dat: version 1 for one waveform, 2 (with the channel count) for several;
    /// little-endian.
    /// </summary>
    /// <param name="output">Where to write; not disposed.</param>
    /// <param name="bits">16 (default), or 8: each value divided by 256, toward zero, as audiowaveform does.</param>
    public void Save(Stream output, int bits = 16)
    {
        if (bits is not (8 or 16))
            throw new ArgumentException("Bits must be 8 or 16.", nameof(bits));
        using var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        int version = Channels == 1 ? 1 : 2;
        w.Write(version);
        w.Write(bits == 8 ? 1u : 0u);
        w.Write(SampleRate);
        w.Write(SamplesPerPixel);
        w.Write((uint)Length);
        if (version == 2)
            w.Write(Channels);
        foreach (short v in _data)
        {
            if (bits == 8)
                w.Write((sbyte)(v / 256));
            else
                w.Write(v);
        }
    }
}
