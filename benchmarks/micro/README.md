# Microbenchmarks

The per-frame pixel work, on its own: `CvResize.ResizeRows` from each source size to scenedetect's processed size
(`VideoReader.DownscaledSize`: 426x240, 640x360 and 1920x1080 to 256x144, 1280x534 to 256x107), and
`EdgeDetector.Detect` on the V plane of those resized frames (what `--stats` and edge weights run). The project
references src/ShotDetector (through its `InternalsVisibleTo`), so it always measures the code in the tree, not a
copy of it.

```
dotnet run -c Release --project benchmarks/micro                 # both sets, timings and hashes
dotnet run -c Release --project benchmarks/micro -- --hash-only  # hashes only: a quick check that results didn't change
```

Options: `--set all|real|synthetic`, `--frames N` (60), `--rounds N` (15), `--filter TEXT` (cases whose name contains
it, e.g. `edges` or `1280x534`), `--films DIR` (default `samples/realworld`), `--video FILE` (repeatable: these instead
of the films). `BENCH_FFMPEG`: the folder with ffmpeg and ffprobe, else PATH.

## Inputs

- **real**: keyframes from one public-domain film per source size, the ones `tools/realworld.py` pins by SHA-256
  (Elephants Dream 426x240, Big Buck Bunny 640x360, Tears of Steel 1280x534, the Sintel trailer 1920x1080). Download
  them once with `python tools/realworld.py --download elephants_dream_426x240_h264.mp4 bbb_640x360_h264.mp4
  tears_of_steel_1280x534_h264.mov sintel_trailer_1920x1080_h264.mp4`; films that are missing are skipped. ffmpeg
  converts the frames as the pipelines do (`scale=W:H:flags=bicubic,format=bgr24`), and they are cached under the build
  output (`bin/.../frames/`), never in git. Use these for decisions: synthetic images hid the edge detector's real cost
  once (127 us per frame on smoothed noise, 306 us on a film's frames).
- **synthetic**: a seeded set (gradients, moving rectangles and discs, a little noise), the same on every machine, so
  the benchmark runs without the films or a network.

## Output

Per case, the best of N rounds in microseconds per frame (rounds interleave the cases, after three warm-up passes),
and the first 16 hex digits of the SHA-256 of all its outputs; then one SHA-256 over every case. `inputs` lines hash
each set's frames.

The hash says whether the results changed: an optimisation must leave it as it was. Synthetic hashes are the same
everywhere; real ones depend on the converted frames too, so they compare between builds on one machine with one ffmpeg
(the `inputs` hash shows when the frames differ). The total covers the cases that ran, so compare runs with the same
options and films.

Timings belong to the machine they were taken on. Compare A and B on the same machine, interleaved (A, B, A, B...),
each a separate build of its own checkout (a worktree per side), and pin the CPUs where you can (CLAUDE.md, "Benchmarks
on this desktop"). For whole-pipeline numbers there is the weekly `benchmark.yml` (`tools/bench.py`).
