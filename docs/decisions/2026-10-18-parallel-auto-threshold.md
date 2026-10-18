# Parallel by default only from 8 CPUs, with a frame-size cap (2026-10-18)

### Question

The release-candidate performance and security reviews of parallel detection as the default (#398) measured it on a
4-CPU box: a 130 s 1080p h264 file took 17.5 s in 2 chunks against 15.3 s sequential (about 15% slower; +46 to
90 MB), since a sequential run with 4 decoder threads already kept the cores busy, while a 16-thread desktop gains
1.5x to 2.3x. Memory grows per chunk (a decoder and its frame buffers each): a 4096 x 4096 clip, in-process with
FastYuv, peaked at 525 MB sequential, 836 MB in 2 chunks and 1197 MB in 4. When should `DetectionMode.Auto` chunk?

### Choices

1. From 8 CPUs on, and sequential above a frame size (recommended).
2. From 8 CPUs on only.
3. Keep it as it is (from 4 CPUs on, any size).
4. Auto sequential for now.

### Answer

**From 8 CPUs on, with a frame-size cap.** `Auto` chunks only when `Environment.ProcessorCount` is 8 or more and the
decoded frame is at most 3840 x 2160 pixels (width x height); otherwise it reads sequentially. Still half the CPUs,
at most 4 chunks, each at least a minute, and never more chunks than the decoder threads the caller set (`--threads
1` runs one decoder). `Mode = DetectionMode.Parallel` (CLI `--parallel`) and the hidden `--chunks N` still chunk from
4 CPUs on, at any size; CI's parallel comparisons use them, as its runners have 4 CPUs.

Implementation choice (not asked): `--parallel`, a hidden no-op since the previous decision, is back in `--help`, as
it now differs from the default.
