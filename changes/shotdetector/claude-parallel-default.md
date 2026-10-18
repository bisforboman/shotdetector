### New

- Parallel detection is the default for long local files: `Detect` (and the CLI) scores a file of two minutes or more
  in chunks on several cores at once, with exactly the sequential result, wherever it could before with
  `Parallel = true` (in-process decoding, no time range or frame skip, not MPEG-PS; anything else reads sequentially).
- `DetectionOptions.Mode` (`DetectionMode`): `Auto`, the default, chunks for `Detect` but not for `DetectStreamAsync`,
  whose shots still come as they are found; `Sequential` never chunks; `Parallel` chunks `DetectStreamAsync` too.
- `shotdetect --sequential` scores the file in one pass; `--parallel` is still accepted (the default now).
- `DetectionOptions.Parallel` is obsolete (use `Mode = DetectionMode.Parallel`); it still works.
