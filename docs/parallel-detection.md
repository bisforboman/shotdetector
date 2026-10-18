# Parallel detection of long files

Status: built (2026-10-16), opt-in at first (`DetectionOptions.Parallel`, `--parallel`); the default for `Detect`
since 2026-10-18 from 8 CPUs on and for frames up to 3840 x 2160: `DetectionOptions.Mode`, `--sequential`,
`DetectedVideo.Chunks`. The user's choices: docs/decisions/ ("Parallel detection: opt-in, in-process files, automatic
chunks, a prototype first", "Parallel detection: build it, `Parallel` and `--parallel`, half the CPUs up to 4",
"Parallel detection as the default" and "Parallel by default only from 8 CPUs, with a frame-size cap").

## Turning it on and off

`DetectionOptions.Mode` (`DetectionMode`):

- `Auto`, the default: parallel for `Detect` and `DetectAsync` (and so the CLI) from 8 CPUs on and for frames up to
  3840 x 2160, sequential for `DetectStreamAsync`, which would otherwise deliver every shot at the end. On 4 CPUs a
  sequential run already keeps the cores busy (2 chunks were ~15% slower there), and memory grows per chunk with the
  frame size. Every condition below still applies: a file that can't be chunked reads sequentially.
- `Sequential`: one pass, never chunks (CLI `--sequential`).
- `Parallel`: chunks wherever it can, from 4 CPUs on and at any frame size, `DetectStreamAsync` too (CLI
  `--parallel`).

`DetectionOptions.Parallel` is obsolete and still works: true is `Parallel`, false `Sequential`. The CLI's hidden
`--chunks <n>` forces a chunk count for CI; CI's runners have 4 CPUs, so its comparisons ask for `--parallel` or
`--chunks`.

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

## As built

- `IDetector.Metric` (the score, from the frame and at most the previous one, with its stats) and `Decide` (the
  unchanged decision, in frame order); `ProcessFrame` is the two in a row. Threshold's stats dedupe by position stays
  in `Decide`, so threshold with stats runs in chunks too.
- `ShotDetection.ScoreChunked`: chunks split evenly by the probe's frame count (not on keyframes: the exact seek starts
  anywhere), each on its own `VideoReader` with its share of the decoder threads (the remainder to the first chunks,
  so 5 in 2 chunks are 3 and 2; 0, FFmpeg's choice, stays 0). Checked: every frame's
  position equals the probe's for its index, each chunk gives its frames, the overlap frame's score equals the next
  chunk's, every chunk's first frame has the first chunk's size, and nothing threw. Otherwise the file is read
  sequentially from the start (a real error comes from there), `DetectedVideo.Chunks` is 1 and
  `DetectedVideo.ChunkFallback` says why (also a `DetectionResult.Warnings` entry, which the CLI prints).
- `AutoChunks`: half the CPUs, at most 4, each chunk at least a minute, never more chunks than the decoder threads
  the caller set; `Auto` from 8 CPUs on and for frames up to 3840 x 2160 pixels, `Parallel` from 4 CPUs on at any size.
- Sequential, always: streams and URLs, the executable path, MPEG-PS without packet pts, a time range, frame skip.
  Progress counts all chunks' frames; a fallback starts it over. When `Parallel` was asked for (`--parallel`,
  `--chunks`) and the file is read sequentially all the same, a warning says why: one of those, fewer than 4 CPUs,
  frames over the cap, one decoder thread or shorter than two minutes. `Auto` reading sequentially says nothing.
- Damaged video: frame-threaded decoders conceal damage differently from run to run (`samples/broken/corrupt.mp4` gave
  three different stats files in three sequential runs), so chunks can't be compared there; dropped frames are caught
  by the positions.

## Prototype (2026-10-16)

Exactness, scene list and stats CSV byte for byte against sequential: all 35 samples and variants with 7 detector
configurations at 5 chunks (245/245) and the two films at 4 and 8 chunks (28/28) identical. The two MPEG-TS variants
fell back (their seek landed late; fixed in #316, since then they run in chunks); open GOPs (long GOP, MPEG-2, DivX),
VFR and 0.7.1's frame labels needed nothing special.

Speed, adaptive, median wall time (CPU time the same throughout):

| Machine | Film | Sequential | 2 chunks | 4 chunks | 8 chunks |
|---|---|---|---|---|---|
| 16-thread desktop | Elephants Dream 426x240 | 6.5 s | 1.55x | 2.23x | 2.27x |
| 16-thread desktop | Tears of Steel 1280x534 | 13.5 s | 1.39x | 1.49x | 1.55x |
| 4-vCPU runner, x64 / ARM | Elephants Dream | 5.2 s / 6.2 s | 1.41x / 1.62x | 1.18x / 1.33x | 1.02x / 1.10x |
| 4-vCPU runner, x64 / ARM | Tears of Steel | 17.9 s / 17.4 s | 1.09x / 1.11x | 1.15x / 1.13x | 1.08x / 1.06x |

Decoding already uses several cores (Tears of Steel keeps ~5 busy sequentially), so the gain is what the idle cores
give; past 4 chunks nothing measured gained.

## Proof

- Unit tests: chunked scores equal sequential scores, chunk boundaries on and off keyframes, the overlap check
  triggering on a crafted open-GOP clip.
- CI: the parity and real-world jobs run each case both ways and require byte-identical output.
- The weekly benchmark (tools/bench.py) reports both, the parallel default beside `shotdetect --sequential`.
- CI's comparisons with scenedetect (compare.py) run the default, so long in-process files there run in chunks;
  compare-parallel.py's reference asks for `--sequential`.
- CI also compares it on ARM (ci.yml's arch job) and on Windows and macOS (parity-os.yml, nightly), in-process.

## Risks

- Decoding is already multi-threaded, so the gain may be smaller than the core count; the prototype measures it before
  anything else is built.
- Memory: one decoder and its buffers per chunk.
- Edge cases in the frame-to-index mapping (repeated pts, joined encodes): they go to the sequential list rather than
  being guessed.
