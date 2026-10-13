### Fixed

- FrameReader: `VideoFrameReader` with both deinterlacing and `FrameRate` gave one frame more at the end than ffmpeg's
  `-vf yadif,fps=R`: yadif's frames carry their duration in its doubled time base, which the fps filter read as twice
  as long. It now gets the duration in the stream's time base, and the frames equal ffmpeg's, byte for byte.
