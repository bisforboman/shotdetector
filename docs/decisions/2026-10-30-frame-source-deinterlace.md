# Frame sources: deinterlacing, how frames carry their field flags (2026-10-30)

Issue #614, from shotdetector-web (its "Interlaced H.264" row): `Detect(YuvVideo, IEnumerable<YuvFrame>, ...)` refused
`Deinterlace` (FFmpeg's yadif filter, which this path doesn't have), so `--deinterlace auto`, the setting for broadcast
material, had no frame-source equivalent, and `DetectedVideo.FieldOrder` and `Deinterlaced` couldn't be a file's. The
issue proposed `YuvVideo.FieldOrder`, per-frame flags for yadif's automatic parity, and `Deinterlace` accepted with
`Yadif` (FastYuv's row-wise port), the frames held as FrameDecoder's row-wise path holds them.

## Questions

1. How does each `YuvFrame` carry what yadif's automatic parity reads?
2. When is a missing `DetectionOptions.Yadif` an `ArgumentException`?

## Choices

1. - **Two bools**: `YuvFrame.Interlaced` and `YuvFrame.TopFieldFirst`, init properties like `Pts`, default false:
     FFmpeg's `AV_FRAME_FLAG_INTERLACED` and `AV_FRAME_FLAG_TOP_FIELD_FIRST` (ffprobe's `interlaced_frame`,
     `top_field_first`) copied as they are.
   - **One nullable bool**: `YuvFrame.TopFieldFirst` as `bool?`, null when not flagged interlaced; one property, but
     the caller maps FFmpeg's two flags into it.
2. - **Only if it deinterlaces**: `On`, or `Auto` with an interlaced `YuvVideo.FieldOrder`; `Auto` on a progressive or
     unknown field order runs without `Yadif`, as `Off`, so a caller can always pass `Auto`.
   - **Whenever not Off**: the same error up front for every video.

## Answers

1. **Two bools** (the owner, 2026-10-30).
2. **Only if it deinterlaces** (the owner, 2026-10-30).

## How

- `YuvVideo.FieldOrder` (`FrameReader.FieldOrder`, default `Unknown`) resolves `Auto` as a file's stream does and is
  reported in `DetectedVideo.FieldOrder`; it is in `Merge`'s fingerprint.
- Deinterlaced as FrameDecoder's row-wise yadif: output k from frames k-1, k and k+1, frame 0 its own previous and the
  last its own next; the parity from frame k's flags (`tff = !Interlaced || TopFieldFirst`); only the rows the
  conversion reads; frame k's time (yadif mode 0, one frame out per frame in). The reader copies each frame's planes as
  it takes it (`YuvFrame` lets the caller reuse its buffers once the enumeration moves on), keeping the last three.
- The frame after the one converted is taken first, so the run's start takes one more frame before its first
  (`TakesBeforeStart`): `DetectAsync`'s driver still puts one frame in the feed per step.
- With `YuvVideo.Rotation`, deinterlaced before turning, as the file path (`-noautorotate`, yadif, then the turn).
- Ranges (`ScoreRange`, `Merge`) refuse it when it would deinterlace: a range would need the frames on either side.
- Tests: FrameInputTests on libx264 MBAFF clips with top-first and bottom-first frames, and MPEG-2, against
  `Detect(path)` deinterlacing in-process with `Yadif` (Auto, On, adaptive, hash, a start time, frame skip, no resize,
  rotated), the flags read per frame with ffprobe; synthetic checks of the parity, of reused buffers and of `Auto`.
