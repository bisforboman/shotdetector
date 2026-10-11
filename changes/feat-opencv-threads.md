### Fixed

- Damaged video: the default decoder threads are OpenCV's (the CPUs, at most 16) on both paths, since error
  concealment depends on the count; damaged TS and truncated files now give scenedetect's stats exactly (they
  differed a little). `DecodeThreads` / `--threads` still override it.
