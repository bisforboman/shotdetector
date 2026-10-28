# Parallel detection's caps above 8 CPUs (2026-10-28)

### Question

`AutoChunks` gives at most 4 chunks, and a chunked run shares at most 16 decoder threads, whatever the CPU count. Both
caps came from 4-vCPU runners and a profile there (a chunk keeps about 1.5-1.75 cores busy), so on 16 CPUs and more they
might leave cores idle. Measured on the owner's 16-thread desktop (Ryzen 7 5800X, Windows, released shotdetect 3.19.0,
Tears of Steel 1280 x 534 h264, content detector, `tools/bench-cpus.ps1`, medians of 3 interleaved rounds; every scene
CSV identical to the sequential one), two runs:

| Chunks | 16 threads (default) | 24 threads | 32 threads |
|---|---|---|---|
| 1 (sequential) | 6.77 s / 6.03 s | 6.79 s / 6.05 s | 6.05 s |
| 2 | 1.53x | 1.55x | |
| 4 (today's) | 1.57x / 1.41x, 110 MB | 1.62x / 1.43x, 129 MB | 1.41x, 153 MB |
| 6 | 1.29x / 1.20x | 1.52x / 1.33x | 1.33x |
| 8 | 1.36x / 1.20x | 1.42x / 1.24x | 1.27x |

Speed-ups against the same run's sequential default (first run / second run; the sequential baseline was 0.7 s faster
in the second). More chunks cost CPU time: 61 s at 4 chunks, 66-68 s at 6, 72-73 s at 8 (45-51 s sequential).

### Choices

1. **Keep both caps:** record the measurement, no code change.
2. **Keep, and measure above 16 CPUs later** (a larger runner) as a Maybe row.
3. **Raise the threads to min(1.5 x CPUs, 24)** for chunked runs: about +2-3% at 16 threads for about 19 MB more; keep
   4 chunks.

### Answer

1, keep both caps (the owner, 2026-10-28). Past 4 chunks every count was slower at every thread count, and 32 threads
gave nothing over 16; 24 gained 2-3%, within the spread between the two runs. Machines above 16 CPUs stay unmeasured.
