# TryTake with FrameRate (2026-10-28)

### Question

Issue #574's open point: the picture-a-second loop gives the frame at or after each second (`ffmpeg -ss i`), not
plain `ffmpeg -vf fps=1`'s frames (at 25 fps those are 0.48 s, 1.48 s, ...). The reader's FrameRate option already
picks fps=1's frames, but only for converted reads, not for TryTake. What should be done?

### Choices

1. **Allow TryTake with FrameRate**: TryTake on a FrameRate reader gives the fps filter's frames unconverted, so each
   file is byte-identical to `ffmpeg -vf fps=R,scale=... out_%d.jpg`; TryTakeAt and TryTakeForwardTo stay refused, as
   their read counterparts are.
2. **Write it up only**: describe the difference and the options, build nothing.
3. **Backlog row only**: a Maybe row, to build if the issue's author asks.

### Answer

**1. Allow TryTake with FrameRate** (the owner, 2026-10-28).

As built: the fps loop's "next output frame" is shared by `TryRead` (which converts it) and `TryTake` (which hands out
a new reference to the filter's output frame, no copy), so reads and takes still mix on one reader. The frame's
`Pts` is the fps filter's, in 1/FrameRate units, and its `Time` that times the rate, as `VideoFrame`'s are with
FrameRate. It carries the decoder's bits per raw sample as `FrameDecoder.TakeFrame`'s does: ffmpeg's command line
hands them to its encoder through `fps` too (a metadata-only filter), though none of the tested sources' files depend
on it. Tested byte for byte against `ffmpeg -i X -vf [yadif,]fps=R[,scale=...] out_%03d.ext` (JPEG at a fixed qscale
with a scale, PNG without a filter; mpeg4 with B-frames, a TS starting at 10 s, 10-bit, interlaced; 1 and 5/2 fps;
ReadAhead 0 and 2). Supersedes the refusal in docs/decisions/2026-10-28-reader-take-decoded-frames.md for `TryTake`.
