### New

- `VideoFrameReader.TryTake`, `TryTakeForwardTo` and `TryTakeAt` give the decoded frame unconverted (a `DecodedFrame`,
  the caller's to dispose) at exactly the positions `TryRead`, `TryReadForwardTo` and `TryReadAt` find (keyframe
  jumps, ffmpeg's seek, `ReadAhead`, deinterlacing and autorotation), so `ImageEncoder.Encode(DecodedFrame, ...)` writes
  what `ffmpeg [-ss T] -i X -vf scale=... -frames:v 1 out.jpg` writes for each, byte for byte, without leaving the
  reader (issue #574). One scaling pass instead of two. The conversion's options (`Width`, `Height`, `MaxWidth`,
  `FrameFormat`) don't apply to takes, so one reader can both read and take; not with `FrameRate`. A taken frame is a
  reference to the decoded buffers (no copy), valid after the next read and after the reader is disposed.
- `DecodedFrame.Time`: the frame's time from the input's start, as `VideoFrame.Time` (null when it has no timestamp).
