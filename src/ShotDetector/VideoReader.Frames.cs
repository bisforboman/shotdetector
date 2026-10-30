using System.Globalization;
using FieldOrder = FrameReader.FieldOrder;

namespace ShotDetector;

/// <summary>
/// Frames the caller decoded (<see cref="ShotDetection.Detect(YuvVideo, IEnumerable{YuvFrame}, DetectionOptions?, CancellationToken)"/>):
/// no probe, no ffmpeg, no FFmpeg libraries. The video's description stands in for the probe; the frames' planes go
/// through the same converter (FastYuv's) and resize as a file's on the YUV pipeline, so a file's frames give its result.
/// </summary>
internal sealed partial class VideoReader
{
    readonly IEnumerator<YuvFrame>? _frames; // the caller's frames; null for every other input
    readonly bool _framesTimed;              // the frames carry pts (YuvVideo.TimeBase set)
    readonly double _framesRate;             // the video's own rate, for the constant-rate check
    readonly long[]? _frameTimestamps;       // every frame's pts (YuvVideo.FrameTimestamps), in place of that check
    int _lastEntry = -1;                     // the entry of _frameTimestamps the last frame taken is at
    readonly List<long> _framePts = [];      // each frame's pts as it arrives
    readonly int _frameSkip;                 // frames FrameSkip won't analyse aren't converted
    readonly int _frameBase;                 // the first frame's index in the video (a range's, ShotDetection.ScoreRange); 0 otherwise
    int _framesRead;                         // the index after the last frame taken from _frames
    bool _framesEnded;
    YuvFrame _currentFrame;                  // the last frame taken: valid until the next MoveNext

    /// <summary>Whether the frames come from the caller (<see cref="YuvVideo"/>), not a decoder.</summary>
    internal bool FromFrames => _frames is not null;

