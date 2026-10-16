# Parallel detection of long files

Status: design (2026-10-16), for review; next a measured prototype. The user's choices: docs/decisions/
("Parallel detection: opt-in, in-process files, automatic chunks, a prototype first").

## Goal

Detect a long local file on several cores at once, with **exactly** the sequential result: the same cuts, scene list,
stats CSV and timecodes, byte for byte. Opt-in at first (an option and a CLI flag), the default only after CI has
compared both ways on every sample and film for a while.

## Why it can be exact

Detection is two halves (mapped 2026-10-16):

1. **Per-frame scores**, the expensive half: decoding, conversion, resize, then each detector's metric. Every metric
   depends on the current frame and at most the previous analysed one (content and adaptive's HSV deltas,
   histogram correlation, hash distance; threshold's average needs only the current frame).
2. **The cut decisions**, the cheap half: thresholds, minimum lengths, the flash filter, adaptive's window, threshold's
   fade state, then MergeLastScene/DropShortScenes. Already separate in the code (`ProcessScore`, `ProcessAverage`;
   histogram and hash need a small split) and never feeds back into the scores.

So: compute the scores of chunks of frames in parallel, each chunk decoding **one frame earlier** than its first, then
run the unchanged decision pass once over all scores in frame order, from the run's real start. Exactness then rests on
one claim: a chunk's scores equal the sequential run's scores for the same frames.

## The plan

- **Chunks:** one per core, at most, and none shorter than a minimum (about a minute), so short clips stay sequential.
  Boundaries on keyframes; each chunk's own reader seeks with the existing exact seek and starts scoring one frame
  before its boundary.
- **Frames are identified by pts**, not by counting: each decoded frame maps to its global index through the probe's
  sorted packet pts, so a chunk's frames get the same numbers and positions as in a sequential run.
- **Overlap check:** each chunk also scores a few frames past its end, and the next chunk must give identical scores for
  them. A mismatch (an open GOP's leading frames, a decoder depending on history) means the run falls back to
  sequential, and says so in `DetectedVideo`.
- **Sequential, always,** where exactness isn't provable cheaply: streams and URLs, the executable path, MPEG-PS
  without packet pts (no cheap seek), resolution changes, dropped or damaged frames (a frame count or pts that doesn't
  match the probe), frame skip whose phase a chunk can't keep, and threshold with stats (its stats dedupe by position
  across the whole run). The list can shrink as the prototype proves cases.
- **Stats** are merged in frame order, as `Stats.Csv` already does by position.
- **Threads:** each chunk's decoder gets a share of the cores, so the total stays near today's.

## Proof

- Unit tests: chunked scores equal sequential scores, chunk boundaries on and off keyframes, the overlap check
  triggering on a crafted open-GOP clip.
- CI: the parity and real-world jobs run each case both ways and require byte-identical output.
- The benchmark reports both, so the default can be decided on numbers.

## Risks

- Decoding is already multi-threaded, so the gain may be smaller than the core count; the prototype measures it before
  anything else is built.
- Memory: one decoder and its buffers per chunk.
- Edge cases in the frame-to-index mapping (repeated pts, joined encodes): they go to the sequential list rather than
  being guessed.
