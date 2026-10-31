### New

- `shotdetect --loudness`: integrated loudness, loudness range, true and sample peak (EBU R128 / ITU-R BS.1770, FFmpeg's
  own `ebur128`), judged against `--loudness-target` (-23 LUFS), `--loudness-tolerance` (0.5 LU) and `--true-peak-max`
  (-1 dBTP), exit code 3 when out of range; `--loudness-json` for pipelines (issue #636).
