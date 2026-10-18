### New

- `DetectedVideo.ChunkFallback`: why scoring in chunks fell back to a sequential read (a chunk's frame at another
  position, overlap scores that differ, ...), also a `DetectionResult.Warnings` entry that the CLI prints. Before, a
  fallback was silent and looked like a run that never chunked.
- `Mode = DetectionMode.Parallel` (CLI `--parallel`, `--chunks`) warns when it reads the file sequentially all the
  same, and why: fewer than 4 CPUs, shorter than two minutes, a time range, frame skip, the ffmpeg executable, a
  stream, MPEG-PS without timestamps.

### Fixed

- Parallel detection: decoder threads that don't divide evenly among the chunks are no longer lost (`--threads 5` in 2
  chunks decoded with 2 + 2, now 3 + 2), and `DecodeThreads = 0` (FFmpeg's choice) stays FFmpeg's choice in each chunk
  instead of becoming 1 thread.
