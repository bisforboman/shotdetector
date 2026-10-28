### Fixed

- `VideoFrameReader`: the last frame of an MPEG-PS or AVI file with B-frames, which comes out of the decoder without a
  timestamp, threw an `OverflowException` (`TryRead`, `TryReadForwardTo`, `ReadAt`). It now gets the
  time ffmpeg's command line makes up for it (the previous frame's plus its duration), with `FrameRate` too.
- `FrameDecoder`: a 4:2:2 video turned by 90 or 270 degrees came out with half its chroma never written (memory left
  over from earlier frames). Such a rotation is refused with a `FrameReaderException` (`CanRotate` already said it
  isn't supported); 180 degrees and 4:2:0/4:4:4 turns are as before.
