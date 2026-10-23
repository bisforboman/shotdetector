### Fixed

- With `--decoder process` and deinterlacing (`--deinterlace`), a time range's first frame now matches a full read's: a seek that landed on the wanted frame itself (a keyframe in an MPEG-TS) left the deinterlacer without the frame before it, so that frame had other pixels. Such a seek now reads again from further back, as in-process.
