using System.Diagnostics.CodeAnalysis;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>A pixel format <see cref="VideoFrameReader"/> converts frames to.</summary>
public enum FrameFormat
{
    /// <summary>Packed 8-bit blue, green, red (3 bytes a pixel), as OpenCV holds images.</summary>
    Bgr24,

    /// <summary>Packed 8-bit red, green, blue (3 bytes a pixel).</summary>
    Rgb24,

    /// <summary>Packed 8-bit blue, green, red, alpha (4 bytes a pixel; alpha opaque).</summary>
    Bgra32,

    /// <summary>Packed 8-bit red, green, blue, alpha (4 bytes a pixel; alpha opaque).</summary>
    Rgba32,

    /// <summary>8-bit luma only (1 byte a pixel), in the source's range.</summary>
    Gray8,

    /// <summary>8-bit planar YUV 4:2:0: the Y plane, then U, then V (each half the size, rounded up), in the source's colour space and range.</summary>
    Yuv420p,
}

/// <summary>What a <see cref="VideoFrameReader"/> gives and how it decodes.</summary>
public sealed record VideoFrameReaderOptions
{
    /// <summary>Output width in pixels; null (with <see cref="Height"/> null): the frame's own (after any rotation).</summary>
    public int? Width { get; init; }

    /// <summary>Output height in pixels; null (with <see cref="Width"/> null): the frame's own.</summary>
    public int? Height { get; init; }

    /// <summary>
    /// The widest output in pixels, as ffmpeg's <c>scale='min(MaxWidth,iw)':-2</c>: a wider frame is scaled down to this
    /// width; a frame as wide or narrower keeps its width (never upscaled). The height follows the aspect ratio, rounded
    /// to the nearest even number (so an odd height becomes even even when the width stays, as with ffmpeg). Measured
    /// on the frame after any rotation (and on each frame, if the size changes mid-stream). Null: no cap. Not with
    /// <see cref="Width"/> and <see cref="Height"/>.
    /// </summary>
    public int? MaxWidth { get; init; }

    /// <summary>The output's pixel format.</summary>
    public FrameFormat FrameFormat { get; init; } = FrameFormat.Bgr24;

    /// <summary>
    /// Frames per second out, through FFmpeg's <c>fps</c> filter (its defaults: nearest-frame rounding, duplicating or
    /// dropping as needed): the frames <c>-vf fps=R,scale=...</c> gives, only those converted. 1 for a picture every
    /// second, 1/10 for one every ten seconds. Null: every frame as decoded. Needs libavfilter with fps.
    /// </summary>
    public Rational? FrameRate { get; init; }

    /// <summary>How to decode (library folder, threads, deinterlacing, input options); null: the defaults.</summary>
    public FrameDecoderOptions? Decoder { get; init; }

    /// <summary>
    /// Frames to decode ahead on a background thread while the caller works on the frame it has (encodes it, analyses
    /// it, uploads it): decoding (with deinterlacing and rotation) goes on between reads, with up to this many decoded
    /// frames waiting, each a decoded frame's memory (about 3 MB at 1080p 8-bit 4:2:0). The frames, their tags and the
    /// errors are those 0 gives, in the same order. The conversion to <see cref="FrameFormat"/> stays in the read (the
    /// frame <see cref="VideoFrameReader.TryReadForwardTo"/> gives isn't known before it is asked for), so a frame is
    /// valid until the next read as without. A Stream input is read on that thread; with row-wise deinterlacing every
    /// frame is deinterlaced, also those <see cref="VideoFrameReader.TryReadForwardTo"/> passes over. It pays with spare
    /// cores (decoding doesn't keep them all busy): 4 hides the work between reads as far as decoding allows; 1 or 2 hand
    /// over nearly every frame on its own, which can cost more than it saves where every core decodes. Dispose waits for
    /// the frame being decoded. 0: decode in the read.
    /// </summary>
    public int ReadAhead { get; init; }

    /// <summary>Decode every packet, also those of frames before the one wanted that nothing references (tests compare).</summary>
    internal bool DecodeEveryPacket { get; init; }
}

