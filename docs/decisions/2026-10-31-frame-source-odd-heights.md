# Frame sources: odd heights and other pixel formats (2026-10-31)

Issue #630, from the frame-source parity audit (docs/frame-source-parity.md, item 6). Frames from the caller of an odd
height (8-bit) and in yuv444p, gray or NV12 were refused: the file path converts them with FFmpeg, and no managed
converter reproduced its bytes. All rare.

What scoping found: at an odd height swscale can't take its unscaled 8-bit converter and scales, as for 10-bit (the
MMXEXT vertical scaler FastYuv already ports): 4:2:0 through 10-bit's output, 4:2:2 through `yuv2bgr24_1`; the chroma
is interpolated from where it sits. yuv444p takes another path (a 2:1 bicubic horizontal chroma filter), gray its own.
NV12 needs no converter: a host de-interleaving it to yuv420p gives the samples FFmpeg decodes for the file.

## Question

How far should #630 go?

## Choices

1. **Odd heights only** (recommended): extend FastYuv's scaled-path port to 8-bit input, yuv420p, yuvj420p, yuv422p
   and yuvj422p at odd heights and even widths, byte for byte as the file path on x64; the file path with FastYuv then
   converts them too, so ARM gets x86's results. yuv444p and gray stay refused with a clear message; NV12 documented as
   de-interleaved to yuv420p.
2. **Odd heights, 4:4:4 and gray**: as the first, plus ports of swscale's yuv444p and gray paths to BGR. Several times
   the work, for formats browsers' decoders rarely give.
3. **Won't do, docs only**: keep them refused, document the NV12 de-interleave; a backlog Maybe row.

## Answer

**Odd heights only** (the owner, 2026-10-31).

## How

- `IYuvOddHeightConverter` (ShotDetector, public): `ForOddHeight(pixelFormat, colorSpace, fullRange, chromaLocation,
  width, height)` gives an `IYuvFrameConverter` (a row, or a frame's rows at once), or null where it doesn't reproduce
  swscale. FastYuv's `SwscaleYuv420` implements it with `SwscaleYuv420P10` on the 8-bit samples widened to 10 bits
  (docs/fastyuv-10bit-422.md, "As built (8-bit odd heights)").
- The file path takes FastYuv's pipelines for odd heights when the converter implements the interface (an older
  converter without it keeps the default path), with the chroma location as for 10-bit; frame sources take them on
  the same conversions, and refuse what the converter declines (odd widths at odd heights, tiny frames).
- CI: the sample `sintel_oddheight.mkv` (VP9, 854x479, sited left) in the parity matrix, and arch-diff requires ARM's
  FastYuv results to equal x64's on 8-bit odd heights.
