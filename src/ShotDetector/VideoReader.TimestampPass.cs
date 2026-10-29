using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace ShotDetector;

internal sealed partial class VideoReader
{
    /// <summary>
    /// ffprobe's decoding pass for every frame's timestamp (MPEG-PS: packets without pts), its values handed over as
    /// ffprobe prints them: run first (<see cref="FrameTimestamps"/>), or alongside the executable's read from the start,
    /// whose frames take their labels from the values so far (<see cref="PacketOfFromPass"/>). Cancelling the token it
    /// was started with kills ffprobe.
    /// </summary>
    internal sealed class TimestampPass : IDisposable
    {
        readonly string _ffprobe;
        readonly Process _proc;
        readonly BlockingCollection<long> _values = [];
        readonly Task<string> _stderr;
        readonly Task _stdout;
        readonly CancellationTokenRegistration _kill;
        bool? _exited; // whether ffprobe had exited when disposed

        public TimestampPass(string ffprobe, string path, string[] inputOptions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ffprobe = ffprobe;
            _proc = Start(new ProcessStartInfo(ffprobe, FrameTimestampsArgs(path, inputOptions)) { RedirectStandardOutput = true, RedirectStandardError = true });
            _kill = cancellationToken.Register(() =>
            {
                Cancelled = true;
                Kill(_proc);
            });
            _stderr = _proc.StandardError.ReadToEndAsync(CancellationToken.None);

            // Each frame's timestamp as OpenCV takes it: its pts when set and non-zero, else its pkt_dts (0 without one).
            _stdout = Task.Run(() =>
            {
                try
                {
                    long? framePts = null;
                    string? line;
                    while ((line = _proc.StandardOutput.ReadLine()) is not null)
                    {
                        line = line.Trim();
                        if (line.StartsWith("pts="))
                        {
                            framePts = long.TryParse(line[4..], CultureInfo.InvariantCulture, out long p) && p != 0 ? p : null;
                        }
                        else if (line.StartsWith("pkt_dts="))
                        {
                            _values.Add(framePts ?? (long.TryParse(line[8..], CultureInfo.InvariantCulture, out long d) ? d : 0));
                            framePts = null;
                        }
                    }
                }
                finally
                {
                    _values.CompleteAdding();
                }
            });
        }

        /// <summary>Whether the token it was started with was cancelled (ffprobe killed: its values are cut short).</summary>
        public bool Cancelled { get; private set; }

        /// <summary>Tests: whether ffprobe has exited.</summary>
        internal bool HasExited => _exited ?? _proc.HasExited;

        /// <summary>
        /// Adds the next frame's timestamp to <paramref name="values"/>, waiting for ffprobe if it hasn't printed it yet;
        /// false at the end of the pass (every timestamp taken), after ffprobe exited cleanly (its failure throws as the
        /// pass run first does, <see cref="Run"/>'s), or once a <see cref="Cancelled"/> pass has none left.
        /// </summary>
        public bool TakeNext(List<long> values, CancellationToken cancellationToken)
        {
            if (_values.TryTake(out long value, Timeout.Infinite, cancellationToken))
            {
                values.Add(value);
                return true;
            }

            _stdout.Wait(cancellationToken);
            _proc.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (Cancelled)
                return false;
            if (_proc.ExitCode != 0)
                throw new ShotDetectionException(ShotDetectionError.InvalidInput, $"{_ffprobe} failed ({_proc.ExitCode}): {_stderr.Result}");
            return false;
        }

        /// <summary>Kills ffprobe if it is still running.</summary>
        public void Dispose()
        {
            _kill.Dispose();
            Kill(_proc);
            try
            {
                _stdout.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _exited = _proc.WaitForExit(5000);
            _proc.Dispose();
            _values.Dispose();
        }
    }
}
