# OnShot for ranges, and for a range continuing from earlier ones (2026-10-30)

Issue #598, from shotdetector-web: the page resumes a stopped run after a reload from its ranges' scores
(`RangeScores.ToBytes()` kept in the browser, a range scored later from where it stopped, `Merge` over both), so every
run that might stop is a `ScoreRangeAsync` from frame 0 rather than a `DetectAsync`. `ScoreRange` didn't tell
`DetectionOptions.OnShot` anything (shots were decided in `Merge`), so the page lost its live shot table. The issue asks
for a range from frame 0 to tell the shots Detect would, as each cut is confirmed (the shots Merge gives for that range
alone, up to where its frames stop), and offers an optional second part: an overload taking the earlier ranges' scores,
so a resumed range tells its shots live too. Without it the page would show the resumed part's shots at the merge.

## Question

Build the range from frame 0 alone, or the continuing overload too?

## Choices

1. **Both**: a range from frame 0 tells its shots, and `ScoreRange`/`ScoreRangeAsync` overloads taking the earlier
   `RangeScores` tell a resumed range's: the same shots as the whole Detect, without repeats.
2. **Frame 0 only**: the resumed part's shots appear at the merge (acceptable to the page, by the issue).

## Answer

**Both** (the owner, 2026-10-30; after #597, docs/decisions/2026-10-30-frame-sources-without-ffmpeg.md).

## How

- A range from frame 0 with `OnShot` set runs Merge's decisions while it scores (`LiveShots`): `Steps` over a live
  `Prescored` whose metrics grow as each frame is scored, a null step waiting for the next, so the decisions are the
  same code as Merge's and Detect's, and each shot is told after the same frame as `DetectAsync` tells it. The scores
  don't change.
- `ScoreRange(video, earlier, frames, firstFrame, options)` (and `ScoreRangeAsync`) checks the earlier ranges as Merge
  would (fingerprints, from frame 0, in order, ending at or after this range's first frame) and every frame they share
  with this range (pts and scores), with or without `OnShot`. The decisions go over the earlier scores to this range's
  first frame and then this range's; the shots confirmed while deciding the earlier ranges' frames aren't told (those
  ranges told them as they scored), the rest are.
- Without repeats means the confirmed shots: what a stopped range tells when its frames end (its last shot, and with
  several detectors, DropShortScenes or MergeLastScene every shot, as Detect tells those at the end) ended where the
  frames did, so the resumed range tells those again, from their `Shot.Number` on, with their real ends. The shots told
  before each run's frames stopped, then all of the last run's, are the whole video's (FrameRangeTests).
- `Merge` still tells `OnShot` too: a page that had its ranges tell their shots leaves it out of Merge's options.
