### Fixed

- `VideoFrameReader` on raw elementary streams (.m2v, raw MPEG-4 `-f m4v`, .h264, .hevc): frames are timed as ffmpeg's
  command line times them (it drops these demuxers' timestamps and counts from 0 by the frame rate: raw MPEG-1/2 frames
  were a frame late, so `TryReadForwardTo` didn't give ffmpeg's `-i X -ss T` frame). `Seek`, `TryReadAt` and `TryTakeAt`
  give the full decode's frame, decoding from the start: ffmpeg's `-ss` can't seek these streams (it lands on some
  keyframe, and raw H.264 and HEVC gave no frame at all), and reads forward make no keyframe jumps there.
- `TryReadForwardTo` and `ReadAt` in MP4 with MPEG-4 Part 2 B-frames (also what ffmpeg writes to .m4v): a keyframe jump
  could give the keyframe after the time (3.36 s for 3.312 s; the index is by dts and the B-frames before the keyframe
  don't decode after a jump); it now reads from the keyframe before when the jump lands past the time.
- A seek the container refuses (ffmpeg's margin before the first index entry: AVI, FLV near the start) reads from the
  start in a fresh reader too, giving the full decode's frame. ffmpeg reads on from where the refused seek left the
  input, past the first keyframe: in FLV, `TryReadAt(0)` gave a frame 5.28 s in, or none.
- `TryTake`, `TryTakeAt` and `TryTakeForwardTo`: the frame's `Pts` and `Time` are the reader's (as `TryRead` gives
  them), not the decoder's (which a raw stream's or an MPEG-PS frame without a timestamp lacked).
