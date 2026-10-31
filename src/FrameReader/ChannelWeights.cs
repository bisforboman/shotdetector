namespace FrameReader;

/// <summary>
/// The weight of each channel in a <see cref="LoudnessMeter"/>'s sum, in the samples' channel order. BS.1770-4 and
/// FFmpeg's <c>ebur128</c>: 0 for the LFE channels, 1.41 for the side and back ones (surround), 1 for the rest.
/// </summary>
public sealed class ChannelWeights
{
    // FFmpeg's AVChannel numbers (libavutil/channel_layout.h): a layout's channels come in this order.
    const int Lfe = 3, Lfe2 = 35;

    // ebur128's BACK_MASK: back left/centre/right, top back left/centre/right, side left/right, surround direct left/right.
    const ulong BackMask = (1UL << 4) | (1UL << 8) | (1UL << 5) | (1UL << 15) | (1UL << 16) | (1UL << 17) | (1UL << 9) | (1UL << 10) | (1UL << 33) | (1UL << 34);

    // FFmpeg's named layouts (channel_layout_map, in its order: av_channel_layout_default takes the first of a count).
    static readonly (string Name, ulong Mask)[] Layouts = BuildLayouts();

    readonly double[] _weights;

    /// <summary>Weights of your own, one per channel in the samples' order.</summary>
    /// <param name="weights">The weights.</param>
    /// <exception cref="ArgumentException">No weights, or one that is negative or not finite.</exception>
    public ChannelWeights(IEnumerable<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _weights = [.. weights];
        if (_weights.Length == 0)
            throw new ArgumentException("There are no weights.", nameof(weights));
        if (_weights.Any(w => !double.IsFinite(w) || w < 0))
            throw new ArgumentException("A weight is negative or not finite.", nameof(weights));
    }

    ChannelWeights(double[] weights) => _weights = weights;

    /// <summary>The weights, in the samples' channel order.</summary>
    public IReadOnlyList<double> Weights => _weights;

    /// <summary>
    /// ebur128's weights for FFmpeg's default layout of <paramref name="channels"/> channels (what
    /// <see cref="AudioReader"/> gives a stream without a layout): mono, stereo, 2.1, 4.0, 5.0, 5.1 (FL FR FC LFE BL BR),
    /// 6.1, 7.1 (FL FR FC LFE BL BR SL SR), 5.1.4, 7.1.4, 9.1.4, 9.1.6, 22.2; 1 for every channel of any other count.
    /// </summary>
    /// <param name="channels">The channel count.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> isn't positive.</exception>
    public static ChannelWeights Default(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        foreach (var (_, mask) in Layouts)
        {
            if (ulong.PopCount(mask) == (ulong)channels)
                return FromMask(mask);
        }

        return new ChannelWeights(Enumerable.Repeat(1.0, channels).ToArray());
    }

    /// <summary>
    /// ebur128's weights for a layout as FFmpeg describes it (<see cref="StreamInfo.ChannelLayout"/>): one of its named
    /// layouts (stereo, 5.1(side), 7.1, 5.1.4, ...), any other layout's channels by name in the samples' order
    /// (<c>5 channels (FL+FR+LFE+BL+BR)</c>, as it describes them, or the names alone; a channel's <c>@label</c> is
    /// ignored), or <c>N channels</c> for channels without positions (each weighted 1, as ebur128 weighs them).
    /// </summary>
    /// <param name="layout">FFmpeg's description of the layout.</param>
    /// <returns>The weights; null for a name FFmpeg's standard layouts don't have, or a channel name it doesn't know.</returns>
    public static ChannelWeights? ForLayout(string layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var (name, mask) in Layouts)
        {
            if (name == layout)
                return FromMask(mask);
        }

