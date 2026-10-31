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
