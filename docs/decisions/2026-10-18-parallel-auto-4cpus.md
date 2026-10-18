# Parallel by default from 4 CPUs (2026-10-18)

Supersedes the 8-CPU part of "Parallel by default only from 8 CPUs, with a frame-size cap"
(2026-10-18-parallel-auto-threshold.md); its 3840 x 2160 cap stays. That decision rested on one measurement, a 130 s
1080p h264 file taking 17.5 s in 2 chunks against 15.3 s sequential on a 4-CPU box, which no runner reproduced, SMT
included.

### Question 1

With runner numbers in, what should `DetectionMode.Auto` do? It chunked only on 8 or more CPUs, for frames up to
3840 x 2160.

Measured on GitHub's 4-vCPU runners
([run 39144967128](https://github.com/bisforboman/shotdetector/actions/runs/39144967128): CLI from main, our FFmpeg
build in-process, content detector, 3 interleaved rounds, within 1-2%): x64 is an AMD EPYC 7763 with 2 cores and 2
SMT threads each, ARM a Neoverse-N2 with 4 cores. Speed-up in 2 chunks against sequential, wall time:

| Video | x64 | x64 --stats | ARM | ARM --stats |
|---|---|---|---|---|
| Sintel trailer looped, 1920x1080, x264 at 16.7 Mbps | 1.09x | 1.07x | 1.08x | 1.08x |
| testsrc2 + noise, 1920x1080, 22 Mbps | 1.11x | 1.11x | 1.15x | 1.14x |
| Tears of Steel, 1280x534 | 1.07x | 1.08x | 1.11x | 1.10x |
| Big Buck Bunny, 640x360 | 1.07x | 1.26x | 1.11x | 1.60x |
| Elephants Dream, 426x240 | 1.28x | 1.56x | 1.46x | 1.82x |

Never a loss. The cost: 10 to 54 MB more peak memory; CPU time +1% to +8% on x64 (+15% to +17% at low resolution
with `--stats`, where the SMT siblings share cores), about 0 on ARM. A 4-core Xeon (no SMT) gave 1.12x to 1.36x from
240p to 1080p at 22 Mbps.

### Choices

1. From 4 CPUs, keep the 4K cap (recommended).
2. From 4 CPUs, up to 1280 x 720.
3. Keep 8 or more CPUs.

### Answer

**From 4 CPUs, keep the 4K cap.** `Auto` chunks wherever `Parallel` would, except for frames larger than
3840 x 2160: half the CPUs, at most 4 chunks, each at least a minute. Below 4 CPUs half the CPUs is one chunk.
`Sequential` (CLI `--sequential`) opts out, e.g. for services running several detections at once.

### Question 2

4 chunks beat 2 on every 4-CPU row (Elephants Dream 1.38x against 1.28x on x64 and 1.75x against 1.46x on ARM; Tears
of Steel 1.17x against 1.07x and 1.24x against 1.11x; 1080p 1.08x to 1.22x against 1.07x to 1.15x), for about 65 MB
more at 1080p. `Auto` uses half the CPUs (2 on 4). Change that?

### Choices

1. Keep half the CPUs (recommended).
2. All the CPUs, at most 4.

### Answer

**Keep half the CPUs.** `--chunks N` still asks for more.