        // av_channel_layout_describe's other forms: "3 channels" (unspecified order), "5 channels (FL+FR+LFE+BL+BR)"
        // (each channel by name); the names alone too.
        string names = layout.Trim();
        int count = -1;
        int space = names.IndexOf(" channels", StringComparison.Ordinal);
        if (space > 0 && int.TryParse(names[..space], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
        {
            string rest = names[(space + " channels".Length)..];
            if (rest.Length == 0)
                return n > 0 ? new ChannelWeights(Enumerable.Repeat(1.0, n).ToArray()) : null;
            if (!rest.StartsWith(" (", StringComparison.Ordinal) || !rest.EndsWith(')'))
                return null;
            (count, names) = (n, rest[2..^1]);
        }

        var weights = new List<double>();
        foreach (string part in names.Split('+'))
        {
            string channel = part.Split('@')[0];
            int id = Array.IndexOf(ChannelNames, channel);
            if (channel.Length == 0 || id < 0)
                return null;
            weights.Add(Weight(id));
        }

        return count < 0 || count == weights.Count ? new ChannelWeights([.. weights]) : null;
    }

    // FFmpeg's channel names by AVChannel number (libavutil/channel_layout.c's channel_names; null where none).
    static readonly string?[] ChannelNames = BuildNames();

    static string?[] BuildNames()
    {
        var names = new string?[63];
        string[] first = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR"];
        first.CopyTo(names, 0);
        string[] later = ["DL", "DR", "WL", "WR", "SDL", "SDR", "LFE2", "TSL", "TSR", "BFC", "BFL", "BFR", "SSL", "SSR", "TTL", "TTR"];
        later.CopyTo(names, 29);
        (names[61], names[62]) = ("BIL", "BIR");
        return names;
    }

    // ebur128's weight of a channel: 0 for the LFEs, 1.41 for those in its BACK_MASK, 1 for the rest.
    static double Weight(int channel) => channel is Lfe or Lfe2 ? 0 : (BackMask & (1UL << channel)) != 0 ? 1.41 : 1;

    static ChannelWeights FromMask(ulong mask)
    {
        var weights = new List<double>();
        for (int c = 0; c < 64; c++)
        {
            if ((mask & (1UL << c)) == 0)
                continue;
            weights.Add(Weight(c));
        }

        return new ChannelWeights([.. weights]);
    }

    static (string, ulong)[] BuildLayouts()
    {
        static ulong M(params int[] channels) => channels.Aggregate(0UL, (m, c) => m | (1UL << c));
        const int FL = 0, FR = 1, FC = 2, LFE = 3, BL = 4, BR = 5, FLC = 6, FRC = 7, BC = 8, SL = 9, SR = 10, TC = 11;
        const int TFL = 12, TFC = 13, TFR = 14, TBL = 15, TBC = 16, TBR = 17, StereoL = 29, StereoR = 30, WL = 31, WR = 32;
        const int LFE2 = 35, TSL = 36, TSR = 37, BFC = 38, BFL = 39, BFR = 40, BinauralL = 61, BinauralR = 62;
        ulong stereo = M(FL, FR), surround = stereo | M(FC), p31 = surround | M(LFE), p40 = surround | M(BC);
        ulong p22 = stereo | M(SL, SR), quad = stereo | M(BL, BR), p50 = surround | M(SL, SR), p51 = p50 | M(LFE);
        ulong p50b = surround | M(BL, BR), p51b = p50b | M(LFE), p71 = p51 | M(BL, BR), p512 = p51 | M(TFL, TFR);
        ulong p712 = p71 | M(TFL, TFR), p714 = p712 | M(TBL, TBR), p914 = p714 | M(FLC, FRC), p916 = p914 | M(TSL, TSR);
        ulong octagonal = p50 | M(BL, BC, BR), p60f = p22 | M(FLC, FRC);
        return
        [
            ("mono", M(FC)), ("stereo", stereo), ("2.1", stereo | M(LFE)), ("3.0", surround), ("3.0(back)", stereo | M(BC)),
            ("4.0", p40), ("quad", quad), ("quad(side)", p22), ("3.1", p31), ("5.0", p50b), ("5.0(side)", p50),
            ("4.1", p40 | M(LFE)), ("5.1", p51b), ("5.1(side)", p51), ("6.0", p50 | M(BC)), ("6.0(front)", p60f),
            ("3.1.2", p31 | M(TFL, TFR)), ("hexagonal", p50b | M(BC)), ("6.1", p51 | M(BC)), ("6.1(back)", p51b | M(BC)),
            ("6.1(front)", p60f | M(LFE)), ("7.0", p50 | M(BL, BR)), ("7.0(front)", p50 | M(FLC, FRC)), ("7.1", p71),
            ("7.1(wide)", p51b | M(FLC, FRC)), ("7.1(wide-side)", p51 | M(FLC, FRC)), ("5.1.2", p512),
            ("5.1.2(back)", p51b | M(TFL, TFR)), ("octagonal", octagonal), ("cube", quad | M(TFL, TFR, TBL, TBR)),
            ("5.1.4", p512 | M(TBL, TBR)), ("7.1.2", p712), ("7.1.4", p714), ("7.2.3", p712 | M(TBC, LFE2)),
            ("9.1.4", p914), ("9.1.6", p916),
            ("hexadecagonal", octagonal | M(WL, WR, TBL, TBR, TBC, TFC, TFL, TFR)), ("binaural", M(BinauralL, BinauralR)),
            ("downmix", M(StereoL, StereoR)), ("22.2", p916 | M(BC, LFE2, TFC, TC, TBC, BFC, BFL, BFR)),
        ];
    }
}
