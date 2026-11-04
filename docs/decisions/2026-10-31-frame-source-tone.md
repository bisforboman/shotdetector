# Frame sources: the reference tone (2026-10-31)

Issue #629, from the frame-source parity audit (docs/frame-source-parity.md). `TrimReferenceTone` reads a file's audio
with FrameReader's `AudioReader` (FFmpeg) and was refused for frames from the caller ("needs a file path"). Finding the
tone in samples the caller decoded was already there: `FrameReader.ReferenceToneFinder` (#494) gives the segments
`ReferenceTone.Find` gives for the same samples, without FFmpeg. What was missing is a way to hand them to detection.

## Question

Where does a frame-source caller give the tone segments it found, for `TrimReferenceTone`?

## Choices

1. **`YuvVideo.ReferenceTone`** (recommended): an optional `IReadOnlyList<ToneSegment>?` on the video, what the caller
   found in its audio. `TrimReferenceTone` trims by it as the file path does (`TrimmedToTone`) and reports it in
   `DetectionResult.ReferenceTone`; refused without it. It describes the media, like `FrameTimestamps`, and the
   options stay the file path's.
2. **`DetectionOptions.ToneSegments`**: required by the trim on a frame source, refused by the file path (which finds
   its own). Puts one file's data into options that are often shared across files.
3. **A public trim helper only**: `TrimmedToTone` public, the caller sets `StartTime`/`EndTime` itself;
   `TrimReferenceTone` stays refused on frame sources and `DetectionResult.ReferenceTone` null there.

## Answer

**`YuvVideo.ReferenceTone`** (the owner, 2026-10-31).

## How

- `YuvVideo.ReferenceTone`, null by default (not searched) and ignored without the trim. Not in the ranges'
  fingerprint: `ScoreRange` and `Merge` refuse `TrimReferenceTone` (it sets a time range, which ranges refuse too).
- The host searches with `options.ReferenceTone`'s settings (frequency, minimum length, search duration), as
  `FindReferenceTone` does with them on a file, and the first sample's time it gives `ReferenceToneFinder.Add` is the
  file's audio start, so the segments, and the time range they set, are the file path's
  (`FrameInputTests.SameAsTheFileTrimmedToTone`).

## Ranges take it (2026-11-04)

shotdetector-web's pages detect in ranges only (one range from frame 0, ranges split across workers, a stopped run
resumed from a keyframe), so `ScoreRange`, `ScoreRangeAsync`, `Merge`, `FirstRangeToScoreAgain` and `RangeFrames`
with the earlier ranges now take `TrimReferenceTone` with `YuvVideo.ReferenceTone` (a time range set by hand is still
refused). No choice was left to the owner: the bar, the merge equal to Detect's with the same tone for any split
(shots, cuts, scene list, the trimmed range's stats, the spans, `ReferenceTone`), fixes the design, as for the spans
(docs/decisions/2026-11-02-freeze-spans-for-ranges.md).

- **The start**: each range works out the frame Detect's seek to the trimmed start lands on (`VideoReader.StartEstimate`,
  which needs no frame), takes the frames before it without converting them (scores null, no flags, no stats), and
  starts its detectors, blackdetect and freezedetect there, as Detect's after its seek. A range from that frame or
  before it has no freeze seed (-1): its comparisons start at the run's start.
- **The end**: ranges score the frames after it as without the trim (a range doesn't know where its end stops the run
  until it has the frames). Merge's decisions run Detect's own seek and end check over the joined pts, so the frame
  count, the positions and a stopped run's end are Detect's; the stats and spans are the run's frames'. The ranges
  past the end, and the ranges freezedetect names to score again, are as without the trim: the trailing tone is
  scored for nothing, a few seconds at most.
- **Fingerprint**: `TrimReferenceTone` and the tone's segments, only when set, so other ranges keep theirs.
- **Checks**: `RangeToneTests` (shotdetector-web's tone-silence clip in shape: tone at 0-6 s and 9-15 s; every frame
  its own range, splits inside both tones and at the run's edges, resumed runs, stopped runs, timed, untimed,
  deinterlaced, three detectors, `OnShot`, the asynchronous overloads), `FrameInputTests.RangesTrimmedToToneAreTheFiles`
  (a decoded file: the merge, the frame source's Detect, the in-process file's and the executable's).
