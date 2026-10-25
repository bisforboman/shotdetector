# Parallel detection of long files

Status: built (2026-10-16), opt-in at first (`DetectionOptions.Parallel`, `--parallel`); the default for `Detect`
since 2026-10-18 from 4 CPUs on and for frames up to 3840 x 2160: `DetectionOptions.Mode`, `--sequential`,
`DetectedVideo.Chunks`. The user's choices: docs/decisions/ ("Parallel detection: opt-in, in-process files, automatic
chunks, a prototype first", "Parallel detection: build it, `Parallel` and `--parallel`, half the CPUs up to 4",
"Parallel detection as the default", "Parallel by default only from 8 CPUs, with a frame-size cap" and "Parallel by
default from 4 CPUs", which replaced the 8-CPU gate the same day after runner measurements).

## Turning it on and off

`DetectionOptions.Mode` (`DetectionMode`):

- `Auto`, the default: parallel for `Detect` and `DetectAsync` (and so the CLI) from 4 CPUs on and for frames up to
  3840 x 2160, sequential for `DetectStreamAsync`, which would otherwise deliver every shot at the end. 2 chunks on 4
  CPUs gained 1.07x to 1.8x on every film and clip measured on GitHub's x64 (2 cores with SMT) and ARM runners, 1080p
  included, for 10 to 54 MB more and up to 8% more CPU time on x64 (17% at low resolution with stats); memory grows
  per chunk with the frame size, hence the cap. Every condition below still applies: a file that can't be chunked
  reads sequentially.
- `Sequential`: one pass, never chunks (CLI `--sequential`).
- `Parallel`: chunks wherever it can, from 4 CPUs on and at any frame size, `DetectStreamAsync` too (CLI
  `--parallel`), and says why in `DetectionResult.Warnings` when it can't (and in `DetectionOptions.Warnings`, which
  `DetectStreamAsync` tells too).

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
  anywhere), each on its own `VideoReader`, which takes the outer reader's probe (stream entries, every frame's pts, the
  Xvid IDCT; read-only, 2026-10-19) rather than probing the file again, with its share of the decoder threads (the remainder to the first chunks,
  so 5 in 2 chunks are 3 and 2; with 0, FFmpeg's choice, the default count is shared out: for a frame-threaded
  decoder up to 1080p, twice the CPUs at most 16, since 2026-10-21, below). Checked: every frame's
  position equals the probe's for its index, each chunk gives its frames, the overlap frame's score equals the next
  chunk's, every chunk's first frame has the first chunk's size, and nothing threw. Otherwise the file is read
  sequentially from the start (a real error comes from there), `DetectedVideo.Chunks` is 1 and
  `DetectedVideo.ChunkFallback` says why (also a `DetectionResult.Warnings` entry, which the CLI prints).
- `AutoChunks`: half the CPUs, at most 4, each chunk at least a minute, never more chunks than the decoder threads
  the caller set (so none below 4 CPUs); `Auto` for frames up to 3840 x 2160 pixels, `Parallel` at any size.
- Decoder threads for frame-threaded decoders (`FrameDecoder.HasFrameThreads`: FFmpeg's frame threading, h264, HEVC,
  VP9, ProRes; not MPEG-2's slices or dav1d's own threads), 2026-10-21: a chunked run shares out twice the CPUs (at
  most 16) instead of the CPUs for frames up to 1920 x 1080, so 2 chunks on 4 CPUs get 4 threads each, OpenCV's count
  for one decoder, rather than 2 (`ChunkedDecodeThreads`). Such a decoder decodes a frame per thread at once and waits
  on the frames in flight: profiled on 4 vCPUs, 2 frame threads per chunk kept each 62-68% busy. Measured on this
  4-vCPU container, 150 s at 1080p25, min of 3 interleaved runs, adaptive with stats: h264 8-bit 16.8 s to 15.1 s
  (1.11x), 10-bit 23.8 s to 19.5 s (1.22x), the same CPU time, ~30 MB more, identical scene and stats CSVs; MPEG-2
  unchanged. At 3840 x 2160 it gained 1.09x for 125 MB more (398 to 523 MB: each thread holds 4K frames), so larger
  frames keep the CPUs' count. A sequential run keeps OpenCV's count too
  (docs/decisions/2026-10-11-decoder-threads-like-opencv.md: damaged files conceal the way scenedetect's decoder does
  only with it, though corrupt input still varies from run to run in both: 2026-10-22-damaged-files-best-effort.md),
  although 8 threads there gave 1.19-1.22x on 1080p h264 as well.
- Frame buffers (between a reader's decoding thread and the scoring): a sequential run's 8 shared out,
  `ChunkBuffers` = max(2, 8 / chunks), so 4 each in 2 chunks and 2 in 3 or 4 (2026-10-18; it was 8 each). Measured
  on this 4-CPU container, content detector, with and without stats, 3 interleaved rounds (the table below): CPU time
  unchanged everywhere; wall time varied up to 4x between runs of one case on this machine, and no count's median
  stayed outside 8's range (a recheck of 2 chunks at 4 buffers, 5 rounds when quieter: 2.82 s against 2.93 s at 360p,
  8.45 s against 8.85 s at 720p). Even 2 buffers in 2 chunks kept the speed; 8 / chunks was kept so a chunked run
  holds no more buffers than a sequential one. What it saves is the buffers' size: the sampled rows on the default
  path (a few MB per chunk), whole frames with FastYuv.

  | Clip | Path | Sequential | 2 chunks, 8 / 4 each | 4 chunks, 8 / 2 each |
  |---|---|---|---|---|
  | 426x240 | default | 89 MB | 99 / 97 MB | 111 / 105 MB |
  | 1280x534 | default | 107 MB | 131 / 124 MB | 165 / 143 MB |
  | 1280x720 | default | 115 MB | 142 / 133 MB | 183 / 155 MB |
  | 1920x1080 | default | 147 MB | 187 / 175 MB | 259 / 218 MB |
  | 1920x1080 | FastYuv | 154 MB | 202 / 178 MB | 292 / 221 MB |
  | 3840x2160 | FastYuv | 366 MB | 553 / 458 MB | 868 / 583 MB |

  Peak RSS, medians of 3; with stats the same within a few MB.
