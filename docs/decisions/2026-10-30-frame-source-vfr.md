# Frame sources: variable frame rate, from every frame's timestamp given up front (2026-10-30)

Issue #609, from shotdetector-web (its backlog row "Variable frame rate"): the frame-source path (`Detect(YuvVideo,
IEnumerable<YuvFrame>, ...)`, `DetectAsync`, `ScoreRange(Async)`, `Merge`) refused any frame more than half a frame
off the constant-rate grid from frame 0 (`VideoReader.CheckConstantRate`), while `Detect(path)` takes a variable-rate
file at its own timestamps. The grid held up three things: it identified a range's frames (a range given under another
`firstFrame` lands off the grid), `StartTime` (round(seconds × fps) is the frame at a constant rate; the file path
searches every frame's timestamp otherwise), and it refused frames the decoder dropped.

## Question

How does the frame-source path know a video isn't on the constant-rate grid?

## Choices

1. **Timestamps up front**: a new `YuvVideo.FrameTimestamps`, every frame's pts in presentation order. Given, no grid
   check: each frame's pts must be its entry, which identifies dropped or misnumbered frames and ranges exactly, and
   `StartTime` is the file path's. Without it the grid check stays as today; VFR needs the list (an MP4's sample table
   has it).
2. **A VariableFrameRate flag** on `YuvVideo` turns the grid check off, frames at their own pts; `FrameTimestamps`
   optional, for `StartTime` (refused without it); ranges identified by their overlap frames' pts.
3. **No grid check at all**: frames always at their pts, dropped frames accepted as the file path accepts them;
   `FrameTimestamps` optional, for `StartTime`. Changes behaviour for constant-rate callers (what's refused today
   would be accepted).

## Answer

**Timestamps up front** (the owner, 2026-10-30).

## How

- `YuvVideo.FrameTimestamps` (`IReadOnlyList<long>?`, in `TimeBase`, which it needs; rising, else an
  `ArgumentException`): the file path's packets' pts, sorted, which label its frames. Given, there is no grid check:
  the first frame given must be at its own entry (frame 0's, or a range's frame before its first), each next frame at
  a later entry. Skipping entries is a frame the decoder dropped, which `Detect` takes as a file's run does (the frame
  keeps its own time, the numbers go on: the issue's "what the file path does (nothing)").
- Positions: each frame at its own pts, as before; a frame not taken yet at its entry, as a file's frame at its
  packet's.
- `StartTime`: the file path's `SeekFrame` over the entries (the round(seconds × fps) estimate when within a frame,
  else the first frame at or after half a frame before the time), known before any frame is taken; refused when a frame
  before the start was dropped (the file path would number the frames from the start's packet).
- Ranges: `Merge` checks every frame at its own entry in place of the grid, so a range given under another
  `firstFrame` or with a dropped frame is refused (a range's frames are numbered from its first); `ScoreRange` refuses a
  range whose first frame isn't at its entry as it starts. The timestamps' count and SHA-256 are in the fingerprint.
- Tests: FrameInputTests against `Detect(path)` on the issue's clip (25 fps to frame 50, then each frame twice as long)
  with every detector kind, start times by the estimate and by the search, a time range and 0.7.1, whole and split at
  keyframes and merged; the checks themselves on generated frames; benchmarks/wasm runs a variable-rate variant (whole,
  in ranges and from a start time) on WebAssembly against the native run.
