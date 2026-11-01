using System.Buffers.Binary;
using System.Globalization;

namespace FrameReader;

/// <summary>
/// The picture's measures of a run's frames (<see cref="PictureMeter"/>), by column, from <see cref="FirstFrame"/>, and
/// FFmpeg's figures from them: <c>signalstats</c>' (<see cref="YAvg"/>, <see cref="Brng"/>), <c>blackdetect</c>'s
/// (<see cref="BlackSpans(double, double)"/>), <c>cropdetect</c>'s (<see cref="Crop"/>, <see cref="FrameCrop"/>), and the colour bars
/// (<see cref="BarsSpans"/>); and each frame's picture type (<see cref="Type"/>, <see cref="Types"/>). The ranges of a split run join (<see cref="Join"/>) into the whole run's, byte for byte.
/// </summary>
public sealed class PictureMeasures
{
    const uint Magic = 0x4D505246; // "FRPM": FrameReader's picture measures, version 1
    const uint MagicThreshold = 0x32505246; // "FRP2": version 1 with the black pixel threshold after the header

    /// <summary>blackdetect's default pixel_black_th, the threshold of version 1's measures.</summary>
    internal const double DefaultBlackPixelThreshold = 0.10;

    readonly List<long> _pts = [];
    readonly List<ushort> _min = [], _max = [];
    readonly List<long> _sum = [];
    readonly List<int> _black = [], _brng = [];
    readonly List<short> _top = [], _bottom = [], _left = [], _right = [];
    readonly List<bool> _bars = [];
    readonly List<PictureType> _type = [];

    internal PictureMeasures(int width, int height, bool fullRange, int bitDepth, Rational timeBase, int firstFrame, double blackPixelThreshold)
    {
        BlackPixelThreshold = blackPixelThreshold;
        Width = width;
        Height = height;
        FullRange = fullRange;
        BitDepth = bitDepth;
        TimeBase = timeBase;
        FirstFrame = firstFrame;
    }

    /// <summary>The upright picture's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The upright picture's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Whether the samples are full range (<see cref="PictureFormat.FullRange"/>).</summary>
    public bool FullRange { get; }

    /// <summary>Bits per sample (<see cref="PictureFormat.BitDepth"/>): YMIN, YAVG and YMAX are at this depth, as signalstats gives them.</summary>
    public int BitDepth { get; }

    /// <summary>The frames' pts' time base (<see cref="PictureFormat.TimeBase"/>).</summary>
    public Rational TimeBase { get; }

    /// <summary>The first frame's index in the video (display order).</summary>
    public int FirstFrame { get; }

    /// <summary>
    /// blackdetect's pixel_black_th the black pixels were counted with (<see cref="PictureMeter"/>'s; 0.10, FFmpeg's
    /// default, unless the meter was given another).
    /// </summary>
    public double BlackPixelThreshold { get; }

    /// <summary>How many frames, from <see cref="FirstFrame"/>.</summary>
    public int Count => _pts.Count;

    int Pixels => Width * Height;

    internal void Add(long pts,
        int min,
        int max,
        long sum,
        int black,
        int brng,
        short top,
        short bottom,
        short left,
        short right,
        bool bars,
        PictureType type)
    {
        _pts.Add(pts);
        _min.Add((ushort)min);
        _max.Add((ushort)max);
        _sum.Add(sum);
        _black.Add(black);
        _brng.Add(brng);
        _top.Add(top);
        _bottom.Add(bottom);
        _left.Add(left);
        _right.Add(right);
        _bars.Add(bars);
        _type.Add(type);
    }

    /// <summary>Frame <paramref name="i"/>'s pts, in <see cref="TimeBase"/> units.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public long Pts(int i) => _pts[i];

    /// <summary>signalstats' YMIN.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public int YMin(int i) => _min[i];

    /// <summary>signalstats' YMAX.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public int YMax(int i) => _max[i];

    /// <summary>signalstats' YAVG: the luma's mean.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public double YAvg(int i) => 1.0 * _sum[i] / Pixels;

    /// <summary>signalstats' BRNG: the share of pixels out of broadcast range, luma 16 to 235 or chroma 16 to 240 (scaled to the depth).</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public double Brng(int i) => 1.0 * _brng[i] / Pixels;

    /// <summary>The pixels BRNG counts.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public int BrngPixels(int i) => _brng[i];

    /// <summary>blackdetect's: at least 98% of the pixels at or under its threshold.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public bool IsBlack(int i) => IsBlack(i, 0.98);

    /// <summary>blackdetect's with <c>pic_th=<paramref name="pictureThreshold"/></c>: that share of the pixels or more at or under its threshold.</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    /// <param name="pictureThreshold">The share of black pixels that makes a frame black (picture_black_ratio_th, 0 to 1).</param>
    public bool IsBlack(int i, double pictureThreshold) => (double)_black[i] / Pixels >= pictureThreshold;

