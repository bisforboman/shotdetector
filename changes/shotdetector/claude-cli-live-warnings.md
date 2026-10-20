### New

- `shotdetect` prints each warning on stderr as soon as it is known (why `--parallel` reads sequentially, an HLS
  playlist read by the ffmpeg executable) instead of after detection, and still once.
