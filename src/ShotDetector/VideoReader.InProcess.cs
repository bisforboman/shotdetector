namespace ShotDetector;

public sealed partial class VideoReader
{
    /// <summary>
    /// Whether frames are decoded in this process (<see cref="VideoDecoder.InProcess"/>, or <see cref="VideoDecoder.Auto"/>
    /// with FFmpeg 8's libraries available) rather than by the ffmpeg executable. Inputs it doesn't handle yet use the
    /// executable: streams and URLs, image sequences, rotated video, and deinterlacing.
    /// </summary>
    public bool DecodesInProcess => _decoder != VideoDecoder.FfmpegProcess && _stream is null && !Streaming && _inputOptions.Length == 0
        && _rotation == 0 && !_deinterlace && (_decoder == VideoDecoder.InProcess || InProcessDecoder.CanLoad(_ffmpegDirectory));

    /// <summary>Frames(startFrame, count) decoded in-process: the same bytes per pipeline as the ffmpeg pipe, then the same resize.</summary>
    IEnumerable<byte[]> FramesInProcess(int startFrame, int? count, CancellationToken cancellationToken)
    {
        var pipeline = Pipeline;
        var resizer = pipeline != FramePipeline.FfmpegScale ? new CvResize(CropRegion.Width, CropRegion.Height, Width, Height) : null;
        bool live = Compatibility != PySceneDetectVersion.V0_6_4;
        _livePackets.Clear();
        _liveStart = startFrame;
        int size = pipeline switch
        {
            FramePipeline.Yuv420Sampled => IYuv420Converter.FrameSize(SourceWidth, SourceHeight),
            FramePipeline.SampledBgr => resizer!.SourceCols.Length * resizer.SourceRows.Length * 3,
            FramePipeline.FullFrameResize => CropRegion.Width * CropRegion.Height * 3,
            _ => Width * Height * 3,
        };
        // The sampled pixels, with the crop's offset, as the remap maps hold them.
        int[]? cols = resizer?.SourceCols.Select(c => c + CropRegion.X).ToArray();
        int[]? rows = resizer?.SourceRows.Select(r => r + CropRegion.Y).ToArray();
        byte[] raw = new byte[size], small = new byte[Width * Height * 3];

        using var decoder = new InProcessDecoder(_path, _ffmpegDirectory, _decodeThreads ?? DefaultDecodeThreads(pipeline, Environment.ProcessorCount, inProcess: true));
        // Seek as the command line does: two frames early, then drop every frame before the wanted one's pts.
        long? from = startFrame > 0 && startFrame < _pts.Length ? _pts[startFrame] : null;
        if (from is not null && startFrame - 2 > 0)
            decoder.Seek(_pts[startFrame - 2]);
        int emitted = 0;
        while ((count is null || emitted < count) && decoder.Next())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long pts = decoder.Pts;
            if (from is { } f && pts < f)
                continue;
            if (live)
                _livePackets.Add(PacketOf(pts * decoder.TimeBase, _livePackets.Count > 0 ? _livePackets[^1] : startFrame - 1));
            emitted++;
            switch (pipeline)
            {
                case FramePipeline.Yuv420Sampled:
                    decoder.WriteYuv420(raw);
                    resizer!.ResizeYuv420(raw, small, _yuv420ForVideo!, SourceWidth, SourceHeight, CropRegion.X, CropRegion.Y);
                    yield return small;
                    break;
                case FramePipeline.SampledBgr:
                    decoder.WriteSampledBgr(raw, cols!, rows!);
                    resizer!.ResizeSampled(raw, small);
                    yield return small;
                    break;
                case FramePipeline.FullFrameResize:
                    decoder.WriteBgr(raw, CropRegion);
                    resizer!.Resize(raw, small);
                    yield return small;
                    break;
                default:
                    decoder.WriteScaledBgr(raw, CropRegion, Width, Height);
                    yield return raw;
                    break;
            }
        }
    }
}