    /// <summary>The black pixels blackdetect counts in frame <paramref name="i"/> (at or under <see cref="BlackPixelThreshold"/>).</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public int BlackPixels(int i) => _black[i];

    /// <summary>Colour bars (<see cref="PictureMeter"/>, by their definition).</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public bool IsBars(int i) => _bars[i];

    /// <summary>The frame's picture type as the decoder says it (FFmpeg's <c>pict_type</c>).</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public PictureType Type(int i) => _type[i];

    /// <summary>
    /// Every frame's picture type, one character each, as FFmpeg's <c>av_get_picture_type_char</c> writes it (ffprobe's
    /// <c>pict_type</c>): I, P, B, S, i (SI), p (SP), b (BI), ? (none).
    /// </summary>
    public string Types() => string.Create(Count, _type, static (chars, types) =>
    {
        for (int i = 0; i < chars.Length; i++)
            chars[i] = TypeChar(types[i]);
    });

    /// <summary>FFmpeg's <c>av_get_picture_type_char</c>.</summary>
    public static char TypeChar(PictureType type) => type switch
    {
        PictureType.I => 'I',
        PictureType.P => 'P',
        PictureType.B => 'B',
        PictureType.S => 'S',
        PictureType.SI => 'i',
        PictureType.SP => 'p',
        PictureType.BI => 'b',
        _ => '?',
    };

    /// <summary>
    /// blackdetect's spans with <c>d=<paramref name="minSeconds"/></c>: from a black frame's pts to the first frame
    /// after that isn't black, or at the end the last frame's pts (FFmpeg's, a FIXME there), kept when that lasts at
    /// least <paramref name="minSeconds"/> (in the time base, rounded down, as blackdetect).
    /// </summary>
    public IReadOnlyList<FrameSpan> BlackSpans(double minSeconds = 2) => BlackSpans(minSeconds, 0.98);

    /// <summary>
    /// <see cref="BlackSpans(double)"/> with <c>pic_th=<paramref name="pictureThreshold"/></c> (the pixel threshold is
    /// the meter's, <see cref="BlackPixelThreshold"/>).
    /// </summary>
    /// <param name="minSeconds">blackdetect's d: the shortest span kept, in seconds.</param>
    /// <param name="pictureThreshold">blackdetect's pic_th (picture_black_ratio_th): the share of black pixels that makes a frame black.</param>
    public IReadOnlyList<FrameSpan> BlackSpans(double minSeconds, double pictureThreshold)
    {
        long least = (long)(minSeconds / (TimeBase.Num / (double)TimeBase.Den));
        return Spans(i => IsBlack(i, pictureThreshold), endAtLast: true).Where(s => s.EndPts - s.StartPts >= least).ToArray();
    }

    /// <summary>The colour bars' spans: from a bars frame to the first frame after that isn't (the end at the file's end).</summary>
    public IReadOnlyList<FrameSpan> BarsSpans() => Spans(IsBars, endAtLast: false);

    List<FrameSpan> Spans(Func<int, bool> on, bool endAtLast)
    {
        var spans = new List<FrameSpan>();
        int start = -1;
        for (int i = 0; i < Count; i++)
        {
            if (on(i) && start < 0)
            {
                start = i;
            }
            else if (!on(i) && start >= 0)
            {
                spans.Add(new FrameSpan(FirstFrame + start, FirstFrame + i, _pts[start], _pts[i]));
                start = -1;
            }
        }

        if (start >= 0)
        {
            int last = Count - 1;
            spans.Add(endAtLast
                ? new FrameSpan(FirstFrame + start, FirstFrame + last, _pts[start], _pts[last])
                : new FrameSpan(FirstFrame + start, FirstFrame + Count, _pts[start], _pts[last]));
        }

        return spans;
    }

    /// <summary>cropdetect's figures for frame <paramref name="i"/> alone (<c>reset=1:skip=0</c>).</summary>
    /// <param name="i">The frame, counted from <see cref="FirstFrame"/>.</param>
    public CropArea FrameCrop(int i)
    {
        var state = CropState.Start(Width, Height);
        state.Scan(_top[i], _bottom[i], _left[i], _right[i]);
        return state.Area();
    }

    /// <summary>
    /// cropdetect's figures with its defaults (<c>limit=24/255:round=16:skip=2:reset=0</c>) after the last frame: the
    /// area holding every frame's content but the first two; null for three frames or fewer, or for a range not from frame 0.
    /// </summary>
    public CropArea? Crop()
    {
        const int skip = 2;
        if (FirstFrame != 0 || Count <= skip)
            return null;
        var state = CropState.Start(Width, Height);
        for (int i = skip; i < Count; i++)
            state.Scan(_top[i], _bottom[i], _left[i], _right[i]);
        return state.Area();
    }

