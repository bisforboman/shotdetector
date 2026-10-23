using System.Collections.Concurrent;

namespace ShotDetector;

internal sealed partial class VideoReader
{
    /// <summary>
    /// Whether frames are decoded in this process (<see cref="VideoDecoder.InProcess"/>, or <see cref="VideoDecoder.Auto"/>
    /// with FFmpeg 8's libraries available) rather than by the ffmpeg executable. Inputs it doesn't handle use the
    /// executable: URLs whose protocol the libraries lack (and Streaming on a path), codecs the libraries have no decoder
    /// for, and rotations other than quarter turns.
    /// </summary>
    public bool DecodesInProcess => _decoder != VideoDecoder.FfmpegProcess && (_stream is not null || !Streaming || _urlInProcess)
        && !_inProcessGaveNothing && !_alwaysBt601
        && (_decoder == VideoDecoder.InProcess || InProcess.CanLoad(_ffmpegDirectory))
        && FrameReader.FrameDecoder.HasDecoder(Codec)
        && _rotation % 90 == 0

        // Deinterlacing needs libavfilter's yadif too (ShotDetector.Native has it since 0.9).
        && (!_deinterlace || InProcess.CanDeinterlace(_ffmpegDirectory));

    bool _inProcessGaveNothing; // Auto, and the libraries decoded no frame of this video: the executable instead
    bool _ptsFromFrames;        // MPEG-PS and the like: packets without pts, timestamps from a decoding pass

    /// <summary>MPEG-PS and the like: the packets carry no pts, so a seek decodes from the start.</summary>
    internal bool PtsFromFrames => _ptsFromFrames;

    /// <summary>The decoded size of the first frame Frames gave in-process (before rotation); null before then.</summary>
    internal (int Width, int Height)? FirstFrameSize { get; private set; }

    /// <summary>On a YUV pipeline in-process: the frames that took the BGR conversion instead (another size or pixel format).</summary>
    internal int BgrFallbackFrames { get; private set; }

    /// <summary>Whether the last in-process read converted on a thread of its own (<see cref="DetectionOptions.ConvertThreads"/>).</summary>
    internal bool ConvertedOnThread { get; private set; }

    /// <summary>
    /// The conversion threads when <see cref="DetectionOptions.ConvertThreads"/> is unset: 1 for frames of at least
    /// 1280 x 720's pixels (HD, coded size, so 720 x 1280 portrait or stored sideways counts too; 1280 x 534 scope
    /// doesn't), else 0. Sequential runs of HD gained 1.09-1.20x with the same CPU time and results, 360p 1.00-1.03x
    /// (2026-10-22, docs/decisions/2026-10-22-decode-split.md).
    /// </summary>
    internal static int DefaultConvertThreads(int codedWidth, int codedHeight) =>
        (long)codedWidth * codedHeight >= ConvertThreadMinPixels ? 1 : 0;

    /// <summary>The fewest pixels per frame <see cref="DefaultConvertThreads"/> converts on a thread of its own for.</summary>
    internal const long ConvertThreadMinPixels = 1280L * 720;

    /// <summary>The most bytes of decoded frames queued for the conversion thread (at least one frame, at most three).</summary>
    const long HandoffBytes = 32L << 20;

    /// <summary>
    /// The decoded frame's rows this video's YUV pipeline converts and the chroma rows converting them reads
    /// (<see cref="YuvRowsRead(CvResize, IYuvChromaRows, int, int, bool, int, int, int)"/>); null when the converter
    /// doesn't say which (<see cref="IYuvChromaRows"/>): whole planes then.
    /// </summary>
    internal (int[] Luma, int[] Chroma)? YuvRowsRead(CvResize resizer) => YuvForVideo?.ChromaRows is { } chromaRows
        ? YuvRowsRead(resizer, chromaRows, _codedSize.W, _codedSize.H, Pipeline == FramePipeline.Yuv422Sampled, Turn, CropRegion.X, CropRegion.Y)
        : null;

