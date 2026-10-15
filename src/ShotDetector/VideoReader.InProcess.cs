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

    /// <summary>Frames(startFrame, count) decoded in-process: the same bytes per pipeline as the ffmpeg pipe, then the same resize.</summary>
    IEnumerable<byte[]> FramesInProcess(int startFrame, int? count, CancellationToken cancellationToken)
    {
        var pipeline = Pipeline;
        var resizer = pipeline != FramePipeline.FfmpegScale ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        _livePackets.Clear();
        _liveStart = startFrame;
        int size = pipeline switch
        {
            // Frames of a new size come resized already (below), so room for those too.
            FramePipeline.Yuv420Sampled => Math.Max(IYuv420Converter.FrameSize(SourceWidth, SourceHeight), Width * Height * 3),
            FramePipeline.FullFrameResize => CropRegion.Width * CropRegion.Height * 3,
            _ => Width * Height * 3,
        };

        // The rows the resize reads, with the crop's offset: the only ones converted. The decoding thread resizes those
        // frames itself (straight from the converter's rows, no copy), with its own CvResize: the caller's resizes the
        // FastYuv frames meanwhile, and each keeps row buffers.
        int[]? rows = resizer?.SourceRows.Select(r => r + CropRegion.Y).ToArray();
        var decoderResizer = resizer is null ? null : new CvResize(CropRegion.Width, CropRegion.Height, Width, Height);
        var small = new byte[Width * Height * 3];

        // Decoding and converting run on their own thread and the resize and scoring on the caller's, overlapped as
        // with the executable (where ffmpeg is the other side). Eight buffers, so neither side waits for the other on
        // every frame (with three, the handoffs cost as much as the decoding on small video). Cancelled by the
        // caller's token, or by us when the caller stops iterating.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var free = new BlockingCollection<byte[]>();
        using var full = new BlockingCollection<(byte[] Buffer, long Pts, bool Bgr)>();
        for (int i = 0; i < 8; i++)
            free.Add(new byte[size]);
        double timeBase = 0;
        var producer = Task.Run(() =>
        {
            try
            {
                InProcess.Load(_ffmpegDirectory);
                var decoderOptions = new FrameReader.FrameDecoderOptions
                {
                    Threads = _decodeThreads ?? DefaultDecodeThreads(Environment.ProcessorCount),
                    Deinterlace = _deinterlace,
                    RowDeinterlacer = _yadif,
                    InputOptions = InProcessProbe.Options(_inputOptions),

                    // Rotated as OpenCV rotates (FrameWriter): by the display matrix's angle alone, after converting.
                    Autorotate = false,
                };
                using var decoder = _stream is null
                    ? new FrameReader.FrameDecoder(_path, decoderOptions)
                    : new FrameReader.FrameDecoder(new PrefixedStream(_prefix, _stream), decoderOptions);
                using var writer = new FrameWriter(decoder, Turn);
                timeBase = decoder.TimeBase.Value;

                // Streamed: times from the stream's start, as ffmpeg gives them without -copyts.
                long offset = Streaming ? decoder.StartOffset : 0;

                // Seek as the command line does: two frames early, then drop every frame before the wanted one's pts.
                // Not when the packets carry no pts (MPEG-PS): there libavformat's seek lands after the wanted frame (no
                // index to go by), so decode from the start and drop.
                long? from = startFrame > 0 && startFrame < _pts.Length ? _pts[startFrame] : null;
                if (from is not null && startFrame - 2 > 0 && !_ptsFromFrames)
                    decoder.Seek(_pts[startFrame - 2]);
                int emitted = 0;
                while ((count is null || emitted < count) && decoder.Next())
                {
                    long pts = decoder.Pts;
                    if (from is { } f && pts < f)
                        continue;
                    var buffer = free.Take(cancel.Token);

                    // A frame of another size than the stream's first: OpenCV scales it to that size inside its one
                    // conversion to BGR, which FastYuv's conversion at the stream's size can't reproduce. The core
                    // path's conversion for those frames instead (exact; only they are slower).
                    bool bgr = pipeline == FramePipeline.Yuv420Sampled && !writer.HasFirstSize;
                    switch (bgr ? FramePipeline.SampledBgr : pipeline)
                    {
                        case FramePipeline.Yuv420Sampled:
                            writer.WriteYuv420(buffer);
                            break;
                        case FramePipeline.SampledBgr:
                            writer.WriteResized(buffer, decoderResizer!, rows!, (CropRegion.X, CropRegion.Y));
                            break;
                        case FramePipeline.FullFrameResize:
                            writer.WriteBgr(buffer, CropRegion);
                            break;
                        default:
                            writer.WriteScaledBgr(buffer, CropRegion, Width, Height);
                            break;
                    }

                    full.Add((buffer, pts + offset, bgr), cancel.Token);
                    emitted++;
                }

                if (decoder.InputError is { } e)
                    throw new ShotDetectionException(ShotDetectionError.DecodeFailed, $"Reading the stream failed: {e.Message}", e);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
            }
            catch (FrameReader.FrameReaderException e)
            {
                throw InProcess.Map(e, _ffmpegDirectory);
            }
            finally
            {
                full.CompleteAdding();
            }
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

                // Resized already, in a buffer sized for yuv420p: the frame's own bytes (scores count its pixels).
                if (bgr)
                {
                    buffer.AsSpan(0, small.Length).CopyTo(small);
                    free.Add(buffer);
                    yield return small;
                    continue;
                }

                switch (pipeline)
                {
                    case FramePipeline.Yuv420Sampled:
                        resizer!.ResizeYuv420(buffer, small, _yuv420ForVideo!, SourceWidth, SourceHeight, CropRegion.X, CropRegion.Y);
                        break;
                    case FramePipeline.FullFrameResize:
                        resizer!.Resize(buffer, small);
                        break;
                    default: // resized already (SampledBgr) or by ffmpeg's scale
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
    }
}
