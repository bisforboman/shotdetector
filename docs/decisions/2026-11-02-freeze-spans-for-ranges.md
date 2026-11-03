# Black and freeze spans for ranges (2026-11-02)

The backlog's "Black and freeze spans for ranges" (from shotdetector-web, 2026-11-01): every page run is a range, scored
in parallel workers and merged, or resumed from a stopped run's ranges. `ScoreRange` refused `BlackDetect` and
`FreezeDetect`, so the page worked black spans out itself and took no freezes from split or resumed runs. The bar is
FrameReader's PictureMeter's: the ranges of any split join into the whole run's spans, byte for byte.

Black spans needed no choice. blackdetect's judgement of a frame is its own alone, so each range carries one flag per
frame and Merge works the spans out over the joined flags, as Detect's chunks do (docs/decisions/2026-11-01-black-spans.md).
freezedetect compares each frame with the still picture's first, however far back (docs/decisions/2026-11-01-freeze-spans.md).
A range scored on its own can't judge its first frames when a still crosses its start, because the reference picture
is in an earlier range.

## Question

shotdetector-web scores ranges in parallel workers. Which design for freezes?

## Choices

1. **Flags + re-score** (recommended): each range runs freezedetect from the frame before its first and carries one
   bit per frame (within n of the still's first, or not). Merge replays freezedetect over the joined bits, byte for
   byte. If a range starts inside a still (rare: a freeze over a split point), Merge refuses and names the range. The
   host scores it again, given the earlier ranges, from the still's first frame (a RangeFrames overload taking the
   earlier ranges gives that frame). Splits stay parallel, and resumed runs are exact the first time. The still that
   crosses the start is decoded twice.
2. **Carry the still picture**: the same bits, plus each range's still picture at its end in RangeScores (one frame's
   planes, about 3 MB at 1080p). A range starting inside a still is scored again given the earlier ranges, without
   reaching back. The bytes are bigger, and seeding FreezeDetector needs new FrameReader API, so also a FrameReader
   release.
3. **Sequential freezes only**: with FreezeDetect, every range after the first is given the earlier ranges' scores, so
   ranges are scored one after another. Parallel splits refuse FreezeDetect, but black spans still join in any split.

## Answer

**Flags + re-score** (the owner, 2026-11-02).

## How

- **The bits**: a range's detector (FrameReader's `FreezeDetector`) runs at d = 0. Every frame after the first is then
  inside a span (within n) or ends one (not), so `VideoReader.FrozenFlags` reads each frame's comparison off the
  spans; the comparison doesn't depend on d. `RangeScores` carries the black flags, the freeze flags and the freeze
  seed (where the comparisons started: the frame before the range, or the still's first frame).
- **The replay**: Merge runs freezedetect's state machine (its activate, without the SAD) over the joined flags with
  the real d: durations rescaled to microseconds as av_rescale_q rounds, the spans and the still running at the end
  (no end) as Detect gives them. A range's flags are the run's when the run's still picture at the frame before the
  range is the range's seed. Otherwise the range is refused, and `ShotDetection.FirstRangeToScoreAgain(video, ranges,
  options)` names the first such range, so a host need not parse the message.
- **Scoring again**: `ShotDetection.RangeFrames(video, earlier, options, first, end)` gives a range's frames after the
  earlier ranges: from the still's first frame when the range starts inside one (deinterlacing, the frame before it),
  else the plain overload's. `ScoreRange(video, earlier, frames, first, options)` feeds that frame to the detector
  first, takes the frames up to the range's own without scoring them, and refuses timed frames that don't start there.
  The new range takes the old one's place, and the next is checked, as a still can cross more than one start. Each
  range is scored again at most once.
- **Bytes**: `RangeScores` format version 2 (the flags after the warnings, eight to a byte). Ranges without either
  option still write version 1, byte for byte as before, and both versions are read.
- **Fingerprint**: `BlackDetect` and `FreezeDetect` (n and d, the thresholds) are in it only when set, as deinterlacing
  is, so ranges scored without them keep their fingerprint.
- **Deinterlacing**: a range's last frame isn't final, so its flags go with its scores (`WithoutLastFrame`).

## Checks

- `RangeSpansTests.SpansOfAnySplitAreDetects`: a clip with black and stills (one whose picture drifts, so the still's
  first moves on every 19 frames, and one to the end), timed and untimed, deinterlaced, at d = 0, 0.2 s and 2 s. It is
  split every frame its own range, at and around each stretch's first and last frames, inside them, and at random;
  each range scored on its own, then those named scored again, and also one after another as a resumed run. Every
  split's spans equal Detect's (records, lines and durations' bits).
- `FrameInputTests.SameAsTheFileWithBlackSpans` and `SameAsTheFileWithFreezeSpans` (with SHOTDETECTOR_FFMPEG_LIBS):
  decoded H.264 (8-bit, 10-bit, MBAFF deinterlaced) split the same ways, the merged spans equal the frame-source
  Detect's, the in-process file's and the executable's.
- The refusals: a range starting inside a still (Merge, a continuing range given the wrong frames, an earlier range
  to score again first), the fingerprint, and the bytes of each option alone.