    /// <param name="video">What the headers would say.</param>
    /// <param name="frames">The frames, not yet started.</param>
    /// <param name="o">The resolved options.</param>
    /// <param name="frameBase">The index in the video of the first frame <paramref name="frames"/> gives: 0, or a range's
    /// first frame (<see cref="ShotDetection.ScoreRange(YuvVideo, IEnumerable{YuvFrame}, long, DetectionOptions?, CancellationToken)"/>),
    /// whose frames aren't checked for the constant rate here (that needs frame 0's pts: <see cref="ShotDetection.Merge"/> checks them);
    /// with <see cref="YuvVideo.FrameTimestamps"/>, its first frame must be at that frame's entry.</param>
    internal VideoReader(YuvVideo video, IEnumerator<YuvFrame> frames, DetectionOptions o, int frameBase = 0)
    {
        if (o.Yuv420Converter is null)
        {
            throw new ArgumentException("Frames from the caller need DetectionOptions.Yuv420Converter (SwscaleYuv420 from the "
                + "ShotDetector.FastYuv package): without FFmpeg there is no other conversion to BGR.");
        }

        if (o.Deinterlace != DeinterlaceMode.Off || o.Yadif is not null)
            throw new ArgumentException("Deinterlace isn't available for frames from the caller (it is FFmpeg's yadif); pass deinterlaced frames.");
        if (o.FfmpegResize)
            throw new ArgumentException("FfmpegResize isn't available for frames from the caller (it is ffmpeg's scaler).");
        if (video.PixelFormat is not ("yuv420p" or "yuvj420p"))
            throw new ArgumentException($"Frames from the caller must be yuv420p or yuvj420p so far, not {video.PixelFormat}.");
        if (video.Width < 1 || video.Height < 1 || (long)video.Width * video.Height > FrameReader.FrameDecoderOptions.DefaultMaxPixels)
            throw new ArgumentException($"The frames' size ({video.Width}x{video.Height}) must be positive and at most 8192 x 8192 pixels.");
        if (video.Height % 2 != 0)
            throw new ArgumentException($"The frames' height must be even ({video.Height}): the converter reproduces swscale's yuv420p conversion, which needs it.");
        if (video.FrameRate.Num <= 0 || video.FrameRate.Den <= 0)
            throw new ArgumentException("YuvVideo.FrameRate must be positive.");
        if (video.TimeBase is { } t && (t.Num <= 0 || t.Den <= 0))
            throw new ArgumentException("YuvVideo.TimeBase must be positive.");
        if (video.FrameTimestamps is { } entries)
        {
            if (video.TimeBase is null)
                throw new ArgumentException("YuvVideo.FrameTimestamps are in YuvVideo.TimeBase, which isn't set.");
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i] <= entries[i - 1])
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"YuvVideo.FrameTimestamps must rise from each frame to the next (presentation order): entry {i} is {entries[i]}, after {entries[i - 1]}."));
                }
            }

            _frameTimestamps = [.. entries];
        }

        if (video.Rotation % 90 != 0 || Math.Abs(video.Rotation) >= 360)
            throw new ArgumentException($"YuvVideo.Rotation must be a quarter turn (0, 90, 180, 270, or -90, -180, -270), not {video.Rotation}.");

        Compatibility = o.Compatibility;
        _yuv420 = o.Yuv420Converter;
        _path = "";
        _ffprobe = "";
        FfmpegExe = "";
        _inputOptions = [];
        _knownPts = [];
        _probed = new((ProbeResult?)null);
        _decoder = VideoDecoder.Auto;
        _frameBuffers = o.FrameBuffers;
        _frameSkip = o.FrameSkip;
        _frames = frames;
        _frameBase = frameBase;
        _framesRead = frameBase;
        SourceWidth = video.Width;
        SourceHeight = video.Height;
        _codedSize = (video.Width, video.Height);

        // As a file's: frames turned upright after converting (Turn), the upright size reported and analysed.
        _rotation = video.Rotation;
        if (Math.Abs(_rotation) % 180 == 90)
            (SourceWidth, SourceHeight) = (SourceHeight, SourceWidth);

        // As a file's: scenedetect's fraction of the average rate, or the frame rate override.
        _framesRate = video.FrameRate.Value;
        Fps = o.FrameRate is not { } fr ? Fps.FromFloat(_framesRate)
            : fr <= 0 ? throw new ArgumentException("FrameRate must be positive.")
            : Fps.FromFloat(fr);
        FrameCountHint = video.FrameCount ?? 0;
        OpenCvFrameCount = FrameCountHint > 0 ? FrameCountHint
            : video.Duration is { } length ? (long)Math.Floor(length.TotalSeconds * _framesRate + 0.5) : 0;
        Duration = video.Duration;
        (CropRegion, Width, Height) = AnalysedSize(SourceWidth, SourceHeight, o);
        _pixelFormat = video.PixelFormat;
        var stream = new Dictionary<string, string>
        {
            ["color_space"] = video.ColorSpace,
            ["color_range"] = video.FullRange ? "pc" : "",
        };
        _colours = new Lazy<Colours>(() => ReadColours(stream));
        if (YuvForVideo is null)
            throw new ArgumentException($"The converter doesn't take this video's colours (color_space '{video.ColorSpace}', full range {video.FullRange}).");
        FieldOrder = FieldOrder.Unknown;
        Codec = video.Codec;
        Container = video.Container;
        _framesTimed = video.TimeBase is not null;
        _timeBase = video.TimeBase is { } tb ? tb.Num / (double)tb.Den : 0;
        _startPts = video.StartPts;
    }

    /// <summary>Takes the next frame: checks its planes, records its pts and that it comes at the constant rate.</summary>
    bool TakeFrame()
    {
        if (_framesEnded || !_frames!.MoveNext())
        {
            _framesEnded = true;
            return false;
        }

        var f = _frames.Current;
        int w = _codedSize.W, h = _codedSize.H, cw = (w + 1) / 2, ch = (h + 1) / 2;
        CheckPlane(f.Y, f.YStride, w, h, "Y");
        CheckPlane(f.U, f.UStride, cw, ch, "U");
        CheckPlane(f.V, f.VStride, cw, ch, "V");
        if (_frameTimestamps is { } entries)
        {
            // At its entry: the first frame given at its own (frame 0's, or a range's first frame's), each next one at a
            // later one. Skipping entries is a frame the decoder dropped, which a file's run takes as it comes (the
            // frame keeps its own time, the numbers go on); ranges are checked frame by frame when merged.
            int entry = _lastEntry < 0
                ? (_framesRead < entries.Length && entries[_framesRead] == f.Pts ? _framesRead : -1)
                : Array.BinarySearch(entries, _lastEntry + 1, entries.Length - _lastEntry - 1, f.Pts);
            if (entry < 0)
            {
                throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                    string.Create(CultureInfo.InvariantCulture, _lastEntry < 0
                        ? $"Frame {_framesRead}'s pts is {f.Pts}, not its entry in YuvVideo.FrameTimestamps ({(_framesRead < entries.Length ? entries[_framesRead].ToString(CultureInfo.InvariantCulture) : "none: there are " + entries.Length)}): is it that frame?"
                        : $"Frame {_framesRead}'s pts ({f.Pts}) isn't in YuvVideo.FrameTimestamps after the previous frame's ({entries[_lastEntry]}): frames must come in presentation order, at their timestamps."));
            }

            _lastEntry = entry;
            _framePts.Add(f.Pts);
        }
        else if (_framesTimed)
        {
            // Frame k at k / rate from the first, within half a frame: no variable rate, no frame the decoder dropped
            // (a file's frames are labelled by their packets, which frames from the caller can't say). A range's frames
            // are checked from frame 0's when the ranges are merged.
            if (_framePts.Count > 0 && _frameBase == 0)
                CheckConstantRate(_framePts[0], f.Pts, _framesRead, _timeBase, _framesRate);
            _framePts.Add(f.Pts);
        }

        _currentFrame = f;
        _framesRead++;
        return true;

        void CheckPlane(ReadOnlyMemory<byte> plane, int stride, int width, int height, string name)
        {
            if (stride < width || plane.Length < (long)stride * (height - 1) + width)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"Frame {_framesRead}'s {name} plane ({plane.Length} bytes, stride {stride}) is smaller than {width} x {height}."));
            }
        }
    }

    /// <summary>
    /// Frame <paramref name="frame"/> (at <paramref name="pts"/>) at frame / rate after frame 0 (at <paramref name="firstPts"/>),
    /// within half a frame; else a <see cref="ShotDetectionException"/> (<see cref="ShotDetectionError.InvalidInput"/>). The
    /// sequential run's check of each frame and the merge's of every range's.
    /// </summary>
    internal static void CheckConstantRate(long firstPts, long pts, int frame, double timeBase, double rate)
    {
        double from = (pts - firstPts) * timeBase, expected = frame / rate;
        if (!(Math.Abs(from - expected) <= 0.5 / rate))
        {
            throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                string.Create(CultureInfo.InvariantCulture,
                    $"Frame {frame} is {from:0.######} s after the first, not {expected:0.######} s: frames from the caller must come at a constant frame rate (variable frame rate and dropped frames aren't supported yet)."));
        }
    }

    /// <summary>Every frame's pts, <see cref="YuvVideo.FrameTimestamps"/>; null when not given.</summary>
    internal IReadOnlyList<long>? FrameTimestamps => _frameTimestamps;

    /// <summary>Whether the frames carry pts (<see cref="YuvVideo.TimeBase"/> set).</summary>
    internal bool FramesTimed => _framesTimed;

    /// <summary>The frames' own rate (the video's average rate, not the <see cref="DetectionOptions.FrameRate"/> override).</summary>
    internal double FramesRate => _framesRate;

    /// <summary>The time base of the frames' pts in seconds.</summary>
    internal double FramesTimeBase => _timeBase;

    /// <summary>Each frame's pts as it arrived, from the first given (<c>frameBase</c>); empty without <see cref="YuvVideo.TimeBase"/>.</summary>
    internal IReadOnlyList<long> FramePts => _framePts;

    /// <summary>
    /// A reader for frames already read elsewhere (<see cref="ShotDetection.Merge"/>): <paramref name="count"/> frames from
    /// frame 0, at <paramref name="pts"/> (one each, or none when untimed), so <see cref="Position"/> gives each frame's as
    /// the sequential run's reader does. Nothing more is taken from the caller.
    /// </summary>
    internal void LoadFrames(IReadOnlyList<long> pts, int count)
    {
        if (_frameBase != 0 || _framesRead != 0)
            throw new InvalidOperationException("Frames are loaded into a new reader from frame 0 only.");
        _framePts.AddRange(pts);
        _framesRead = count;
        _framesEnded = true;
        _framesStarted = true;
    }

    /// <summary>One more frame for a reader of <see cref="LoadFrames"/>: a range's next, as it is scored (ShotDetection.LiveShots).</summary>
    internal void LoadFrame(long? pts)
    {
        if (pts is { } p)
            _framePts.Add(p);
        _framesRead++;
    }

    /// <summary>The frame's position, as <see cref="Position"/> computes a file's: from its pts, else from its number.</summary>
    FrameTime FramePosition(int frame)
    {
        // Before the frames are read (where a run starts): taken without converting them. Once reading has started,
        // a frame past the last one taken has no pts, as a file's past its last packet. With every frame's timestamp
        // given, a frame not taken is at its entry, as a file's frame is at its packet's.
        while (frame >= _framesRead && !_framesStarted && _frameTimestamps is null && TakeFrame())
        {
        }

        if (_framesTimed && frame - _frameBase is >= 0 and var given && given < _framePts.Count)
            return PtsPosition(_framePts[given], frame);
        if (_frameTimestamps is { } entries && frame >= 0 && frame < entries.Length)
            return PtsPosition(entries[frame], frame);
        return FrameTime.Pts((long)frame * Fps.Den, Fps.Num, Fps);
    }

    /// <summary>Frame <paramref name="frame"/>'s position at <paramref name="pts"/>: in microseconds from the start, or by its number where that isn't positive.</summary>
    FrameTime PtsPosition(long pts, int frame)
    {
        double ms = (pts - _startPts) * _timeBase * 1000;
        long micros = (long)Math.Round(ms * 1000);
        return micros > 0 ? FrameTime.Pts(micros, 1_000_000, Fps) : FrameTime.Pts((long)frame * Fps.Den, Fps.Num, Fps);
    }

    bool _framesStarted; // FramesFromCaller has begun: frames are taken only there from then on

    /// <summary>
    /// <see cref="SeekFrame"/> for frames from the caller: round(seconds × fps), as for a constant-rate file, clamped to
    /// the frame count when known. The file path's search for the nearest frame needs every timestamp first: with
    /// <see cref="YuvVideo.FrameTimestamps"/> it is that search; without them, at a constant rate the estimate is the frame.
    /// </summary>
    int FramesSeekFrame(double seconds)
    {
        if (seconds <= 0)
            return 0;
        int estimate = StartEstimate(seconds);
        while (estimate >= _framesRead)
        {
            if (!TakeFrame())
                throw new ArgumentException($"StartTime is after the last frame ({_framesRead} frames).");
        }

        if (_frameTimestamps is { } entries)
        {
            // Frame k at entry k, as the file path's frame after its seek is the one at that packet: a frame dropped
            // before it would number the frames from there otherwise.
            if (_framePts[estimate - _frameBase] != entries[estimate])
            {
                throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                    string.Create(CultureInfo.InvariantCulture, $"Frame {estimate}, where StartTime starts, isn't at its entry in YuvVideo.FrameTimestamps: a frame before it was dropped."));
            }

            return estimate;
        }

        // Within a frame, as Position's check for a file; else (the start past the last frame) the last.
        double at = FramePosition(estimate).Seconds;
        if (Math.Abs(at - seconds) <= 1.0 / Fps.Value || (estimate == FrameCountHint - 1 && at < seconds))
            return estimate;
        throw new ShotDetectionException(ShotDetectionError.InvalidInput,
            string.Create(CultureInfo.InvariantCulture, $"Frame {estimate} is at {at} s, too far from the start time {seconds} s for a constant frame rate."));
    }

    /// <summary>
    /// The frame <see cref="FramesSeekFrame"/> starts from: round(seconds × fps), clamped to the frame count when known;
    /// with <see cref="YuvVideo.FrameTimestamps"/>, the file path's frame (<see cref="SeekFrame"/>) from them.
    /// </summary>
    int StartEstimate(double seconds)
    {
        if (_frameTimestamps is { Length: > 0 } entries)
        {
            // As the file's: the estimate when it is within a frame, else the first frame at or after half a frame before the time.
            int at = Math.Clamp(FrameAt(seconds), 0, entries.Length - 1);
            if (Math.Abs(PtsPosition(entries[at], at).Seconds - seconds) <= 1.0 / Fps.Value)
                return at;
            double target = seconds - (0.5 / Fps.Value);
            int lo = 0, hi = entries.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (PtsPosition(entries[mid], mid).Seconds >= target)
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return lo;
        }

        int estimate = FrameAt(seconds);
        return FrameCountHint > 0 ? (int)Math.Clamp(estimate, 0, FrameCountHint - 1) : estimate;
    }

    /// <summary>
    /// Whether a run's start (<see cref="Position"/> of frame 0, then <see cref="SeekFrame"/> to
    /// <paramref name="startSeconds"/>) would take another frame from the caller. The run takes those one at a time
    /// (<see cref="TakeBeforeStart"/>), each in a step of its own, so its driver can put each in the feed first; the start
    /// then finds them taken. False for every other input.
    /// </summary>
    internal bool TakesBeforeStart(double? startSeconds) =>
        _frames is not null && !_framesStarted && !_framesEnded
        && _framesRead <= (startSeconds is { } s && s > 0 ? StartEstimate(s) : 0);

    /// <summary>Takes one of the frames <see cref="TakesBeforeStart"/> wants.</summary>
    internal void TakeBeforeStart() => TakeFrame();

    /// <summary>
    /// <see cref="Frames(int, int?, CancellationToken, int)"/> for frames from the caller: the frames before
    /// <paramref name="startFrame"/> are taken but not converted, nor are those FrameSkip won't analyse.
    /// </summary>
    IEnumerable<byte[]> FramesFromCaller(int startFrame, int? count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count is <= 0)
            yield break;
        bool resize = (Width, Height) != (CropRegion.Width, CropRegion.Height);
        var resizer = resize ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        var convert = YuvForVideo!;
        int w = _codedSize.W, h = _codedSize.H, cw = (w + 1) / 2, ch = (h + 1) / 2;
        var yuv = new byte[IYuv420Converter.FrameSize(w, h)];
        var small = new byte[Width * Height * 3];

        // The rows the resize reads, when the converter says which chroma rows those take; every row without a resize.
        var rows = resizer is not null ? YuvRowsRead(resizer) : null;
        int[] luma = rows?.Luma ?? [.. Enumerable.Range(0, h)], chroma = rows?.Chroma ?? [.. Enumerable.Range(0, ch)];
        int turn = Turn;
        int[] cols = turn != 0 ? [.. Enumerable.Range(0, w)] : [.. Enumerable.Range(CropRegion.X, CropRegion.Width)];
        var bgrRow = resize ? [] : new byte[w * 3];
        var bgr = resize || turn == 0 ? [] : new byte[w * h * 3];
        _framesStarted = true;
        for (int k = startFrame; count is null || k < startFrame + count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (_framesRead <= k)
            {
                if (!TakeFrame())
                    yield break;
            }

            if (_framesRead != k + 1)
                throw new InvalidOperationException($"Frame {k} was passed before it was converted.");
            if ((k - startFrame) % (_frameSkip + 1) == 0)
            {
                var f = _currentFrame;
                foreach (int r in luma)
                    f.Y.Span.Slice(r * f.YStride, w).CopyTo(yuv.AsSpan(r * w, w));
                foreach (int r in chroma)
                {
                    f.U.Span.Slice(r * f.UStride, cw).CopyTo(yuv.AsSpan(w * h + r * cw, cw));
                    f.V.Span.Slice(r * f.VStride, cw).CopyTo(yuv.AsSpan(w * h + cw * ch + r * cw, cw));
                }

                if (resizer is not null)
                {
                    resizer.ResizeYuv(yuv, small, convert, w, h, turn, CropRegion.X, CropRegion.Y);
                }
                else if (turn != 0)
                {
                    // Turned without a resize (ffmpeg's scale,format=bgr24 and transpose or flips, then the crop, for a
                    // file): the whole frame converted, then the crop's pixels taken from where the turn puts them.
                    for (int y = 0; y < h; y++)
                        convert.Row(yuv, w, h, y, cols, bgr.AsSpan(y * w * 3, w * 3));
                    for (int y = 0; y < CropRegion.Height; y++)
                    {
                        for (int x = 0; x < CropRegion.Width; x++)
                        {
                            var (sx, sy) = CvResize.TurnedSource(turn, CropRegion.X + x, CropRegion.Y + y, w, h);
                            bgr.AsSpan(((sy * w) + sx) * 3, 3).CopyTo(small.AsSpan(((y * CropRegion.Width) + x) * 3));
                        }
                    }
                }
                else
                {
                    // No resize (ffmpeg's scale,format=bgr24 then the crop, for a file): the crop's pixels converted.
                    for (int y = 0; y < CropRegion.Height; y++)
                    {
                        convert.Row(yuv, w, h, CropRegion.Y + y, cols, bgrRow);
                        bgrRow.AsSpan(CropRegion.X * 3, CropRegion.Width * 3).CopyTo(small.AsSpan(y * CropRegion.Width * 3));
                    }
                }
            }

            yield return small;
        }
    }
}
