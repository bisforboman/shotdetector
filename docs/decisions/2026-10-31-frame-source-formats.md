# Frame sources: 10-bit 4:2:0 and 8-bit 4:2:2 (2026-10-31)

Issue #627, from the frame-source parity audit (docs/frame-source-parity.md). The frame source refused every format
but yuv420p and yuvj420p, though FastYuv converts yuv420p10le (HEVC Main 10, 10-bit H.264) and yuv422p/yuvj422p for
the file path. The issue asked for 16-bit planes in `YuvFrame` "or a variant", and for `YuvVideo.ChromaLocation`:
swscale interpolates 10-bit 4:2:0's chroma from where its samples sit, and the file path in-process follows each
frame's own siting, as OpenCV's `sws_scale_frame` does.

## Questions

1. How does a `YuvFrame` carry yuv420p10le's samples?
2. Where does the frame source take the 10-bit chroma location from?

## Choices

1. **The same `YuvFrame`, bytes**: `ReadOnlyMemory<byte>` planes holding 16-bit little-endian samples, the strides in
   bytes (FFmpeg's linesize layout, what WebCodecs' I420P10 `copyTo` gives); 4:2:2 the same type with full-height
   chroma planes. Or **separate 16-bit planes**: a constructor on `ReadOnlyMemory<ushort>`, strides in samples.
2. **The video's and each frame's**: `YuvVideo.ChromaLocation` (the first frame's) and an optional
   `YuvFrame.ChromaLocation` (null: the video's), followed per frame like #626's colour tags. Or **the video's only**:
   a stream whose siting changes would differ, silently.

## Answer

**The same `YuvFrame`, bytes** and **the video's and each frame's** (the owner, 2026-10-31).

## How

- `YuvVideo.PixelFormat` takes yuv422p, yuvj422p and yuv420p10le too. The planes are checked for the format: chroma
  planes of the full height for 4:2:2, two bytes a sample for 10-bit. The height must be even for the 8-bit formats
  only, as the file path's YUV pipelines need. FastYuv's 10-bit converter needs an even width; an odd one is refused
  with the video, as before.
- `YuvVideo.ChromaLocation` and `YuvFrame.ChromaLocation` (ffprobe's names; null, empty or "unspecified": unspecified,
  which swscale takes as center) go to the file path's `FrameConversions` with the colour tags. They are used for
  yuv420p10le only, as by the file path.
- Deinterlacing takes these formats too: FastYuv's yadif filters 16-bit rows, and 4:2:2's chroma planes at their full
  height.
