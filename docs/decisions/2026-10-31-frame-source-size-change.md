# Frame sources: a frame of another size (2026-10-31)

Issue #625, from the frame-source parity audit (docs/frame-source-parity.md). `YuvFrame` carried no size, so a stream
whose resolution changes midway was read wrong through the frame source (a larger frame as its top-left corner) or
threw (a smaller one's planes too small), where `Detect(path)` scales such a frame to the stream's size in its one
conversion to BGR (swscale, bicubic), as OpenCV's `sws_scale_frame` does into its buffer. With FastYuv the file path
converts those frames with FFmpeg; the frame source has no FFmpeg, so matching it means porting swscale's scaled 8-bit
yuv420p to BGR path (luma and chroma bicubic at any ratio, x86's MMXEXT vertical scaler and output) to FastYuv.

## Questions

1. Scale such a frame as the file path does (the port, behind a new converter interface), or, as a first step,
   refuse it with a clear error?
2. How does a `YuvFrame` say its size?

## Choices

1. **Refuse now, scale later**: a frame of another size than `YuvVideo`'s throws `ShotDetectionException`
   (`InvalidInput`) naming the frame and both sizes, so a result is never silently another video's; the scaling port
   becomes a backlog row. **Scale**: the port now, done when a stream changing 320x240 to 352x288 midway gives
   `Detect(path)`'s CSVs byte for byte.
2. **`int Width`, `int Height`** init properties on `YuvFrame`, 0 (the default) meaning the video's, so existing callers
   don't change; or a nullable `(int Width, int Height)? Size`.

## Answer

**Refuse now, scale later**, with **`Width`/`Height`, 0 = the video's** (the owner, 2026-10-31).

## How

- `YuvFrame.Width`/`Height` (0: `YuvVideo.Width`/`Height`). The reader checks each frame's size before its planes;
  any other size (a negative one too) throws `InvalidInput` ("Frame N is WxH, not the video's WxH ..."), in `Detect`,
  `DetectAsync` and `ScoreRange` alike.
- A host that leaves them 0 isn't checked: it should set them from its decoder (WebCodecs' `codedWidth`/`codedHeight`,
  FrameReader's `FrameDecoder.Width`/`Height`), as `FrameInputTests.Decoded` does.
- The scaling port is in docs/backlog.md (Maybe until a caller meets a resolution change).
