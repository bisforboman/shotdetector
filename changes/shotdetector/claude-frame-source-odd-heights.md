### New

- FastYuv converts 8-bit yuv420p and yuv422p (and yuvj) video of an odd decoded height too, at even widths: swscale
  scales those (its unscaled converter needs an even height), and FastYuv now reproduces that path as x86 runs it
  (`IYuvOddHeightConverter`, which `SwscaleYuv420` implements). So the file path with FastYuv gives x86's results for
  them on ARM too, and frame sources (`Detect(YuvVideo, ...)`) take odd heights, with the chroma sited by
  `YuvVideo.ChromaLocation`, where they were refused ("height must be even"). yuv444p, gray and odd sizes in both
  directions stay refused on frame sources (issue #630, docs/decisions/2026-10-31-frame-source-odd-heights.md).
