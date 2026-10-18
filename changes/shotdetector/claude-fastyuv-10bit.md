### New

- ShotDetector.FastYuv converts 10-bit 4:2:0 video (yuv420p10le, e.g. h264 High 10) too, with x86's exact bytes:
  `SwscaleYuv420` implements the new `IYuv420P10Converter`, a port of the path x86's swscale takes for it (the
  bicubic chroma filters as initFilter sets them up, the MMXEXT vertical scaler and output, the C output for the
  last two rows), with the chroma sited where the stream says. So on ARM, ShotDetector with FastYuv gives x64's
  results for 10-bit as it does for 8-bit. Even widths, any height, turned or not; odd widths keep the default
  path. On x64 the results are the same either way, but 10-bit through FastYuv is slower than the default path at
  small sizes (each chroma sample the resize reads is filtered again): it's there for other CPUs' results.
