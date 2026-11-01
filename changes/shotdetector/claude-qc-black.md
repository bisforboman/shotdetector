### New

- `shotdetect qc` checks black: ffmpeg's blackdetect spans of `--black-min` seconds (2) from the detection pass, a span
  failing only mid-programme, more than `--edge` seconds (10) from the start and the end (issue #636).
