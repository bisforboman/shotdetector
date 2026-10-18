### New

- **Parallel detection is now the default** for `Detect`/`DetectAsync` and `shotdetect` on machines with 8 CPUs or
  more. A local file of two minutes or more, decoded in-process (no time range, frame skip or MPEG-PS), with frames up
  to 3840x2160, is scored in up to 4 chunks at once (half the CPUs), with exactly the sequential result. More cores are
  busy at once, for about the same total CPU time. Memory grows by one decoder and its frame buffers per chunk. If a
  chunk can't reproduce the file, it is read sequentially instead, and progress starts over. Services that run several
  detections at once can set `Mode = DetectionMode.Sequential` (CLI `--sequential`). `DetectStreamAsync` stays
  sequential.
- `DetectionOptions.Mode` (`DetectionMode`): `Auto`, the default, as above; `Sequential` never chunks; `Parallel`
  chunks from 4 CPUs on and at any frame size, `DetectStreamAsync` too (its shots then all come at the end). Chunks
  never outnumber the decoder threads you set (`DecodeThreads`, `--threads`).
- `shotdetect --sequential` scores the file in one pass; `--parallel` is `Mode = DetectionMode.Parallel`.
- `DetectionOptions.Parallel` is obsolete (use `Mode = DetectionMode.Parallel`). It still works, but it raises warning
  CS0618, which breaks builds that treat warnings as errors.
