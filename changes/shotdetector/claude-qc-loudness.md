### New

- `shotdetect qc -i <file>`: a file's delivery checks in one report (a table on standard output, `--json`), exit code 3
  when any check is out of range. Loudness first, with presets (`--preset ebu-r128`, the default, `atsc-a85`,
  `streaming-14`, `streaming-16`); broadcast range, bars and tone, black and frozen spans follow (issue #636).
  `--loudness` stays as its loudness check alone and takes the presets with `--loudness-preset`.
