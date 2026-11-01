# RangeScores: where a range's scores end (2026-11-01)

From shotdetector-web, after #628 (docs/decisions/2026-10-31-deinterlaced-ranges.md). With deinterlacing, a range's
last frame isn't final: yadif deinterlaced it with itself as the next frame. So `Merge`, and a range continuing from
earlier ones, count a range's scores as ending one frame before the last frame it was given. A resumed deinterlaced
MJPEG run (every frame a keyframe) was refused with "The earlier ranges end at frame 38, without this range's first
frame (39)". The page had resumed at the last frame scored. The refusal is the designed rule, a resumed range needs one
final overlap frame to compare, not a bug. But nothing public said where the final scores end, so the page worked it
out from `RangeFrames`' margins.

## Question

What should `RangeScores` expose?

## Choices

1. **`EndFrame`, exclusive**: one past the last frame whose scores are final (`FirstFrame + FrameCount`, minus 1 when
   deinterlacing). The next or resumed range starts before it, at `EndFrame - 1` at the latest (the overlap frame). A
   range given `RangeFrames(first, end)`' span has `EndFrame` end + 1 either way. Merge's refusals name it.
2. **`LastFrame`, inclusive**: the last frame whose scores are final; the next range starts at or before it.
3. **Both**.

## Answer

**`EndFrame`, exclusive** (the owner, 2026-11-01).

## How

- `RangeScores.EndFrame` is computed from the scores: the range's fingerprint says whether it deinterlaced, and the
  bytes' format doesn't change. For the video's last range, deinterlaced, it is one less than what Merge takes: Merge
  keeps the last range's last frame, the video's last.
- Merge's "without range N's first frame" and ScoreRange's "without this range's first frame" refusals say the frame
  to start at, at the latest.
- Tests: `FrameRangeTests.RunsResumeAtTheEarlierRangesEndFrame` (progressive and deinterlaced: a spanned range's
  `EndFrame`, through its bytes, and runs stopped anywhere, resumed at `EndFrame - 1`, merging into Detect's result,
  one frame later refused) and `FrameInputTests.DeinterlacedRunsOfAnAllKeyframeFileResumeAtEndFrame` (an MJPEG file,
  each run decoded from its own start: the sequential result and the file's).
