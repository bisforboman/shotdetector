# Ranges: deinterlace (2026-10-31)

Issue #628, from shotdetector-web, by way of the frame-source parity audit (docs/frame-source-parity.md): a
deinterlaced video ran on one worker, with no split run and no resume across visits, because `ScoreRange` and `Merge`
refused deinterlacing. yadif reads each frame's previous and next frame, so a range's edges need frames beyond those
it already takes: the frame before the range's first (which its first score compares with) needs its own previous
frame, and the range's last frame needs its next.

## Question

How does `ScoreRange` get the extra frames?

## Choices

1. **Implicit: one more each side**. No new API: when deinterlacing, a range's frames start two frames before
   `firstFrame` (or at 0) and run one frame past the overlap frame. The range's last frame is deinterlaced as the
   video's last (its own next), so Merge takes each range's frames up to the next range's start and leaves that last
   frame out of the overlap check. A range without the extra frame is refused.
2. **Explicit parameter**: a `ScoreRange` overload saying how many context frames were given before and after (for
   example, whether the range ends at the video's end). More API, nothing inferred.

## Answer

**Implicit: one more each side** (the owner, 2026-10-31). Which frames each range takes, deinterlaced or not, a host
then gets from `ShotDetection.RangeFrames` (docs/decisions/2026-10-31-range-frames.md).

## How

- `ScoreRange` reads from `firstFrame - 2` when deinterlacing. The frames before the range's first are taken one per
  step (`TakesBeforeFrame`), so an asynchronous source is awaited between them. Its scores and pts start at
  `firstFrame - 1` as before: the bytes' format doesn't change.
- `Merge` drops each range's last frame unless that range ends with the video (the last range's end). The overlap check
  then compares up to the frame before it, so a range given only one frame past its end leaves no overlap frame and is
  refused ("without range N's first frame to compare"). A range continuing from earlier ones drops the earlier ranges'
  last frame the same way, so it starts at least two frames before where a stopped run's frames ended.
- `OnShot` needs nothing more. The range reads one frame ahead, as Detect does, so its last frame is scored after its
  input has ended, and the shots that frame decides are told as a stopped range's last shots, which a resumed range
  tells again. A range from frame 0 tells Detect's shots after the same frames.
- `Deinterlace` and `Yadif` are in the fingerprint, written only when the video is deinterlaced: ranges scored and merged
  with different deinterlacing are refused, and the fingerprints of ranges that don't deinterlace are unchanged.
