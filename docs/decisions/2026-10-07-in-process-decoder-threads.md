# In-process decoder threads (2026-10-07, issue #12)

### Question

Measured on Linux with 2 CPUs (Sintel 1080p, Big Buck Bunny, Tears of Steel; best of 3, one runner): in-process
with 1 decoder thread used the least CPU (below scenedetect on two films) but took 1.2-1.9x scenedetect's time;
2 threads cut wall time 10-23% for 30-48% more CPU (within 4-13% of scenedetect on two films). Decoding ahead on a
task gave no more speed than a second decoder thread for more CPU, so it was dropped. Which default?

### Choices

1. **All CPUs**, as OpenCV does.
2. One fewer than the CPUs, as the executable path (least CPU).

### Answer

**Choice 1.** `--threads 1` remains for the lowest CPU. Big Buck Bunny stays ~1.7x scenedetect's time in-process
(backlog).

Since 2026-10-11 ([2026-10-11-decoder-threads-like-opencv.md](2026-10-11-decoder-threads-like-opencv.md)) the decoder
takes OpenCV's count, min(CPUs, 16), on both paths, not all CPUs.
