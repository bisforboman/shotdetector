namespace ShotDetector;

/// <summary>
/// The members every input shares reach the decoders (in-process, the executable, the timestamp pass) only through
/// <see cref="IDecoding"/>, whose one implementation only a decoded input's constructor creates: frames from the caller
/// (<see cref="FromFrames"/>) never get one, so a trimmed app on them alone (browser WebAssembly) leaves FFmpeg's code
/// out (ShotDetection.Decoded.cs has the run's half).
/// </summary>
internal sealed partial class VideoReader
{
    readonly IDecoding? _decoding; // null for frames from the caller

    /// <summary>A decoded input's half of the members every input has.</summary>
    interface IDecoding
    {
        /// <summary><see cref="DecodesInProcess"/>.</summary>
        bool DecodesInProcess { get; }

        /// <summary><see cref="Frames(int, int?, CancellationToken, int)"/>, decoded.</summary>
        IEnumerable<byte[]> Frames(int startFrame, int? count, CancellationToken cancellationToken, int seekBack);

        /// <summary><see cref="TakeTimestampPass"/>.</summary>
        void TakeTimestampPass(CancellationToken cancellationToken);
    }

    /// <summary>A file's, a Stream's or a URL's.</summary>
    sealed class Decoding(VideoReader reader) : IDecoding
    {
        public bool DecodesInProcess => reader.DecodesWithLibraries();

        public IEnumerable<byte[]> Frames(int startFrame, int? count, CancellationToken cancellationToken, int seekBack) =>
            reader.FramesDecoded(startFrame, count, cancellationToken, seekBack, null);

        public void TakeTimestampPass(CancellationToken cancellationToken) => reader.TakeTimestampPass(cancellationToken);
    }
}