/// <summary>A decoded, converted frame: valid until the next read from its <see cref="VideoFrameReader"/>.</summary>
public readonly ref struct VideoFrame
{
    internal VideoFrame(int index, long pts, TimeSpan time, int width, int height, FrameFormat format, ReadOnlySpan<byte> data, FrameTags tags)
    {
        (Index, Pts, Time, Width, Height, FrameFormat) = (index, pts, time, width, height, format);
        Data = data;
        Tags = tags;
    }

    /// <summary>
    /// How many distinct frames the reader had given before this one, since it opened or last seeked: 0, 1, 2, ...
    /// (the same frame given again keeps its number). Frames passed over aren't counted; <see cref="Time"/> says where it is.
    /// </summary>
    public int Index { get; }

    /// <summary>Its best-effort timestamp in the stream's time base (<see cref="VideoFrameReader.TimeBase"/>), as decoded.</summary>
    public long Pts { get; }

    /// <summary>Its time from the input's start, as ffmpeg's command line gives it (shifted by the container's start time).</summary>
    public TimeSpan Time { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel format of <see cref="Data"/>.</summary>
    public FrameFormat FrameFormat { get; }

    /// <summary>
    /// The pixels, packed without padding as ffmpeg's rawvideo output holds them (rows of Width × bytes per pixel; for
    /// <see cref="FrameFormat.Yuv420p"/> the three planes one after another). Copy it to keep it past the next read.
    /// </summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>
    /// The pixels' aspect ratio (width over height of one pixel) as ffmpeg's filters give it for this frame: the stream's
    /// (else the codec's), swapped by a rotation and adjusted by scaling to another shape (0/1: not known, taken as square).
    /// </summary>
    public Rational SampleAspectRatio => Tags.Sar.den == 0 ? new(0, 1) : new(Tags.Sar.num, Tags.Sar.den);

    /// <summary>FFmpeg's colour range name of <see cref="Data"/> (tv, pc); null if unspecified (taken as tv for YUV and gray).</summary>
    public string? ColorRange => Name(ffmpeg.av_color_range_name(Tags.Range), Tags.Range == AVColorRange.AVCOL_RANGE_UNSPECIFIED);

    /// <summary>FFmpeg's colour space (matrix) name of <see cref="Data"/> (bt709, gbr for RGB, ...); null if unspecified.</summary>
    public string? ColorSpace => Name(ffmpeg.av_color_space_name(Tags.Space), Tags.Space == AVColorSpace.AVCOL_SPC_UNSPECIFIED);

    /// <summary>FFmpeg's colour primaries name (bt709, ...), as the source's; null if unspecified.</summary>
    public string? ColorPrimaries => Name(ffmpeg.av_color_primaries_name(Tags.Primaries), Tags.Primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED);

    /// <summary>FFmpeg's transfer characteristic name (bt709, ...), as the source's; null if unspecified.</summary>
    public string? ColorTransfer => Name(ffmpeg.av_color_transfer_name(Tags.Transfer), Tags.Transfer == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED);

    /// <summary>The tags ffmpeg's frame carries at this point of its filters (what an <see cref="ImageEncoder"/> needs).</summary>
    internal FrameTags Tags { get; }

    static string? Name(string? name, bool unspecified) => unspecified ? null : name;
}

/// <summary>A converted frame's aspect ratio and colour tags, as FFmpeg holds them.</summary>
internal readonly record struct FrameTags(AVRational Sar,
    AVColorRange Range,
    AVColorSpace Space,
    AVColorPrimaries Primaries,
    AVColorTransferCharacteristic Transfer);

/// <summary>Receives a frame read for the time at <paramref name="index"/> in the list given (<see cref="VideoFrameReader.ReadAt"/>).</summary>
/// <param name="index">The time's position in the list.</param>
/// <param name="frame">The frame, valid only during the call.</param>
public delegate void FrameHandler(int index, VideoFrame frame);

/// <summary>
/// Reads a video's frames at a chosen size and pixel format, in this process through FFmpeg's libraries. The pixels
/// are the bytes ffmpeg's command line gives for <c>-vf scale=W:H:flags=bicubic,format=F -f rawvideo</c> (swscale with
/// the frame's colour tags), so results can be checked against it. No allocations per frame: one output buffer, reused.
/// With <see cref="VideoFrameReaderOptions.ReadAhead"/>, decoding runs ahead on a thread of its own. The TryTake
/// methods give the same frames decoded and not converted (<see cref="DecodedFrame"/>), for <see cref="ImageEncoder"/>'s
/// ffmpeg-exact path.
/// </summary>
public sealed unsafe class VideoFrameReader : IDisposable
{
    readonly FrameDecoder _decoder;
    readonly VideoFrameReaderOptions _options;
    readonly AVPixelFormat _format;
    readonly ReadAhead? _ahead; // ReadAhead: the decoding thread and its frames; null: decoding in the read
    SwsContext* _sws;
    AVFrame* _in, _out;
    byte[] _packed = [];
    int _index = -1;
    long? _keepFrom; // after a seek: frames whose pts plus this is below 0 are dropped (ffmpeg's trim)
    TimeSpan? _lastTarget; // the time read forward to last (since the last seek), and the step to it: the next one's guess
    TimeSpan _lastStep, _shortestStep;
    int _equalSteps; // steps in a row as long as the last (within a millisecond)
    int _stepsToGuess = 2; // how many it takes to guess: doubled by each wrong guess
    bool _guessed; // packets may have been left out on a guess since the last seek

    // The frame read last's timestamp, as ffmpeg's command line gives it: one without (the last frame MPEG-PS and AVI
    // flush out) gets the previous one's plus that one's estimated duration.
    long _pts, _lastPts = ffmpeg.AV_NOPTS_VALUE, _lastDuration;

    // FrameRate: buffer -> fps -> buffersink, fed the decoded frames, its output converted.
    AVFilterGraph* _fpsGraph;
    AVFilterContext* _fpsIn, _fpsOut;
    AVFrame* _fpsFrame;
    bool _fpsFlushed;

    /// <summary>Opens a file, image sequence pattern or URL FFmpeg can read.</summary>
    /// <param name="path">What to open.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    /// <exception cref="FrameReaderException">The libraries didn't load, or the input can't be opened or has no video.</exception>
    public VideoFrameReader(string path, VideoFrameReaderOptions? options = null) : this(options, o => new FrameDecoder(path, o))
    {
    }

    /// <summary>Opens a stream's bytes, read once (headers first: mkv, webm, ts, mov or a faststart mp4).</summary>
    /// <param name="stream">The media's bytes; not disposed.</param>
    /// <param name="options">Size, format and decoding; null: the frames as they are, in Bgr24.</param>
    public VideoFrameReader(Stream stream, VideoFrameReaderOptions? options = null) : this(options, o => new FrameDecoder(stream, o))
    {
    }

    VideoFrameReader(VideoFrameReaderOptions? options, Func<FrameDecoderOptions?, FrameDecoder> open)
    {
        _options = options ?? new VideoFrameReaderOptions();
        if (_options.Width is null != _options.Height is null || _options.Width <= 0 || _options.Height <= 0)
            throw new ArgumentException("Width and Height are both set (positive) or both left out.", nameof(options));
        if (_options.MaxWidth is { } max && (max <= 0 || _options.Width is not null))
            throw new ArgumentException("MaxWidth is positive and not set with Width and Height.", nameof(options));
        _format = PixelFormat(_options.FrameFormat) is var f && f != AVPixelFormat.AV_PIX_FMT_NONE
            ? f
            : throw new ArgumentException($"Unknown format {_options.FrameFormat}.", nameof(options));
        if (_options.FrameRate is { } r && (r.Num <= 0 || r.Den <= 0))
            throw new ArgumentException("FrameRate is positive.", nameof(options));
        if (_options.ReadAhead < 0)
            throw new ArgumentException("ReadAhead is 0 or more.", nameof(options));
        _decoder = open(_options.Decoder);
        if (_options.FrameRate is null && !_options.DecodeEveryPacket)
            _decoder.SkipUnreferenced();
        if (_options.ReadAhead > 0)
            _ahead = new ReadAhead(_decoder, _options.ReadAhead);
    }

    /// <summary>FFmpeg's pixel format for a <see cref="FrameFormat"/>; NONE for an unknown one.</summary>
    internal static AVPixelFormat PixelFormat(FrameFormat format) => format switch
    {
        FrameFormat.Bgr24 => AVPixelFormat.AV_PIX_FMT_BGR24,
        FrameFormat.Rgb24 => AVPixelFormat.AV_PIX_FMT_RGB24,
        FrameFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
        FrameFormat.Rgba32 => AVPixelFormat.AV_PIX_FMT_RGBA,
        FrameFormat.Gray8 => AVPixelFormat.AV_PIX_FMT_GRAY8,
        FrameFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
        _ => AVPixelFormat.AV_PIX_FMT_NONE,
    };

    /// <summary>The stream's time base in seconds per <see cref="VideoFrame.Pts"/> unit.</summary>
    public Rational TimeBase => _decoder.TimeBase;

    /// <summary>
    /// Seeks so the next <see cref="TryRead"/> gives the frame ffmpeg's <c>-ss</c> gives for <paramref name="time"/>:
    /// the first frame at or after it (from the keyframe before it, decoding forward and dropping the frames before,
    /// as ffmpeg's accurate seek does). <see cref="VideoFrame.Index"/> counts again from 0. With deinterlacing, only
    /// before the first read. In open GOPs the B-frames just after the keyframe it lands on decode without their reference,
    /// as with ffmpeg; <see cref="TryReadForwardTo"/> gives the true frames.
    /// </summary>
    /// <param name="time">From the input's start (as <see cref="VideoFrame.Time"/>).</param>
    public void Seek(TimeSpan time)
    {
        NoFrameRate(nameof(Seek));
        _index = -1;
        (_lastTarget, _guessed) = (null, false); // no steps to guess the next time by
        if (_decoder.TimestampsUnreliable)
        {
            // A raw stream: ffmpeg's -ss can't seek it (its frames lose their timestamps, so it lands on some keyframe
            // and keeps every frame from there, or finds none at all), so decode forward from the start instead, to the
            // frame a full decode has at the time. Forward of the frame read last, on from there.
            long keep = _decoder.TrimOffset(time);
            if (!(_current && Pts + keep < 0) && _decoder.Started)
            {
                _ahead?.Clear();
                _decoder.Rewind();
                _lastPts = ffmpeg.AV_NOPTS_VALUE; // timed from the start again
            }

            _keepFrom = keep;
            _decoder.SkipBefore = -keep;
            _current = false;
            return;
        }

        _ahead?.Clear(); // the frames read ahead are the old position's
        _keepFrom = _decoder.SeekLikeFfmpeg(time);
        _lastPts = ffmpeg.AV_NOPTS_VALUE; // as ffmpeg's decoder after an input -ss: no frame before
        _decoder.SkipBefore = -_keepFrom.Value; // the frames the trim drops: those nothing references needn't decode
        _current = false;
    }

    /// <summary>The frame at <paramref name="time"/> (<see cref="Seek"/>, then <see cref="TryRead"/>): a thumbnail.</summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <returns>False when there's no frame at or after the time.</returns>
    public bool TryReadAt(TimeSpan time, out VideoFrame frame)
    {
        Seek(time);
        return TryRead(out frame);
    }

    /// <summary>
    /// The frames at <paramref name="times"/> (ascending), as <see cref="TryReadForwardTo"/> gives them, read by
    /// <paramref name="parallelism"/> readers at once, each taking a contiguous share of the times (and of the decoder
    /// threads): the same frames in less wall time when there are spare cores. <paramref name="handler"/> is called from
    /// several threads at once; in time order within each share.
    /// </summary>
    /// <param name="path">A file (a Stream can't be read by several readers).</param>
    /// <param name="times">From the input's start, ascending.</param>
    /// <param name="options">Size, format and decoding; not <see cref="VideoFrameReaderOptions.FrameRate"/>.</param>
    /// <param name="parallelism">Readers at once; null: half the cores, at most 4.</param>
    /// <param name="handler">Called for each frame found, with its time's index.</param>
    /// <param name="cancellationToken">Stops the readers between frames (<see cref="OperationCanceledException"/>).</param>
    /// <returns>How many times had a frame (the rest are past the end).</returns>
    public static int ReadAt(string path,
        IReadOnlyList<TimeSpan> times,
        VideoFrameReaderOptions? options,
        int? parallelism,
        FrameHandler handler,
        CancellationToken cancellationToken = default)
    {
        options ??= new VideoFrameReaderOptions();
        if (options.FrameRate is not null)
            throw new ArgumentException("ReadAt reads frames at times; FrameRate isn't available.", nameof(options));
        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] < times[i - 1])
                throw new ArgumentException("The times are in ascending order.", nameof(times));
        }

        int cores = Environment.ProcessorCount;
        int workers = Math.Clamp(parallelism ?? Math.Min(4, cores / 2), 1, Math.Max(1, times.Count));
        var decoder = options.Decoder ?? new FrameDecoderOptions();
        if (workers > 1 && decoder.Threads == 0)
            options = options with { Decoder = decoder with { Threads = Math.Max(1, cores / workers) } };
        int found = 0;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, w =>
        {
            int from = w * times.Count / workers, to = (w + 1) * times.Count / workers;
            using var reader = new VideoFrameReader(path, options);
            for (int i = from; i < to; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.AdvanceTo(times[i], i + 1 < to ? times[i + 1] : null))
                    break;
                reader.Convert(out var frame);
                handler(i, frame);
                Interlocked.Increment(ref found);
            }
        });
        return found;
    }

    /// <summary>Decodes and converts the next frame; false at the end.</summary>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <exception cref="FrameReaderException">Decoding or converting failed.</exception>
    public bool TryRead(out VideoFrame frame)
    {
        if (_options.FrameRate is { } rate)
            return TryReadFps(rate, out frame);
        if (!Advance())
        {
            frame = default;
            return false;
        }

        Convert(out frame);
        return true;
    }

    /// <summary>
    /// The next frame as <see cref="TryRead"/> positions it, decoded and not converted: the frame the decoder gives
    /// (deinterlaced and autorotated as <see cref="VideoFrameReaderOptions.Decoder"/> says), at its own size and pixel
    /// format, for <see cref="ImageEncoder.Encode(DecodedFrame, ImageFilterOptions?)"/>'s ffmpeg-exact graph. The
    /// conversion's options (<see cref="VideoFrameReaderOptions.Width"/>, <see cref="VideoFrameReaderOptions.Height"/>,
    /// <see cref="VideoFrameReaderOptions.MaxWidth"/>, <see cref="VideoFrameReaderOptions.FrameFormat"/>) don't apply
    /// to it, so reads and takes mix on one reader: each moves the same position (and counts in
    /// <see cref="VideoFrame.Index"/>). A reference to the decoded buffers, no copy (with
    /// <see cref="VideoFrameReaderOptions.ReadAhead"/>, the queued frame's): the caller disposes it, and it stays valid
    /// after the next read and after the reader is disposed. With <see cref="VideoFrameReaderOptions.FrameRate"/>, the
    /// fps filter's next frame, as <see cref="TryRead"/> gives it there (its <see cref="DecodedFrame.Pts"/> in 1/FrameRate
    /// units, as <see cref="VideoFrame.Pts"/>), so each file is <c>ffmpeg -i X -vf fps=R[,...] out_%d.jpg</c>'s.
    /// </summary>
    /// <param name="frame">The frame, the caller's to dispose; null at the end.</param>
    /// <returns>False at the end.</returns>
    /// <exception cref="FrameReaderException">Decoding or the fps filter failed.</exception>
    public bool TryTake([NotNullWhen(true)] out DecodedFrame? frame)
    {
        if (_options.FrameRate is { } rate)
            frame = NextFps() ? TakeFps(rate) : null;
        else
            frame = Advance() ? Take() : null;
        return frame is not null;
    }

    /// <summary>
    /// The frame at <paramref name="time"/> as <see cref="TryReadAt"/> finds it (<see cref="Seek"/>, then
    /// <see cref="TryTake"/>), decoded and not converted: ffmpeg's <c>ffmpeg -ss T -i X</c> frame, for
    /// <see cref="ImageEncoder.Encode(DecodedFrame, ImageFilterOptions?)"/>.
    /// </summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, the caller's to dispose (see <see cref="TryTake"/>); null when there is none.</param>
    /// <returns>False when there's no frame at or after the time.</returns>
    public bool TryTakeAt(TimeSpan time, [NotNullWhen(true)] out DecodedFrame? frame)
    {
        NoFrameRate(nameof(TryTakeAt));
        Seek(time);
        return TryTake(out frame);
    }

    /// <summary>
    /// The frame <see cref="TryReadForwardTo"/> gives for <paramref name="time"/> (with its keyframe jumps; the frame
    /// given last again, as a new reference, when it is already at or after the time), decoded and not converted:
    /// ffmpeg's <c>ffmpeg -i X -ss T</c> frame, for <see cref="ImageEncoder.Encode(DecodedFrame, ImageFilterOptions?)"/>.
    /// </summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, the caller's to dispose (see <see cref="TryTake"/>); null when the video ends first.</param>
    /// <returns>False when the video ends first.</returns>
    public bool TryTakeForwardTo(TimeSpan time, [NotNullWhen(true)] out DecodedFrame? frame)
    {
        NoFrameRate(nameof(TryTakeForwardTo));
        frame = AdvanceTo(time) ? Take() : null;
        return frame is not null;
    }

    void NoFrameRate(string method)
    {
        if (_options.FrameRate is not null)
            throw new InvalidOperationException($"{method} isn't available with FrameRate.");
    }

    /// <summary>To the next frame (after a seek, the first one ffmpeg's trim keeps); false at the end.</summary>
    bool Advance()
    {
        if (_current && _guessed)
        {
            // Frames after this one may have been left out on a guess of the next time (TryReadForwardTo), also by the
            // decoding thread before this read: to the frame after it as a read forward, which reads again what it needs.
            _decoder.SkipBefore = Pts + 1;
            return Forward(-(Pts + 1), false, 0);
        }

        bool got;
        while ((got = Next()) && _keepFrom is { } keep && Pts + keep < 0)
        {
        }

        _current = got;
        if (got)
            _index++;
        return got;
    }

    /// <summary>
    /// The frame read last, as a reference the caller owns: taken from the decoder, or another of the queued one; timed as
    /// <see cref="TryRead"/> times it (ffmpeg's command line: a raw stream's frames counted from 0, a frame without a
    /// timestamp the one before's plus its duration).
    /// </summary>
    DecodedFrame Take()
    {
        var time = TimeSpan.FromSeconds((Pts + StartOffset) * _decoder.TimeBase.Value);
        if (_ahead is not null)
            return _ahead.Current.Frame.Reference(Pts, time);
        using var taken = _decoder.TakeFrame();
        return taken.Reference(Pts, time);
    }

    /// <summary>
    /// Reads forward to the first frame at or after <paramref name="time"/> (the frame a full decode has there, as
    /// ffmpeg's output-side <c>-ss</c> gives it), converting only that frame. When the time is past the next keyframe it
    /// jumps there instead of decoding the frames in between (from the keyframe before, so open GOPs decode right): a
    /// picture every second is one pass, a picture every few seconds or more decodes a fraction of the video. When the
    /// frame last read is already at or after the time, that frame again. Times behind it don't go back (use
    /// <see cref="Seek"/>). Frames before it that no other frame references (most B-frames of H.264, HEVC, MPEG-1/2 and
    /// MPEG-4 Part 2) stay out of the decoder, and in mp4, mov and mkv also those after it before the next time, guessed
    /// after steps of one length (what a guess too late left out is read again from the keyframe before): the frames given
    /// are those of decoding everything, byte for byte.
    /// </summary>
    /// <param name="time">From the input's start.</param>
    /// <param name="frame">The frame, valid until the next call.</param>
    /// <returns>False when the video ends first.</returns>
    public bool TryReadForwardTo(TimeSpan time, out VideoFrame frame)
    {
        NoFrameRate(nameof(TryReadForwardTo));
        if (!AdvanceTo(time))
        {
            frame = default;
            return false;
        }

        Convert(out frame);
        return true;
    }

    /// <summary>
    /// The next time read forward to, guessed after steps of one length (a picture every second): this one plus the
    /// shortest of them (a guess too early only leaves less out). Null until enough such steps in a row (since a seek or a
    /// wrong guess, which also doubles how many it takes), so a caller whose steps vary is soon no longer guessed for.
    /// </summary>
    TimeSpan? Guess(TimeSpan time)
    {
        var step = _lastTarget is { } last ? time - last : TimeSpan.Zero;
        if (step > TimeSpan.Zero && _equalSteps > 0 && (step - _lastStep).Duration() <= TimeSpan.FromMilliseconds(1))
            (_equalSteps, _shortestStep) = (_equalSteps + 1, step < _shortestStep ? step : _shortestStep);
        else
            (_equalSteps, _shortestStep) = (step > TimeSpan.Zero ? 1 : 0, step);
        (_lastTarget, _lastStep) = (time, step);
        return _equalSteps >= _stepsToGuess ? time + _shortestStep : null;
    }

    /// <summary>
    /// To the first frame at or after <paramref name="time"/> (<see cref="TryReadForwardTo"/>'s positioning); false at the end.
    /// <paramref name="next"/>: the time the next read forward asks for, when the caller knows it (<see cref="ReadAt"/>);
    /// else guessed from the last steps.
    /// </summary>
    bool AdvanceTo(TimeSpan time, TimeSpan? next = null)
    {
        long keep = _decoder.TrimOffset(time);

        // Frames before the one wanted that nothing references needn't decode, nor those after it before the next time
        // (once a frame between is in the decoder, so they aren't the one wanted): the frame threads and the decoding
        // thread read packets past the one wanted before the next time is asked for. A wrong guess is read again (below),
        // where the input can go back.
        var guess = Guess(time);
        long ahead = (next ?? guess) is { } n && _decoder.CanReadAgain ? -_decoder.TrimOffset(n) : -keep;
        _decoder.SkipAhead(-keep, ahead);
        _guessed |= ahead > -keep;
        if (!(_current && Pts + keep >= 0))
        {
            // Far ahead (the target's keyframe, per the index, at least a second past where decoding is): jump there. Frames that come out
            // before the first keyframe after the jump may be open-GOP B-frames decoded without their reference: if
            // one of them is the frame wanted, jump again from the keyframe before.
            long target = -keep;
            bool jumped = false;
            long key = 0;

            // The index's keyframe is at or before the target: a target within the second needs no look (nor, reading
            // ahead, the decoding thread stopped while the index is read).
            long from = (_current ? Pts : -StartOffset) + (long)(1 / _decoder.TimeBase.Value); // before the first read: the start
            if (target > from && Pause() && _decoder.CanSeek && _decoder.KeyframeAtOrBefore(target) is { } found && found > from)
            {
                key = found;

                // To the target, not the index entry: some containers index by dts and seek by pts, where the entry's dts
                // would land a keyframe early. The container finds the keyframe at or before the target.
                _ahead?.Clear(); // what was read ahead is before the jump
                _decoder.Seek(target);
                (_keepFrom, jumped) = (null, true);
            }

            if (!Forward(keep, jumped, key))
                return false;
        }

        // While the caller works, decoding ahead needn't decode what the next time passes over.
        if (next is { } known)
            _decoder.SkipBefore = -_decoder.TrimOffset(known);
        else if (ahead > _decoder.SkipBefore)
            _decoder.SkipBefore = ahead;
        return true;
    }

    /// <summary>
    /// To the first frame whose pts plus <paramref name="keep"/> is 0 or more (and that a seek's trim keeps), after a jump
    /// to the keyframe <paramref name="key"/> when <paramref name="jumped"/>; false at the end.
    /// </summary>
    bool Forward(long keep, bool jumped, long key)
    {
        bool got, beforeKeyframe = jumped, first = jumped;
        while (true)
        {
            got = Next();
            if (got)
            {
                beforeKeyframe &= !IsKeyframe;

                // The first frame after the jump already past the target: frames before it may have been left out. An
                // index by dts (mp4, mov) lands on a keyframe whose pts is past the target when B-frames before it in
                // display order follow it, and MPEG-4 Part 2's decoder drops those (no reference before the keyframe).
                bool past = first && Pts + keep > 0;
                first = false;
                if (!past && (Pts + keep < 0 || (_keepFrom is { } k && Pts + k < 0)))
                    continue;
                if (beforeKeyframe || past)
                {
                    // Possibly damaged or left out: from the keyframe before instead (no further jumps).
                    _ahead?.Clear(); // and from the keyframe before
                    _decoder.Seek(Math.Max(0, key - 1));
                    beforeKeyframe = false;
                    continue;
                }
            }

            // A packet at or after the time and before this frame (or the end) was left out on a guess: it may have been
            // the frame wanted. Read again from the keyframe before, leaving nothing out from the time on, as a jump.
            long wanted = _keepFrom is { } kf ? Math.Max(-keep, -kf) : -keep;
            if (!_decoder.LeftOut(wanted, got ? Pts : long.MaxValue))
                break;
            Pause();
            key = _decoder.KeyframeAtOrBefore(wanted) ?? 0;
            _ahead?.Clear();
            _decoder.SkipBefore = wanted;
            _decoder.Seek(wanted);
            (keep, _keepFrom, _lastTarget) = (-wanted, null, null); // and guess again after twice as many equal steps
            _stepsToGuess = Math.Min(2 * _stepsToGuess, 1 << 20);
            beforeKeyframe = first = true;
            ReadAgain++;
        }

        _current = got;
        if (!got)
            return false;
        _index++;
        return true;
    }

    /// <summary>How many times a wrong guess of the next time was read again (tests).</summary>
    internal int ReadAgain { get; private set; }

    bool _current; // the decoder holds a frame read (not past the end)

    /// <summary>The next decoded frame, the decoder's or the decoding thread's; false at the end.</summary>
    bool Next()
    {
        if (!(_ahead?.Next() ?? _decoder.Next()))
            return false;

        // ffmpeg's command line (fftools' decoder): no timestamp, the previous frame's plus its duration estimate; a
        // container without timestamps has its frames' dropped (so its frames count from 0 by the frame rate).
        long pts = _decoder.TimestampsUnreliable ? ffmpeg.AV_NOPTS_VALUE : _ahead?.Current.Pts ?? _decoder.Pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = _lastPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _lastPts + _lastDuration;
        _lastDuration = DurationEstimate(pts);
        (_lastPts, _pts) = (pts, pts);
        return true;
    }

    /// <summary>
    /// fftools' video_duration_estimate for the frame read last (at <paramref name="pts"/>): its duration, unless the
    /// container has no timestamps (then the codec's frame rate) or the duration looks made up; else the step from the
    /// frame before.
    /// </summary>
    long DurationEstimate(long pts)
    {
        long duration = Duration, diff = _lastPts != ffmpeg.AV_NOPTS_VALUE ? pts - _lastPts : -1;
        bool unreliable = _decoder.TimestampsUnreliable;
        if (duration > 0 && !unreliable && !(duration == 1 && diff > 2))
            return duration;
        long codec = 0;
        var rate = _decoder.CodecFrameRate;
        if (rate.num > 0 && rate.den > 0)
        {
            int fields = (_ahead is null ? _decoder.Frame : _ahead.Current.Frame.Frame)->repeat_pict + 2;
            codec = ffmpeg.av_rescale_q(fields, new AVRational { num = rate.den, den = rate.num * 2 }, _decoder.StreamTimeBase);
        }

        if (codec > 0 && unreliable)
            return codec;
        if (diff > 0)
            return diff;
        if (duration > 0)
            return duration;
        return codec > 0 ? codec : Math.Max(_lastDuration, 1);
    }

    /// <summary>
    /// The frame read last: its timestamp, as <see cref="FrameDecoder.Pts"/>, or for a frame without one as ffmpeg's
    /// command line makes it up.
    /// </summary>
    long Pts => _pts;

    bool IsKeyframe => _ahead?.Current.IsKeyframe ?? _decoder.IsKeyframe;

    /// <summary>What turns <see cref="Pts"/> into time from the input's start: the decoder's, or 0 where frames are timed from 0 (no timestamps).</summary>
    long StartOffset => _decoder.TimestampsUnreliable ? 0 : _decoder.StartOffset;

    long Duration => _ahead?.Current.Duration ?? _decoder.Duration;

    /// <summary>The frame read last, all its rows computed.</summary>
    AVFrame* Frame()
    {
        if (_ahead is not null)
            return _ahead.Current.Frame.Frame;
        _decoder.EnsureRows(0, _decoder.Frame->height);
        return _decoder.Frame;
    }

    /// <summary>Packets left out of the decoder so far (tests).</summary>
    internal long SkippedPackets => _decoder.SkippedPackets;

    /// <summary>How many times reading ahead paused to look at the index (tests).</summary>
    internal int ReadAheadPauses => _ahead?.Pauses ?? 0;

    /// <summary>Stops the decoding thread, if any, until the next read, so the decoder can be asked (its index); true.</summary>
    bool Pause()
    {
        _ahead?.Pause();
        return true;
    }

    /// <summary>The fps filter's next frame, converted.</summary>
    bool TryReadFps(Rational rate, out VideoFrame frame)
    {
        if (!NextFps())
        {
            frame = default;
            return false;
        }

        long pts = _fpsFrame->pts; // in 1/rate
        Convert(_fpsFrame, pts, FpsTime(pts, rate), out frame);
        return true;
    }

    static TimeSpan FpsTime(long pts, Rational rate) => TimeSpan.FromSeconds(pts * rate.Den / (double)rate.Num);

    /// <summary>The fps filter's frame given last, as a reference the caller owns (the decoder's bits per raw sample with it).</summary>
    DecodedFrame TakeFps(Rational rate)
    {
        var clone = ffmpeg.av_frame_clone(_fpsFrame);
        if (clone == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "Can't reference the frame.");
        long pts = _fpsFrame->pts;
        return new DecodedFrame(clone, pts, FpsTime(pts, rate), _decoder.BitsPerRawSample);
    }

    /// <summary>
    /// The fps filter's next frame into _fpsFrame (counted in <see cref="VideoFrame.Index"/>): decoded frames go in (shifted
    /// as ffmpeg shifts them) until one comes out; false at the end.
    /// </summary>
    bool NextFps()
    {
        var rate = _options.FrameRate!.Value;
        while (true)
        {
            if (_fpsOut != null)
            {
                ffmpeg.av_frame_unref(_fpsFrame);
                int got = ffmpeg.av_buffersink_get_frame(_fpsOut, _fpsFrame);
                if (got == 0)
                {
                    _index++;
                    return true;
                }

                if (got == ffmpeg.AVERROR_EOF)
                    break;
                if (got != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Check(got, "fps");
            }

            if (_fpsFlushed)
                break;
            if (!Next())
            {
                if (_fpsIn == null)
                    break;
                Check(ffmpeg.av_buffersrc_add_frame_flags(_fpsIn, null, 0), "fps");
                _fpsFlushed = true;
                continue;
            }

            var src = Frame();
            if (_fpsIn == null)
                BuildFps(src, rate);
            var feed = ffmpeg.av_frame_alloc();
            try
            {
                Check(ffmpeg.av_frame_ref(feed, src), "fps");
                feed->pts = Pts + StartOffset; // ffmpeg's command line shifts by the start time (no -copyts)
                feed->duration = Duration; // in the stream's time base, as the pts: fps ends the stream by the last one
                Check(ffmpeg.av_buffersrc_add_frame_flags(_fpsIn, feed, 0), "fps");
            }
            finally
            {
                ffmpeg.av_frame_free(&feed);
            }
        }

        return false;
    }

    /// <summary>buffer (the decoded frames' size, format, aspect and the stream's time base) -> fps -> buffersink.</summary>
    void BuildFps(AVFrame* first, Rational rate)
    {
        if (ffmpeg.avfilter_get_by_name("fps") == null)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, "FrameRate needs FFmpeg's fps filter, which these libraries leave out.");
        _fpsGraph = ffmpeg.avfilter_graph_alloc();
        _fpsFrame = ffmpeg.av_frame_alloc();
        var sar = first->sample_aspect_ratio.num > 0 ? first->sample_aspect_ratio : new AVRational { num = 0, den = 1 };
        var tb = _decoder.StreamTimeBase;
        string args = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"video_size={first->width}x{first->height}:pix_fmt={first->format}:time_base={tb.num}/{tb.den}:pixel_aspect={sar.num}/{sar.den}");
        AVFilterContext* source, fps, sink;
        Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_graph_create_filter(&fps, ffmpeg.avfilter_get_by_name("fps"), "fps", $"fps={rate.Num}/{rate.Den}", null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _fpsGraph), "fps");
        Check(ffmpeg.avfilter_link(source, 0, fps, 0), "fps");
        Check(ffmpeg.avfilter_link(fps, 0, sink, 0), "fps");
        Check(ffmpeg.avfilter_graph_config(_fpsGraph, null), "fps");
        _fpsIn = source;
        _fpsOut = sink;
    }

    void Convert(out VideoFrame frame)
    {
        long pts = Pts;
        Convert(Frame(), pts, TimeSpan.FromSeconds((pts + StartOffset) * _decoder.TimeBase.Value), out frame);
    }

    void Convert(AVFrame* src, long pts, TimeSpan time, out VideoFrame frame)
    {
        var (width, height) = OutputSize(src->width, src->height);
        if (_out == null)
        {
            _out = ffmpeg.av_frame_alloc();
            _in = ffmpeg.av_frame_alloc();
            _sws = ffmpeg.sws_alloc_context();
            Check(ffmpeg.av_opt_set(_sws, "sws_flags", "bicubic", 0), "scaler");
        }

        if (_out->width != width || _out->height != height)
        {
            ffmpeg.av_frame_unref(_out);
            (_out->format, _out->width, _out->height) = ((int)_format, width, height);
            Check(ffmpeg.av_frame_get_buffer(_out, 1), "frame buffer"); // rows packed, planes one after another
        }

        // As the scale filter does: the input read as progressive (its interl=0), and the output's tags those the link
        // negotiates (YUV and gray keep the input's matrix and range; RGB is RGB).
        // Its in_chroma_loc (default unspecified) replaces the frame's chroma location, so chroma isn't re-sited.
        Check(ffmpeg.av_frame_ref(_in, src), "frame");
        _in->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        _in->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        (_out->chroma_location, _out->color_primaries, _out->color_trc) = (_in->chroma_location, _in->color_primaries, _in->color_trc);
        bool rgb = _options.FrameFormat is not (FrameFormat.Gray8 or FrameFormat.Yuv420p);
        _out->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : src->colorspace;
        _out->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : src->color_range;
        int ret = ffmpeg.sws_scale_frame(_sws, _out, _in);
        ffmpeg.av_frame_unref(_in);
        Check(ret, "convert");
        int size = ffmpeg.av_image_get_buffer_size(_format, width, height, 1);
        ReadOnlySpan<byte> data;
        if (_options.FrameFormat != FrameFormat.Yuv420p)
        {
            data = new ReadOnlySpan<byte>(_out->data[0], size); // one plane, rows packed (align 1)
        }
        else
        {
            // The planes packed one after another, as rawvideo writes them (the frame's buffer may pad between them).
            if (_packed.Length != size)
                _packed = new byte[size];
            var planes = default(byte_ptrArray4);
            var strides = default(int_array4);
            for (uint i = 0; i < 4; i++)
            {
                planes[i] = _out->data[i];
                strides[i] = _out->linesize[i];
            }

            fixed (byte* p = _packed)
                Check(ffmpeg.av_image_copy_to_buffer(p, size, in planes, in strides, _format, width, height, 1), "pack");
            data = _packed;
        }

        // The aspect ratio as the scale filter sets its output's: the input's times the change in shape (0/1 stays).
        var sar = src->sample_aspect_ratio;
        sar = sar.num != 0
            ? ffmpeg.av_mul_q(new AVRational { num = height * src->width, den = width * src->height }, sar)
            : new AVRational { num = 0, den = 1 };

        // Gray as ffmpeg's link negotiates it: full range, no matrix, although swscale copies the luma unchanged.
        var tags = _options.FrameFormat == FrameFormat.Gray8
            ? new FrameTags(sar, AVColorRange.AVCOL_RANGE_JPEG, AVColorSpace.AVCOL_SPC_UNSPECIFIED, _out->color_primaries, _out->color_trc)
            : new FrameTags(sar, _out->color_range, _out->colorspace, _out->color_primaries, _out->color_trc);
        frame = new VideoFrame(_index, pts, time, width, height, _options.FrameFormat, data, tags);
    }

    /// <summary>The size frames come out at: Width and Height, or MaxWidth's cap, or the frame's own.</summary>
    (int Width, int Height) OutputSize(int width, int height)
    {
        if (_options.Width is { } w && _options.Height is { } h)
            return (w, h);
        if (_options.MaxWidth is not { } max)
            return (width, height);

        // scale's -2: av_rescale(w, ih, iw * 2) * 2, the division rounded to nearest (halves away from zero), so an
        // odd height becomes even even when the width stays. A height that comes out 0 (a frame over twice as wide as
        // tall per pixel of the cap) is the input's, as libavfilter fills in a link's missing size.
        int outWidth = Math.Min(max, width);
        long twice = 2L * width;
        int outHeight = (int)((outWidth * (long)height + twice / 2) / twice * 2);
        return (outWidth, outHeight == 0 ? height : outHeight);
    }

    /// <summary>Stops the decoding thread (when reading ahead), then frees the decoder, the scaler and the output buffer.</summary>
    public void Dispose()
    {
        _ahead?.Dispose();
        if (_sws != null)
        {
            var s = _sws;
            ffmpeg.sws_free_context(&s);
            _sws = null;
        }

        if (_in != null)
        {
            var f = _in;
            ffmpeg.av_frame_free(&f);
            _in = null;
        }

        if (_out != null)
        {
            var f = _out;
            ffmpeg.av_frame_free(&f);
            _out = null;
        }

        if (_fpsFrame != null)
        {
            var f = _fpsFrame;
            ffmpeg.av_frame_free(&f);
            _fpsFrame = null;
        }

        if (_fpsGraph != null)
        {
            var g = _fpsGraph;
            ffmpeg.avfilter_graph_free(&g);
            _fpsGraph = null;
            _fpsIn = _fpsOut = null;
        }

        _decoder.Dispose();
    }

    static void Check(int ret, string what)
    {
        if (ret < 0)
            throw new FrameReaderException(FrameReaderError.DecodeFailed, $"FFmpeg ({what}): {FFmpegLibraries.ErrorMessage(ret)}");
    }
}