    /// <summary>
    /// The rows of a <paramref name="width"/> × <paramref name="height"/> decoded frame that
    /// <see cref="CvResize.ResizeYuv(byte[], byte[], YuvConversion, int, int, int, int, int)"/> reads (ascending): the
    /// luma rows it converts, and the chroma rows (of U and V) converting those reads, as the converter gives them.
    /// </summary>
    internal static (int[] Luma, int[] Chroma) YuvRowsRead(CvResize resizer, IYuvChromaRows chromaRows, int width, int height, bool chroma422, int turn, int cropX, int cropY)
    {
        int chromaHeight = chroma422 ? height : (height + 1) / 2;
        var luma = resizer.DecodedRows(width, height, turn, cropX, cropY);
        var chroma = new SortedSet<int>();
        foreach (int y in luma)
        {
            var (first, count) = chromaRows.ChromaRows(width, height, chromaHeight, y);
            for (int c = first; c < first + count; c++)
                chroma.Add(c);
        }

        return (luma, [.. chroma]);
    }

    /// <summary>Frames(startFrame, count) decoded in-process: the same bytes per pipeline as the ffmpeg pipe, then the same resize.</summary>
    IEnumerable<byte[]> FramesInProcess(int startFrame, int? count, CancellationToken cancellationToken, int seekBack)
    {
        var pipeline = Pipeline;
        var resizer = pipeline != FramePipeline.FfmpegScale ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        _livePackets.Clear();
        _liveStart = startFrame;
        int size = pipeline switch
        {
            // Frames of a new size come resized already (below), so room for those too.
            FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled or FramePipeline.Yuv420P10Sampled => Math.Max(YuvFrameSize, Width * Height * 3),
            FramePipeline.FullFrameResize => CropRegion.Width * CropRegion.Height * 3,
            FramePipeline.SampledBgr => resizer!.SourceRows.Length * CropRegion.Width * 3,
            _ => Width * Height * 3,
        };

        // The rows the resize reads, with the crop's offset: the only ones converted. The decoding thread hands those
        // rows over and the caller's thread resizes them (it waited for frames most of the time otherwise: the resize
        // was most of the decoding thread's work). Frames of a new size on the FastYuv path are resized on the decoding
        // thread, with its own CvResize, as each keeps row buffers.
        int[]? rows = resizer?.SourceRows.Select(r => r + CropRegion.Y).ToArray();

        // The YUV pipelines: the decoded rows the conversion reads, when the converter says which chroma rows those are
        // (FastYuv's do), so only they are copied (and, with row-wise yadif, deinterlaced); else whole planes.
        var yuvRows = IsYuv(pipeline) ? YuvRowsRead(resizer!) : null;
        var decoderResizer = resizer is null ? null : new CvResize(CropRegion.Width, CropRegion.Height, Width, Height);
        var small = new byte[Width * Height * 3];

        // Decoding and converting run on their own thread and the resize and scoring on the caller's, overlapped as
        // with the executable (where ffmpeg is the other side). Eight buffers, so neither side waits for the other on
        // every frame (with three, the handoffs cost as much as the decoding on small video); a chunk of a parallel run
        // gets a share of the eight (ShotDetection.ChunkBuffers): there the other chunks keep the cores busy while one
        // waits. Cancelled by the caller's token, or by us when the caller stops iterating.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var free = new BlockingCollection<byte[]>();
        using var full = new BlockingCollection<(byte[] Buffer, long Pts, bool Bgr)>();
        for (int i = 0; i < _frameBuffers; i++)
            free.Add(new byte[size]);
        double timeBase = 0;
        bool late = false;

        // With ConvertThreads (by default for HD: DefaultConvertThreads), converting runs on a thread of its own: the
        // decoding thread takes each frame (a reference to its buffers, not a copy) and hands it over through a short
        // queue (at most ~32 MB of frames, 1 to 3). Not with row-wise yadif: taking a frame deinterlaces all its rows,
        // where the conversion computes only the rows it reads.
        int convertThreads = _convertThreads ?? DefaultConvertThreads(_codedSize.W, _codedSize.H);
        bool convertApart = convertThreads > 0 && !(_deinterlace && _yadif is not null);
        ConvertedOnThread = convertApart;
        long frameBytes = (long)_codedSize.W * _codedSize.H * 3 * (_pixelFormat.EndsWith("le", StringComparison.Ordinal) || _pixelFormat.EndsWith("be", StringComparison.Ordinal) ? 2 : 1);
        using var handoff = new BlockingCollection<FrameReader.DecodedFrame>((int)Math.Clamp(HandoffBytes / Math.Max(1, frameBytes), 1, 3));

        // Stops both threads: the caller's token, ours, or the conversion thread failing (the caller then gets its error,
        // not a cancellation).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);

        // A frame written into a free buffer, by the decoding thread or the conversion thread (whichever owns writer).
        (byte[] Buffer, long Pts, bool Bgr) Write(FrameWriter writer, long pts)
        {
            var buffer = free.Take(stop.Token);
            FirstFrameSize ??= (writer.Width, writer.Height);

            // A frame of another size or pixel format than the stream's (4:2:0 in a 4:2:2 stream, or the
            // reverse): OpenCV scales or converts it inside its one conversion to BGR, which FastYuv's conversion
            // of the stream's layout can't reproduce. The core path's conversion for those frames instead (exact;
            // only they are slower).
            bool bgr = IsYuv(pipeline)
                && !(writer.HasStreamSize && writer.PixelFormat == _pixelFormat);
            if (bgr)
                BgrFallbackFrames++;
            switch (bgr ? FramePipeline.SampledBgr : pipeline)
            {
                case FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled or FramePipeline.Yuv420P10Sampled:
                    writer.WriteYuv(buffer,
                        chroma422: pipeline == FramePipeline.Yuv422Sampled,
                        bytesPerSample: pipeline == FramePipeline.Yuv420P10Sampled ? 2 : 1,
                        yuvRows);
                    break;
                case FramePipeline.SampledBgr when bgr:
                    writer.WriteResized(buffer, decoderResizer!, rows!, (CropRegion.X, CropRegion.Y));
                    break;
                case FramePipeline.SampledBgr:
                    writer.WriteRows(buffer, rows!, (CropRegion.X, CropRegion.Width));
                    break;
                case FramePipeline.FullFrameResize:
                    writer.WriteBgr(buffer, CropRegion);
                    break;
                default:
                    writer.WriteScaledBgr(buffer, CropRegion, Width, Height);
                    break;
            }

            return (buffer, pts, bgr);
        }

        // The conversion thread: the taken frames in order, each let go once written.
        void ConvertTaken(long offset)
        {
            bool ended = false;
            try
            {
                using var writer = new FrameWriter(null, _codedSize, Turn);
                foreach (var frame in handoff.GetConsumingEnumerable(stop.Token))
                {
                    using (frame)
                    {
                        writer.Frame = frame;
                        full.Add(Write(writer, frame.Pts + offset), stop.Token);
                    }
                }

                ended = true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                ended = true;
            }
            catch (FrameReader.FrameReaderException e)
            {
                throw InProcess.Map(e, _ffmpegDirectory);
            }
            finally
            {
                if (!ended)
                    stop.Cancel(); // the decoding thread stops too
            }
        }

        var producer = Task.Run(() =>
        {
            Task? converter = null;
            try
            {
                InProcess.Load(_ffmpegDirectory);
                var decoderOptions = new FrameReader.FrameDecoderOptions
                {
                    Threads = _decodeThreads ?? DefaultDecodeThreads(Environment.ProcessorCount),
                    Deinterlace = _deinterlace,
                    RowDeinterlacer = _yadif,
                    InputOptions = InProcessProbe.Options(_inputOptions),
                    CodecOptions = IdctOptions(Codec, _xvidIdct) is [var name, var value] ? new Dictionary<string, string> { [name[1..]] = value } : null,
                    CancellationToken = stop.Token, // the caller's, ours when the caller stops iterating, or the conversion thread failing

                    // Rotated as OpenCV rotates (FrameWriter): by the display matrix's angle alone, after converting.
                    Autorotate = false,
                };
                using var decoder = _stream is null
                    ? new FrameReader.FrameDecoder(_path, decoderOptions)
                    : new FrameReader.FrameDecoder(new PrefixedStream(_prefix, _stream), decoderOptions);
                using var writer = convertApart ? null : new FrameWriter(decoder, _codedSize, Turn);
                timeBase = decoder.TimeBase.Value;

                // Streamed: times from the stream's start, as ffmpeg gives them without -copyts.
                long offset = Streaming ? decoder.StartOffset : 0;
                if (convertApart)
                    converter = Task.Run(() => ConvertTaken(offset));

                // Seek as the command line does: seekBack frames early, then drop every frame before the wanted one's
                // pts; landed after it (MPEG-TS), again from further back (below). Not when the packets carry no pts
                // (MPEG-PS): there libavformat's seek lands after the wanted frame (no index to go by), so decode from
                // the start and drop.
                long? from = startFrame > 0 && startFrame < _pts.Length ? _pts[startFrame] : null;
                bool seeked = from is not null && startFrame - seekBack > 0 && !_ptsFromFrames;
                if (seeked)
                    decoder.Seek(_pts[startFrame - seekBack]);
                int emitted = 0;
                bool before = false; // a frame decoded before the wanted one
                while ((count is null || emitted < count) && decoder.Next())
                {
                    long pts = decoder.Pts;
                    if (from is { } f && pts < f)
                    {
                        before = true;
                        continue;
                    }

                    // Late, or on the wanted frame itself (a keyframe) while deinterlacing: yadif then has no previous
                    // frame for it and gives other pixels than the sequential read (a join of concatenated copies).
                    if (emitted == 0 && seeked && (SeekLandedLate(pts * timeBase, startFrame) || (_deinterlace && !before)))
                    {
                        late = true;
                        break;
                    }

                    if (writer is null)
                    {
                        var frame = decoder.TakeFrame();
                        try
                        {
                            handoff.Add(frame, stop.Token);
                        }
                        catch
                        {
                            frame.Dispose();
                            throw;
                        }
                    }
                    else
                    {
                        full.Add(Write(writer, pts + offset), stop.Token);
                    }

                    emitted++;
                }

                // No frame at all: the seek landed after the last keyframe.
                late |= seeked && emitted == 0;
                if (decoder.InputError is { } e)
                    throw new ShotDetectionException(ShotDetectionError.DecodeFailed, $"Reading the stream failed: {e.Message}", e);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (FrameReader.FrameReaderException e)
            {
                throw InProcess.Map(e, _ffmpegDirectory);
            }
            finally
            {
                // The conversion thread converts what was handed over, then ends; whatever it left (stopped) is let go.
                handoff.CompleteAdding();
                try
                {
                    converter?.Wait();
                }
                catch (AggregateException)
                {
                } // thrown below, unless decoding failed first

                while (handoff.TryTake(out var left))
                    left.Dispose();
                full.CompleteAdding();
            }

            converter?.GetAwaiter().GetResult(); // the conversion thread's error
        });

        try
        {
            byte[]? previous = null;
            foreach (var (buffer, pts, bgr) in full.GetConsumingEnumerable(cancel.Token))
            {
                if (previous is not null)
                    free.Add(previous);
                previous = null;
                if (Streaming)
                {
                    _timeBase = timeBase;
                    _livePts.Add(pts);
                }
                else
                {
                    _livePackets.Add(PacketOf(pts * timeBase, _livePackets.Count > 0 ? _livePackets[^1] : startFrame - 1));
                }

                // Resized already, in a buffer sized for the YUV planes: the frame's own bytes (scores count its pixels).
                if (bgr)
                {
                    buffer.AsSpan(0, small.Length).CopyTo(small);
                    free.Add(buffer);
                    yield return small;
                    continue;
                }

                switch (pipeline)
                {
                    case FramePipeline.SampledBgr:
                        resizer!.ResizePackedRows(buffer, small);
                        break;
                    case FramePipeline.Yuv420Sampled or FramePipeline.Yuv422Sampled or FramePipeline.Yuv420P10Sampled:
                        resizer!.ResizeYuv(buffer, small, YuvForVideo!, _codedSize.W, _codedSize.H, Turn, CropRegion.X, CropRegion.Y);
                        break;
                    case FramePipeline.FullFrameResize:
                        resizer!.Resize(buffer, small);
                        break;
                    default: // scaled by ffmpeg's scale
                        previous = buffer; // the frame itself: back to the pool once the caller is done with it
                        yield return buffer;
                        continue;
                }

                free.Add(buffer);
                yield return small;
            }

            producer.GetAwaiter().GetResult(); // decoding errors
        }
        finally
        {
            cancel.Cancel();
            try
            {
                producer.Wait();
            }
            catch (AggregateException)
            {
            } // the decoder is disposed on its thread
        }

        if (late)
        {
            foreach (var frame in FramesInProcess(startFrame, count, cancellationToken, SeekBackAgain(seekBack)))
                yield return frame;
        }
    }
}
