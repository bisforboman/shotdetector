# Sequential decoder threads stay OpenCV's count (2026-10-21)

### Question

Should sequential detection get more decoder threads too, for h264 and HEVC?

PR #461 gave chunked (parallel) runs twice the CPUs' decoder threads for frame-threaded decoders up to 1080p, so each
chunk's decoder has up to the CPUs' count. A sequential run kept OpenCV's count (the CPUs, at most 16), which
docs/decisions/2026-10-11-decoder-threads-like-opencv.md chose because damaged files conceal like scenedetect's only
with it. Measured for the question (4 vCPUs, 2026-10-21): 8 decoder threads instead of 4 made sequential detection of
150 s of 1080p h264 1.19-1.22x as fast (8- and 10-bit), same CPU time, identical results on clean files. On
samples/broken (content detector with stats, 3 runs at each count): base.mp4, truncated.mp4, dup_pts.mkv and
res_change.ts gave the same results at 4 and 8 threads; truncated.ts, corrupt.mp4 and corrupt.ts differed from run to
run at either count (truncated.ts on main too).

### Choices

1. **Keep OpenCV's count** (recommended): damaged files keep concealing as scenedetect's decoder does.
2. **Double it**: twice the CPUs (at most 16) for frame-threaded decoders in a sequential run too.
3. **Decide later**: the backlog row keeps waiting.

### Answer

**1. Keep OpenCV's count.** Damaged files keep concealing like scenedetect (the 2026-10-11 decision), and parallel
runs, the default for long files from 4 CPUs on, already get the gain from #461. The Waiting row moved to
docs/backlog-done.md as Won't do.

Note (2026-10-22): "concealing like scenedetect" means the same kind of concealment, not the same bytes on every run:
corrupt input varies from run to run in both tools (2026-10-22-damaged-files-best-effort.md).
