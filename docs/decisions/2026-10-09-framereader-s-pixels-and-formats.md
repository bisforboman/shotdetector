# FrameReader's pixels and formats (2026-10-09)

Asked before building its public frame API (VideoFrameReader):

- Pixels: **the same bytes as ffmpeg's command line (recommended)** (`-vf scale=W:H:flags=bicubic,format=F`), over
  "correct, not exact" and "both, selectable". Checked byte for byte in its tests; it mirrors the scale filter: the
  input read as progressive, its chroma location replaced by unspecified (in_chroma_loc), the output's tags those the
  link negotiates.
- Formats in the first version: **Bgr24/Rgb24, Gray8, Bgra32/Rgba32 and Yuv420p** (all offered).