    /// <summary>The first <paramref name="count"/> frames' measures (a run's, cut to the frames its result read).</summary>
    public PictureMeasures Take(int count)
    {
        var taken = new PictureMeasures(Width, Height, FullRange, BitDepth, TimeBase, FirstFrame, BlackPixelThreshold);
        for (int i = 0; i < Math.Min(count, Count); i++)
            taken.Add(_pts[i], _min[i], _max[i], _sum[i], _black[i], _brng[i], _top[i], _bottom[i], _left[i], _right[i], _bars[i], _type[i]);
        return taken;
    }

    /// <summary>
    /// Ranges' measures joined from the first frame on, while each starts where the one before ends (a range stopped
    /// short ends them); null when none starts at frame 0.
    /// </summary>
    public static PictureMeasures? Join(IEnumerable<PictureMeasures> ranges)
    {
        PictureMeasures? joined = null;
        foreach (var range in ranges.OrderBy(r => r.FirstFrame))
        {
            if (joined is null)
            {
                if (range.FirstFrame != 0)
                    return null;
                joined = range.Take(range.Count);
                continue;
            }

            if (range.FirstFrame != joined.Count
                || (range.Width, range.Height, range.FullRange, range.BitDepth, range.BlackPixelThreshold) != (joined.Width, joined.Height, joined.FullRange, joined.BitDepth, joined.BlackPixelThreshold))
            {
                break;
            }

            for (int i = 0; i < range.Count; i++)
            {
                joined.Add(range._pts[i],
                    range._min[i],
                    range._max[i],
                    range._sum[i],
                    range._black[i],
                    range._brng[i],
                    range._top[i],
                    range._bottom[i],
                    range._left[i],
                    range._right[i],
                    range._bars[i],
                    range._type[i]);
            }
        }

        return joined;
    }