- A conversion thread (2026-10-22, docs/decisions/2026-10-22-decode-split.md): read sequentially in-process (a file,
  a stream or a URL), video of at least 1280 x 720's pixels (the coded size, so portrait HD counts and 1280 x 534 scope
  doesn't) converts its frames on a thread of its own: the decoding thread takes each decoded frame (a reference, not a
  copy) and hands it over (`VideoReader.DefaultConvertThreads`; `DetectionOptions.ConvertThreads` and the hidden
  `--convert-threads N` override it). Measured on 4 vCPUs: 1.09-1.20x on 1080i and 1080p sequentially, the same CPU time,
  1-23 MB more memory, identical results. A parallel run's chunks convert on their decoding threads (`ChunkOptions`): the
  chunks keep the cores busy already (1.00-1.07x there). Not with FastYuv's row-wise yadif (taking a frame would
  deinterlace every row) nor on the executable path.
- Sequential, always: streams and URLs, the executable path, MPEG-PS without packet pts, a time range, frame skip.
  Progress counts all chunks' frames; a fallback starts it over. When `Parallel` was asked for (`--parallel`,
  `--chunks`) and the file is read sequentially all the same, a warning says why: one of those, fewer than 4 CPUs,
  frames over the cap, one decoder thread or shorter than two minutes. `Auto` reading sequentially says nothing.
- Damaged video: frame-threaded decoders conceal damage differently from run to run (`samples/broken/corrupt.mp4` gave
  three different stats files in three sequential runs), so chunks can't be compared there; dropped frames are caught
  by the positions.

## Frame-source ranges (for hosts)

The same split for frames the caller decodes (2026-10-25, docs/decisions/2026-10-25-frame-source-ranges.md): a browser
host decodes a long video in several Web Workers, each with its own single-threaded runtime and no shared memory, and
there is no file to seek, so the host does what `ScoreChunked` does and ShotDetector does the rest.

- **Scores per range:** each worker calls `ShotDetection.ScoreRange(video, frames, firstFrame, options)` with the
  range's frames from `firstFrame - 1` (the frame the first score compares with, as a chunk starts one frame early) to
  one frame past the range's end, and posts `RangeScores.ToBytes()`. The host picks the starts: the frame before each is
  a keyframe, or the worker decodes from an earlier one and drops the frames before `firstFrame - 1`.
- **Decisions once, in order:** `ShotDetection.Merge(video, ranges, options)` runs `Steps` with the ranges' scores in
  place of the frames, exactly as the chunked file path's scores, so the decisions, stats and result are the same code.
- **The overlap check** is the chunked path's: a range's frames from the next range's start on must score (and carry
  pts) as the next range's do. With no sequential fallback, a mismatch is an error that names the frame.
- **Identified by pts:** Merge runs the constant-rate check over every frame from frame 0's pts, as the frame-source
  Detect does per frame, which plays the part of the file path's check against the probe's positions.
- **What can't be split** is refused rather than read sequentially: a time range, frame skip and
  `DetectionMode.Parallel`. Every detector works, with stats too (threshold's dedupe by position stays in Decide).

## Prototype (2026-10-16)

Exactness, scene list and stats CSV byte for byte against sequential: all 35 samples and variants with 7 detector
configurations at 5 chunks (245/245) and the two films at 4 and 8 chunks (28/28) identical. The two MPEG-TS variants
fell back (their seek landed late; fixed in #316, since then they run in chunks; a seek at a join of concatenated TS copies, landing under half a frame late, and an interlaced one landing on the wanted keyframe itself, fixed 2026-10-22: `tools/make-interlaced-ts.sh`); open GOPs (long GOP, MPEG-2, DivX),
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

Measured again on 2026-10-18 for the default's CPU threshold (docs/decisions/2026-10-18-parallel-auto-4cpus.md;
content detector, 3 interleaved rounds, within 1-2%), the 4-vCPU rows above don't hold any more: on both runners 4
chunks beat 2 on every film (Elephants Dream 1.38x against 1.28x on x64, 1.75x against 1.46x on ARM; Tears of Steel
1.17x against 1.07x, 1.24x against 1.11x), and 1080p at 17-22 Mbps gained 1.07x to 1.15x in 2 chunks. `Auto` still
takes half the CPUs (the user's choice: 4 chunks cost about 65 MB more at 1080p for little more there).

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
- Memory: one decoder per chunk; the frame buffers are shared out (since 2026-10-18).
- Edge cases in the frame-to-index mapping (repeated pts, joined encodes): they go to the sequential list rather than
  being guessed.
