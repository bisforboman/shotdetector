# Sequential decoder threads: measure again (2026-10-31)

### Question

[2026-10-21-sequential-decoder-threads.md](2026-10-21-sequential-decoder-threads.md) kept OpenCV's decoder thread
count for sequential runs, giving up 1.19-1.22x on 1080p h264, so that damaged files conceal like scenedetect's. The
next day ([2026-10-22-damaged-files-best-effort.md](2026-10-22-damaged-files-best-effort.md)) showed damaged files vary
from run to run in both tools at any thread count above one, and chunked runs, the default for long files from 4 CPUs
on, already share out up to twice the CPUs (#461). Found in the review of every decision: reconsider twice the
threads for sequential runs?

### Choices

1. Measure again, then decide: a pull request with twice the threads sequentially, CI's exact set (truncated.ts
   included) as the guard, and the timings brought to the owner.
2. Keep OpenCV's count, recording that the 2026-10-21 answer stands despite the 2026-10-22 finding.

### Answer

**1** (the owner, 2026-10-31). A Next row in docs/backlog.md; the owner decides on the result.

### Outcome (2026-11-04)

Measured in #734 on CI's 4-CPU runners: twice the threads sequentially, against `--threads 4`, interleaved on the
same runner, minimum of 5 rounds, every output identical. tears_of_steel 1280x534 h264 1.05x on x64 (2-6% more CPU)
and 1.08-1.09x on ARM (the same CPU); sintel_trailer 1920x1080 1.01-1.04x; 640x360 and smaller about 1.00x. Far below
2026-10-21's 1.19-1.22x (150 s of 1080p; the conversion thread now overlaps more of the decode). The owner: merge it,
a small gain with identical results and one rule for sequential and chunked runs (`VideoReader.DefaultDecodeThreads`).
