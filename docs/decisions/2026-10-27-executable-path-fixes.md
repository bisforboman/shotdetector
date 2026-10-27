# Fixes for the slow ffmpeg-executable path (2026-10-27)

### Question

Which fixes for the slow ffmpeg-executable path should I start?

The first recorded benchmark run (bench-data, 2026-10-25) had `shotdetect --decoder process` at 1.04-1.30x scenedetect
0.7.2's time on every film, while the in-process default is 0.41-0.92x. The backlog row "Measure where the
ffmpeg-executable path's time goes, then decide what to fix" profiled it (2026-10-26, findings below) and ranked the
fixes.

### Choices

1. **Feed remap's maps through the `loop` filter** instead of `-loop 1` image inputs.
2. **Chunk the executable path**: several ffmpeg processes, each with the exact seek, through `ScoreChunked` and its
   overlap check.
3. **Track "process + FastYuv" in the benchmark** (a row in `tools/bench.py`), so that path's speed is followed.
4. **Backlog rows only**: record the findings, start nothing.

### Answer

**1, 2 and 3** (the owner, 2026-10-27). 1 first (its own PR); 2 is a Next row in the backlog after it, with a CI job
comparing chunked with sequential runs on the process path; re-tuning `SampledPays` after 1 is a Maybe row (measure on
CI's x64 and ARM runners first).

### The profile's findings

Measured in a shared 4-vCPU cloud container (loaded: ratios hold, absolute times don't), FFmpeg 8.1.3, scenedetect
0.7.2 with opencv-python 5.0.0. Films: the Sintel trailer (1920x1080, 1253 frames), Tears of Steel (1280x534, 17620
frames), and Tears of Steel re-encoded to 640x360 as a stand-in for Big Buck Bunny 360p (archive.org was blocked).
Every variant was checked for identical output (stats CSV MD5).

Whole runs, content detector, best of 3, interleaved (time relative to scenedetect):

| Film | in-process | `--decoder process` | process + FastYuv |
|---|---|---|---|
| Sintel 1080p | 0.70 | 0.98 | 0.85 |
| Tears of Steel 1280x534 | 0.62 (2 chunks), 0.84 sequential | **1.21** | 0.97 |
| Tears of Steel 640x360 | 0.61 (2 chunks), 0.77 sequential | 0.92 | 0.97 |

- **ffmpeg sets the pace.** ffmpeg's wall time is the run's minus 0.2-0.6 s of probing and start-up, and it is no
  slower inside shotdetect than writing to /dev/null: the .NET reader keeps up (10% of the run's CPU with the sampled
  pipeline, 29% with whole 640x360 frames). The pipe is not a cost (a 1 MB pipe buffer changed nothing).
- **ffmpeg's CPU on Tears of Steel**: decoding ~62%, the whole-frame BGR conversion ~13%, remap with its looping inputs,
  framesync and the extra context switches ~25%. ffmpeg alone (86 s CPU) cost more than all of scenedetect (77 s).
- **remap's cost is mostly its looping inputs**, not the pixel lookup: with `-loop 1`, two demuxer and two decoder
  threads re-read and re-decode the PGM maps for every frame, doubling ffmpeg's context switches (131k against 63k for
  4000 frames). Fix 1 decodes each map once and repeats the frame: ffmpeg alone on Tears of Steel -15% wall, -14% CPU
  (context switches 131k to 87k), Sintel -7%; byte-identical, whole file and in the exact-seek shape. The 640x360 class
  doesn't use remap (`SampledPays` is false there): no gain.
- **ffmpeg uses about 2.4 of 4 cores** (a serial filter thread behind the frame-threaded decoder), room a second
  process can use: two concurrent halves (fix 2) took ffmpeg alone on Tears of Steel from 35.0 to 27.8 s (-20%) and on
  640x360 from 11.7 to 8.9 s (-24%), the same CPU. Films under two minutes aren't chunked. It is the only lever found
  for the 640x360 / whole-frame class.
- **Re-tuning `SampledPays`**: before fix 1, whole frames were 9% faster than remap on Tears of Steel (16% of the pixels
  read), 7% slower on Sintel; after fix 1 the two look about equal on Tears of Steel. 0-9%, on 720p-class video only.
- **FastYuv on the executable path** (raw yuv420p over the pipe): Tears of Steel -20% against the default process path,
  Sintel -14%, 640x360 +5%. It exists already for callers who pass the converter; the MIT path can't carry it. Worth
  tracking in the benchmark (fix 3).
- Rejected: `-filter_threads 1` (0-7%, within noise), a bigger pipe buffer (no gain), dropping remap for crop + ffmpeg
  scale (not `cv2.resize`'s arithmetic, so not byte-identical). `in_chroma_loc=left` on the same-size scale costs
  nothing on 8-bit 4:2:0.