    /// <summary>
    /// The measures as bytes, for a range's worker to hand to the one that joins them, checked by a hash. At the default
    /// pixel threshold the same bytes as before it could be set (version 1).
    /// </summary>
    public byte[] ToBytes()
    {
        const int header = (4 * 4) + 1 + 1 + (4 * 2) + 4;
        const int perFrame = 8 + 2 + 2 + 8 + 4 + 4 + (2 * 4) + 1 + 1;
        bool threshold = BlackPixelThreshold != DefaultBlackPixelThreshold;
        var bytes = new byte[header + (threshold ? 8 : 0) + perFrame * Count + 8];
        var w = new Writer(bytes);
        w.UInt(threshold ? MagicThreshold : Magic);
        if (threshold)
            w.Long(BitConverter.DoubleToInt64Bits(BlackPixelThreshold));
        w.Int(Width);
        w.Int(Height);
        w.Int(FirstFrame);
        w.Byte(FullRange ? (byte)1 : (byte)0);
        w.Byte((byte)BitDepth);
        w.Int(TimeBase.Num);
        w.Int(TimeBase.Den);
        w.Int(Count);
        for (int i = 0; i < Count; i++)
        {
            w.Long(_pts[i]);
            w.UShort(_min[i]);
            w.UShort(_max[i]);
            w.Long(_sum[i]);
            w.Int(_black[i]);
            w.Int(_brng[i]);
            w.Short(_top[i]);
            w.Short(_bottom[i]);
            w.Short(_left[i]);
            w.Short(_right[i]);
            w.Byte(_bars[i] ? (byte)1 : (byte)0);
            w.Byte((byte)_type[i]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(w.At), Hash(bytes.AsSpan(0, w.At)));
        return bytes;
    }

    /// <summary><see cref="ToBytes"/>' measures back.</summary>
    /// <exception cref="InvalidDataException">Not such bytes, or damaged.</exception>
    public static PictureMeasures FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || Hash(bytes[..^8]) != BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]))
            throw new InvalidDataException("The picture's measures are damaged (their hash differs).");
        var r = new Reader(bytes[..^8]);
        uint magic = r.UInt();
        if (magic is not (Magic or MagicThreshold))
            throw new InvalidDataException("These aren't the picture's measures.");
        double threshold = magic == MagicThreshold ? BitConverter.Int64BitsToDouble(r.Long()) : DefaultBlackPixelThreshold;
        int width = r.Int(), height = r.Int(), first = r.Int();
        bool full = r.Byte() != 0;
        int depth = r.Byte();
        var timeBase = new Rational(r.Int(), r.Int());
        var measures = new PictureMeasures(width, height, full, depth, timeBase, first, threshold);
        int count = r.Int();
        for (int i = 0; i < count; i++)
        {
            measures.Add(r.Long(),
                r.UShort(),
                r.UShort(),
                r.Long(),
                r.Int(),
                r.Int(),
                r.Short(),
                r.Short(),
                r.Short(),
                r.Short(),
                r.Byte() != 0,
                (PictureType)r.Byte());
        }

        return measures;
    }

    // FNV-1a, 64 bits: enough to tell damaged bytes from a worker (no package for it).
    static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in bytes)
            hash = (hash ^ b) * 1099511628211;
        return hash;
    }

    ref struct Writer(Span<byte> bytes)
    {
        readonly Span<byte> _bytes = bytes;

        public int At { get; private set; }

        public void Byte(byte v) => _bytes[At++] = v;

        public void Short(short v) => Put(v, 2, BinaryPrimitives.WriteInt16LittleEndian);

        public void UShort(ushort v) => Put(v, 2, BinaryPrimitives.WriteUInt16LittleEndian);

        public void Int(int v) => Put(v, 4, BinaryPrimitives.WriteInt32LittleEndian);

        public void UInt(uint v) => Put(v, 4, BinaryPrimitives.WriteUInt32LittleEndian);

        public void Long(long v) => Put(v, 8, BinaryPrimitives.WriteInt64LittleEndian);

        void Put<T>(T v, int size, SpanWrite<T> write)
        {
            write(_bytes.Slice(At, size), v);
            At += size;
        }
    }

    delegate void SpanWrite<T>(Span<byte> span, T value);

    ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        readonly ReadOnlySpan<byte> _bytes = bytes;
        int _at;

        public byte Byte() => Take(1)[0];

        public short Short() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public ushort UShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        ReadOnlySpan<byte> Take(int n)
        {
            if (_at + n > _bytes.Length)
                throw new InvalidDataException("The picture's measures end early.");
            var span = _bytes.Slice(_at, n);
            _at += n;
            return span;
        }
    }

    /// <summary>cropdetect's state (x1, x2, y1, y2) and its scan of a frame, replayed from the frame's content lines.</summary>
    struct CropState
    {
        int _x1, _x2, _y1, _y2;

        public static CropState Start(int width, int height) =>
            new() { _x1 = width - 1, _y1 = height - 1, _x2 = 0, _y2 = 0 };

        // vf_cropdetect.c's FIND, in order: y1 from the top while under y1, y2 from the bottom while over max(y2, y1),
        // then x1 and x2 the same way.
        public void Scan(int top, int bottom, int left, int right)
        {
            if (top >= 0 && top < _y1)
                _y1 = top;
            if (bottom > Math.Max(_y2, _y1))
                _y2 = bottom;
            if (left >= 0 && left < _x1)
                _x1 = left;
            if (right > Math.Max(_x2, _x1))
                _x2 = right;
        }

        // Rounded as cropdetect does (round=16): x and y up to even, w and h down to a multiple of 16, centred.
        public readonly CropArea Area()
        {
            const int round = 16;
            int x = (_x1 + 1) & ~1, y = (_y1 + 1) & ~1;
            int w = _x2 - x + 1, h = _y2 - y + 1;
            int shrink = w % round;
            w -= shrink;
            x += (shrink / 2 + 1) & ~1;
            shrink = h % round;
            h -= shrink;
            y += (shrink / 2 + 1) & ~1;
            return new CropArea(_x1, _x2, _y1, _y2, w, h, x, y);
        }
    }
}

/// <summary>Frames <see cref="FirstFrame"/> up to <see cref="EndFrame"/> and their pts, as FFmpeg reports the span.</summary>
/// <param name="FirstFrame">The span's first frame (display order, in the video).</param>
/// <param name="EndFrame">The frame after it; blackdetect's span at the end ends at the last frame itself (<see cref="PictureMeasures.BlackSpans(double, double)"/>).</param>
/// <param name="StartPts">The first frame's pts.</param>
/// <param name="EndPts">The end frame's pts (blackdetect's black_end).</param>
public sealed record FrameSpan(int FirstFrame, int EndFrame, long StartPts, long EndPts);

/// <summary>cropdetect's figures: the content's first and last columns and rows, and the crop (<c>crop=w:h:x:y</c>).</summary>
/// <param name="X1">The first column with content (cropdetect's x1).</param>
/// <param name="X2">The last column with content (x2).</param>
/// <param name="Y1">The first row with content (y1).</param>
/// <param name="Y2">The last row with content (y2).</param>
/// <param name="W">The crop's width, rounded down to 16.</param>
/// <param name="H">The crop's height, rounded down to 16.</param>
/// <param name="X">The crop's left edge.</param>
/// <param name="Y">The crop's top edge.</param>
public sealed record CropArea(int X1, int X2, int Y1, int Y2, int W, int H, int X, int Y)
{
    /// <summary>cropdetect's suggestion as it prints it: <c>crop=w:h:x:y</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"crop={W}:{H}:{X}:{Y}");
}
