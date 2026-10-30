namespace ShotDetector;

/// <summary>
/// What only a decoded input's run does (a file's or a Stream's), apart from the steps every run shares
/// (<see cref="Steps"/>): those reach it through <see cref="IDecodedInput"/>, whose one implementation only the file and
/// Stream entry points create. Frames from the caller and <see cref="Merge"/> never reach FFmpeg's code that way, so a
/// trimmed app on them alone (browser WebAssembly) leaves it out, FFmpeg.AutoGen with it (ci.yml's wasm-trim job).
/// </summary>
public static partial class ShotDetection
{
    /// <summary>A decoded input's part of the run: its reader, the reference-tone trim, scoring in chunks.</summary>
    interface IDecodedInput : IDisposable
    {
        /// <summary>The file or URL; null for a Stream (even one spilled to a temporary file).</summary>
        string? Path { get; }

        /// <summary>Whether it can be read in chunks: a file, not a Stream.</summary>
        bool Chunkable { get; }

        /// <summary>The reader (a Stream with its headers last goes to a temporary file first, deleted on Dispose).</summary>
        VideoReader Open(DetectionOptions o, CancellationToken cancellationToken);

        /// <summary><see cref="FindReferenceTone"/> on the file.</summary>
        IReadOnlyList<FrameReader.ToneSegment> ReferenceTone(DetectionOptions o, CancellationToken cancellationToken);

        /// <summary><see cref="ScoreChunked"/> on the file.</summary>
        (List<double?>[] Metrics, int Frames, (Stats? Stats, int From, int To)[] Stats)? ScoreChunked(DetectionOptions o,
            VideoReader video,
            IReadOnlyList<DetectorSettings> settings,
            int minSceneLen,
            bool collectStats,
            int chunks,
            Action? onFrame,
            out string? fallback,
            CancellationToken cancellationToken);
    }

    /// <summary>A file (or URL) to decode, or a Stream.</summary>
    sealed class DecodedInput(string? path, Stream? stream) : IDecodedInput
    {
        TempFile? _spilled;

        public string? Path => path;

        public bool Chunkable => path is not null && _spilled is null;

        public VideoReader Open(DetectionOptions o, CancellationToken cancellationToken)
        {
            if (stream is null)
                return new VideoReader(path!, o, cancellationToken);

            // A streamed mp4 without "faststart" (headers last) can't be read from a pipe: it goes to a temporary file
            // first, read as a file and deleted at the end. Other streams stream.
            byte[] prefix = VideoReader.ReadPrefix(stream, o.ProbeBytes, cancellationToken);
            if (!VideoReader.HeadersLast(prefix))
                return new VideoReader(stream, prefix, o, cancellationToken);
            _spilled = VideoReader.Spill(prefix, stream, cancellationToken);
            return new VideoReader(_spilled.Path, o, cancellationToken);
        }

        public IReadOnlyList<FrameReader.ToneSegment> ReferenceTone(DetectionOptions o, CancellationToken cancellationToken) =>
            FindReferenceTone(path!, o, cancellationToken);

        public (List<double?>[] Metrics, int Frames, (Stats? Stats, int From, int To)[] Stats)? ScoreChunked(DetectionOptions o,
            VideoReader video,
            IReadOnlyList<DetectorSettings> settings,
            int minSceneLen,
            bool collectStats,
            int chunks,
            Action? onFrame,
            out string? fallback,
            CancellationToken cancellationToken) =>
            ShotDetection.ScoreChunked(path!, o, video, settings, minSceneLen, collectStats, chunks, onFrame, out fallback, cancellationToken);

        public void Dispose() => _spilled?.Dispose();
    }
}
