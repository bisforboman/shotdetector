### Fixed

- `VideoFrameReader.TryReadForwardTo` (and `TryTakeForwardTo`, `ReadAt`) gave the wrong frame for times more than a
  second ahead in MPEG-PS (.mpg, .vob), often a GOP late or none at the end, and in AVI with B-frames, a frame or two
  late: their keyframe jumps don't land where a full decode has the frame (MPEG-PS's index marks every PES as a
  keyframe; AVI's I- and P-frames have no timestamps of their own). It now decodes through in those containers, as
  ffmpeg's output-side `-ss` does.
- `VideoFrameReader.Seek` (and `TryReadAt`, `TryTakeAt`) near the start of an AVI with B-frames threw "FFmpeg (seek):
  Operation not permitted". As ffmpeg's command line, a seek the container refuses now reads on from where it is (a
  reader that has read already, from the start).
