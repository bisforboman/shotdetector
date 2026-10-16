### New

- Parallel detection of long files: `DetectionOptions.Parallel` (CLI `--parallel`) scores chunks of a local file on
  several cores at once, with exactly the sequential result: the same cuts, scene list, stats and timecodes. Half the
  CPUs, at most 4 chunks, from 4 CPUs on and for files of two minutes or more. Measured on films: 1.5-2.3x faster on
  16 threads, 1.1-1.6x on 4-CPU runners, with the same CPU time. Anything a chunk can't reproduce (a seek that lands
  late, dropped frames, a size change) reads the file sequentially instead; `DetectedVideo.Chunks` says which
  ran. In-process decoding of files only; streams, URLs, a time range, frame skip and MPEG-PS read sequentially.
