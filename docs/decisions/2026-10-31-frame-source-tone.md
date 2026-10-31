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
