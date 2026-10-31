# VideoFrameReader hands out decoded frames (2026-10-28)

### Question

Issue #574: let VideoFrameReader hand out decoded frames for ImageEncoder's ffmpeg-exact path?

`ImageEncoder.Encode(DecodedFrame, ImageFilterOptions)` writes ffmpeg's one command's file byte for byte, but a
`DecodedFrame` came only from `FrameDecoder.TakeFrame`, so using it meant leaving `VideoFrameReader` and rebuilding its
positioning (`TryReadForwardTo`'s keyframe jumps, `TryReadAt`'s ffmpeg-like seek, `ReadAhead`, deinterlacing, times).

### Choices

1. **TryTake methods**: `VideoFrameReader.TryTake`, `TryTakeForwardTo`, `TryTakeAt`, the decoded frame unconverted,
   positioned exactly as the reads.
2. **Encoder takes a native VideoFrame**: a reader option that skips the conversion (`FrameFormat = Native` or the
   like) and an `ImageEncoder.Encode(VideoFrame, ImageFilterOptions)` that does the single pass.
3. **Public FrameDecoder positioning**: `SeekLikeFfmpeg(TimeSpan)` and a forward-to helper with the keyframe jumps on
   the low-level layer.
4. **Backlog row only**: write it down, build nothing now.

### Answer

**1. TryTake methods** (the owner, 2026-10-28).

As built: the reads' positioning is shared (the reads convert the frame it lands on, the takes hand it out), so takes
and reads mix on one reader, each moving the same position and counting in `VideoFrame.Index`. The conversion's options
(`Width`, `Height`, `MaxWidth`, `FrameFormat`) only apply to reads and are ignored by takes rather than refused, so
one reader serves both kinds of read. `FrameRate` refuses the takes, as it refuses `TryReadForwardTo` and `Seek`: its
frames are the fps filter's, not positions in the decoded stream. A taken frame is a new reference to the decoded
buffers (FFmpeg's refcounting; with `ReadAhead`, to the queued frame's, whose own reference the queue still lets go of
at the next read), so nothing is copied, the same frame can be given twice (`TryTakeForwardTo` to a time the frame
given last already covers), and it outlives the next read and the reader. `DecodedFrame.Time` came with it, the time
`VideoFrame.Time` gives, which a caller couldn't compute from the reader's public API (the container's start offset).

The issue's picture-a-second loop gives each second's frame at or after it, `ffmpeg -i X -ss i` (equal to
`-vf fps=1:round=up` where frames fall on the seconds), not `-vf fps=1`'s, which keeps the last frame of each slot
(0.48 s, 1.48 s, ... at 25 fps): that one stays `FrameRate`'s, for reads.

Since the same day ([2026-10-28-reader-take-frame-rate.md](2026-10-28-reader-take-frame-rate.md)) `TryTake` works with
`FrameRate` too.
