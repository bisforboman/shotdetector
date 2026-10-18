### New

- ShotDetector.FastYuv converts 10-bit 4:2:0 video (yuv420p10le, e.g. h264 High 10) too, with x86's exact bytes:
  `SwscaleYuv420` implements the new `IYuv420P10Converter`, a port of the path x86's swscale takes for it (the
  bicubic chroma filters as initFilter sets them up, the MMXEXT vertical scaler and output, the C output for the
  last two rows), with the chroma sited where the stream says. So on ARM, ShotDetector with FastYuv gives x64's
  results for 10-bit as it does for 8-bit. Even widths, any height, turned or not; odd widths keep the default path.
- On x64, FastYuv isn't needed for exactness on 10-bit: it gives the same bytes as the default path and is slower on
  small video (854x480: about 2.5-3.5x the time; 1080p about even), since each chroma row is filtered again for
  every row that reads it. A converter you set is used as set (docs/decisions/2026-10-18-fastyuv-10bit-x64.md); a
  faster 10-bit pass is on the backlog.
