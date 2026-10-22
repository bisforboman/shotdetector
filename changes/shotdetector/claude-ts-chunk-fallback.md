### Fixed

- Parallel detection no longer falls back to reading sequentially at a join of concatenated MPEG-TS copies (`ffmpeg -f concat -c copy`) where the next copy's first frame follows the last one by less than half a frame: the chunk's seek landed on that frame and took it for the wanted one.
- Deinterlacing in-process (`--deinterlace`), a seek that landed on the wanted frame itself (a keyframe in an MPEG-TS) left the deinterlacer without the frame before it: a time range's first frame had other pixels than in a full read, and a parallel run fell back to reading sequentially. Such a seek now reads again from further back.
