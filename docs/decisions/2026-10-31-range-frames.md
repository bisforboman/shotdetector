# Ranges: which frames a host gives each range (2026-10-31)

From shotdetector-web, during #628. The page copied the internal `VideoReader.DeinterlacesFrames(video, options)` as
`VideoInput.Deinterlaces`, because ranges refused deinterlacing and it had to choose between ranges and one whole
Detect. With #628 ranges deinterlace, but a host still needs the predicate: it decides which frames each range is given,
from `firstFrame - 1` to one past the range's end, or, deinterlacing, from `firstFrame - 2` to two past it. The layouts
can't be mixed: extra frames given to a range that doesn't deinterlace number its frames wrong, which Merge refuses.
Nothing public answered it before a run (`DetectedVideo.Deinterlaced` is known after one).

## Question

How does a host learn which frames to give each range?

## Choices

1. **Make the predicate public**, e.g. `ShotDetection.Deinterlaces(YuvVideo, DetectionOptions)`, and document the
   margins for each case.
2. **A helper that returns a range's frame span**: `ShotDetection.RangeFrames(video, options, firstFrame, endFrame)`
   gives (from, to), covering both margins, so hosts never encode the rule (the existing one frame on each side
   included).
3. **Leave it**: shotdetector-web keeps its copy in step by hand.

## Answer

**The helper** (the owner, 2026-10-31, through the coordinating session).

## How

- `RangeFrames` returns `(From, To)`: the frames `From` to `To` - 1 to give `ScoreRange` for [`firstFrame`, `endFrame`).
  The margin is one frame on each side, two when deinterlacing (`Deinterlace` On, or Auto with an interlaced field
  order), with `From` at least 0. `To` can be past the video's last frame, where the last range's frames simply end.
- It refuses a negative `firstFrame` and an `endFrame` not after it.
- Tests: `FrameRangeTests.RangeFramesGiveTheFramesEachRangeNeeds` (progressive, On, Auto on an interlaced and on a
  progressive field order: ranges built from it merge into Detect's result byte for byte, for every split), and the
  file-backed `FrameInputTests.DeinterlacedRangesFromKeyframesMergeIntoTheSequentialResult` builds its ranges with it.
