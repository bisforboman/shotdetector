### New

- FrameReader: `Video.Transcode(input, output, options)` (and `TranscodeAsync`), a whole file re-encoded in one call
  as `ffmpeg -i IN [-vf yadif] -c:v E -pix_fmt yuv420p -g G -force_key_frames ... OUT`: deinterlaced when the stream
  is flagged interlaced, frames upright, the first audio stream re-encoded and interleaved, keyframes every
  `KeyframeEvery`. With whatever video encoder your libraries have (`libx264`, `libopenh264`) (issue #240).
