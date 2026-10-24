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
    readonly List<long> _framePts = [];      // each frame's pts as it arrives
    readonly int _frameSkip;                 // frames FrameSkip won't analyse aren't converted
    int _framesRead;                         // frames taken from _frames
    bool _framesEnded;
    YuvFrame _currentFrame;                  // the last frame taken: valid until the next MoveNext

    /// <summary>Whether the frames come from the caller (<see cref="YuvVideo"/>), not a decoder.</summary>
    internal bool FromFrames => _frames is not null;

    /// <param name="video">What the headers would say.</param>
    /// <param name="frames">The frames, not yet started.</param>
    /// <param name="o">The resolved options.</param>
    internal VideoReader(YuvVideo video, IEnumerator<YuvFrame> frames, DetectionOptions o)
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

        Compatibility = o.Compatibility;
        _yuv420 = o.Yuv420Converter;
        _path = "";
        _ffprobe = "";
        FfmpegExe = "";
        _inputOptions = [];
        _pts = [];
        _decoder = VideoDecoder.Auto;
        _frameBuffers = o.FrameBuffers;
        _frameSkip = o.FrameSkip;
        _frames = frames;
        SourceWidth = video.Width;
        SourceHeight = video.Height;
        _codedSize = (video.Width, video.Height);

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
        int w = SourceWidth, h = SourceHeight, cw = (w + 1) / 2, ch = (h + 1) / 2;
        CheckPlane(f.Y, f.YStride, w, h, "Y");
        CheckPlane(f.U, f.UStride, cw, ch, "U");
        CheckPlane(f.V, f.VStride, cw, ch, "V");
        if (_framesTimed)
        {
            // Frame k at k / rate from the first, within half a frame: no variable rate, no frame the decoder dropped
            // (a file's frames are labelled by their packets, which frames from the caller can't say).
            if (_framePts.Count > 0)
            {
                double from = (f.Pts - _framePts[0]) * _timeBase, expected = _framesRead / _framesRate;
                if (!(Math.Abs(from - expected) <= 0.5 / _framesRate))
                {
                    throw new ShotDetectionException(ShotDetectionError.InvalidInput,
                        string.Create(CultureInfo.InvariantCulture,
                            $"Frame {_framesRead} is {from:0.######} s after the first, not {expected:0.######} s: frames from the caller must come at a constant frame rate (variable frame rate and dropped frames aren't supported yet)."));
                }
            }

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

    /// <summary>The frame's position, as <see cref="Position"/> computes a file's: from its pts, else from its number.</summary>
    FrameTime FramePosition(int frame)
    {
        // Before the frames are read (where a run starts): taken without converting them. Once reading has started,
        // a frame past the last one taken has no pts, as a file's past its last packet.
        while (frame >= _framesRead && !_framesStarted && TakeFrame())
        {
        }

        if (_framesTimed && frame < _framePts.Count)
        {
            double ms = (_framePts[frame] - _startPts) * _timeBase * 1000;
            long micros = (long)Math.Round(ms * 1000);
            if (micros > 0)
                return FrameTime.Pts(micros, 1_000_000, Fps);
        }

        return FrameTime.Pts((long)frame * Fps.Den, Fps.Num, Fps);
    }

    bool _framesStarted; // FramesFromCaller has begun: frames are taken only there from then on

    /// <summary>
    /// <see cref="SeekFrame"/> for frames from the caller: round(seconds × fps), as for a constant-rate file, clamped to
    /// the frame count when known. The file path's search for the nearest frame needs every timestamp first; at a
    /// constant rate the estimate is the frame.
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

        // Within a frame, as Position's check for a file; else (the start past the last frame) the last.
        double at = FramePosition(estimate).Seconds;
        if (Math.Abs(at - seconds) <= 1.0 / Fps.Value || (estimate == FrameCountHint - 1 && at < seconds))
            return estimate;
        throw new ShotDetectionException(ShotDetectionError.InvalidInput,
            string.Create(CultureInfo.InvariantCulture, $"Frame {estimate} is at {at} s, too far from the start time {seconds} s for a constant frame rate."));
    }

    /// <summary>The frame <see cref="FramesSeekFrame"/> starts from: round(seconds × fps), clamped to the frame count when known.</summary>
    int StartEstimate(double seconds)
    {
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
        bool resize = (Width, Height) != (CropRegion.Width, CropRegion.Height);
        var resizer = resize ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        var convert = YuvForVideo!;
        int w = SourceWidth, h = SourceHeight, cw = (w + 1) / 2, ch = (h + 1) / 2;
        var yuv = new byte[IYuv420Converter.FrameSize(w, h)];
        var small = new byte[Width * Height * 3];

        // The rows the resize reads, when the converter says which chroma rows those take; every row without a resize.
        var rows = resizer is not null ? YuvRowsRead(resizer) : null;
        int[] luma = rows?.Luma ?? [.. Enumerable.Range(0, h)], chroma = rows?.Chroma ?? [.. Enumerable.Range(0, ch)];
        int[] cols = [.. Enumerable.Range(CropRegion.X, CropRegion.Width)];
        var bgrRow = resize ? [] : new byte[w * 3];
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
                    resizer.ResizeYuv(yuv, small, convert, w, h, 0, CropRegion.X, CropRegion.Y);
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
