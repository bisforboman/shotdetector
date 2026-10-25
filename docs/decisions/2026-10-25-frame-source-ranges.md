# Frame-source detection in ranges, for hosts (2026-10-25)

### Question

shotdetector-web decodes in the browser, where decoding is ~80 % of a run (measured 2026-10-24, wasm AOT under Node, the
Sintel trailer). Splitting a long video across several Web Workers, each with its own single-threaded .NET runtime and no
shared memory, would give about 2.5-3.5x on 4 cores, but the frame-source `Detect` reads one video start to end. The file
path already scores chunks in parallel and merges them exactly (docs/parallel-detection.md). What API should let a host do
the same with frames it decodes itself?

### Choices

1. **Score ranges, then merge**: `ShotDetection.ScoreRange(YuvVideo, frames, firstFrame, options)` returns a
   `RangeScores` with `ToBytes`/`FromBytes` (compact, versioned binary for postMessage), and
   `ShotDetection.Merge(YuvVideo, ranges, options)` returns the `DetectionResult`.
2. **The same, plus a `PlanRanges` helper** that picks the range starts from the keyframes.
3. **The same, with JSON** for the ranges' scores instead of binary.

### Answer

**1** (the owner, 2026-10-25). Details settled while building it:

- **The range's end and its overlap frame.** The caller gives a range's frames from `firstFrame - 1` (the previous frame,
  which every score compares with; none for the first range) to one frame past the range's end, or to the video's end.
  ScoreRange doesn't decide where the range ends: it scores every frame from `firstFrame` on. Merge does: a range ends
  where the next one starts, and its frames from there on are overlap. Each overlap frame's score must equal the next
  range's for it, detector by detector (`Equals`, as the chunked file path compares), and so must its pts, including
  the next range's frame before its first; then the range's frames past the next start are dropped. A range given
  several frames past the next start is checked on all of them. A range that ends exactly where the next starts (no
  overlap frame) is refused, as is a gap, ranges out of order, and a first range not at frame 0.
- **Frames are identified by pts.** With `YuvVideo.TimeBase` set, Merge runs the frame-source path's constant-rate check
  over every frame of every range, from frame 0's pts (the same helper Detect uses per frame), so frames given under
  another `firstFrame` than theirs, or a range missing a frame, are refused with the frame and range named. A range's
  own frames aren't checked while it is scored (that needs frame 0's pts), except the first range's, as Detect checks
  them. Without a time base, frames are identified by `firstFrame` alone, and only the overlap scores catch a wrong one.
- **Keyframes.** The host picks the range starts: a real decoder starts at a keyframe, so either the keyframe is the
  frame before the range (`firstFrame` = keyframe + 1), or the host decodes from an earlier keyframe and drops what
  comes before `firstFrame - 1`. The frames a range is given must be exactly those a sequential decode gives there; the
  overlap check catches the usual ways they aren't (a range not starting at a keyframe, an open GOP's leading frames, a
  decoder depending on earlier frames). With no sequential pass to fall back on, as the file path has, a mismatch is a
  `ShotDetectionException` (InvalidInput) naming the frame.
- **The same video and options.** Each RangeScores carries a fingerprint: every `YuvVideo` property and every resolved
  option that changes a score or a decision (the detectors' settings and `Detectors`, crop, downscale, the frame rate
  override, compatibility, stats, drop/merge of short shots, the converter's type), one `name=value` line each,
  invariant culture. Merge refuses a range whose fingerprint differs from its own, quoting the first differing line.
  Callbacks and what only decoding reads (the decoder, threads, Mode, ...) are left out; a test checks that every
  option of `DetectionOptions`, `DetectorSettings` and `YuvVideo` is in one list or the other.
- **Refused options** (an `ArgumentException`, in ScoreRange and Merge): the frame-source Detect's refusals (no
  `Yuv420Converter`, Deinterlace, Yadif, FfmpegResize, TrimReferenceTone), plus what a range can't honour on its own: a
  time range (StartTime, EndTime, Duration and their typed forms; ranges cover the whole video from frame 0), FrameSkip
  (its phase runs across ranges) and `DetectionMode.Parallel` (the host does the chunking; Detect would add a "read
  sequentially" warning the merge couldn't honestly repeat). Every detector is fine: each metric needs at most the
  previous frame, which the frame before the range gives, and threshold's dedupe of stats by position stays in the
  decisions, which run once over everything in Merge.
- **One code path.** Merge runs the unchanged `Steps` with the ranges' scores in place of the frames (as the chunked file
  path's scores): the decisions, the stats merge, PostProcess, MergeLastScene/DropShortScenes, OnShot and the result
  are the code Detect runs. ScoreRange calls each detector's `Metric` as a chunk does. Detect and the file path's
  chunks are unchanged.
- **OnShot and Progress.** Merge tells `OnShot` the shots as Detect tells them (as each cut is confirmed with one
  detector and neither DropShortScenes nor MergeLastScene, else all at the end), all before it returns; it reports no
  `Progress`. ScoreRange tells `Progress` its range's frames (`FramesProcessed` from `firstFrame` on, `ExpectedFrames`
  0 as the range ends where the frames stop; a first report before any frame, the last once they all are, and with `ProgressStep` set only those two) and never
  `OnShot`. `Warnings`: the ranges' warnings (none so far) are told again by Merge and listed once each in the result.
- **The bytes** (`RangeScores.ToBytes`/`FromBytes`), little-endian: "SDRS", a 16-bit format version (1), the
  fingerprint (UTF-8, length-prefixed), `firstFrame`, the scored frame count, the frames' pts (from the frame before),
  each detector's scores (a byte for null or not, then the double's 64 bits, so NaN payloads and -0.0 survive), the stats
  (a key table, then frame, key and the value's bits per entry), the warnings, and an FNV-1a 64-bit checksum of
  everything before it. FromBytes refuses, with a `ShotDetectionException` (InvalidInput), bytes without the magic,
  another version (naming both), truncated or damaged bytes (the checksum), and anything inconsistent (counts past the
  end, bytes left over, a detector count over 1024), before allocating for a count. Never BinaryFormatter.
- **Async.** `ScoreRangeAsync` over `IAsyncEnumerable<YuvFrame>` runs the same steps, awaited, on no other thread, as
  `DetectAsync` (2026-10-24-frame-source-async.md). Merge reads no frames, so it has no async form.

Proved by FrameRangeTests (every detector, with and without stats, several detectors, drop and merge of short shots,
0.7.1, untimed frames, crop and downscale, split whole, in 2 and 3, unevenly, with a one-frame range, at a cut, with the
last range one frame long and with more overlap frames: through the bytes, the shots, cuts, scene and stats CSVs, frame
count and every position equal Detect's; the refusals; the bytes; the fingerprint's coverage; async; OnShot and Progress),
FrameInputTests (H.264 with B-frames and an mpeg4 clip split at their keyframes, each range decoded after a seek to its
keyframe) and tests/FramesWithoutFfmpeg (three ranges, one awaited, merged: nothing of FFmpeg loads).
