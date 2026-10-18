### New

- ShotDetector.FastYuv converts 4:2:2 (yuv422p, yuvj422p) too, through the new `IYuv422Converter` interface, which
  `SwscaleYuv420` implements: 8-bit 4:2:2 video takes the fast path, and on ARM gives x86's results as 4:2:0 does.

### Fixed

- FastYuv on rotated video converts the frames before turning them, as OpenCV does: turned 4:2:2 takes the fast path,
  and turned 4:2:0 with an odd width or height gives the default path's results (turned 4:2:0 whose width is odd, an
  odd height once turned, used to differ).
