### New

- `shotdetect qc` checks broadcast range (signalstats' BRNG in every frame but colour bars at most `--brng-max`
  percent, 1 by default, with the worst frame and the file's YMIN and YMAX, equal to ffmpeg's signalstats) and reports
  colour bars and line-up tone, failing a file without both in its first minute with `--require-bars-tone`
  (issue #636).
