### Fixed

- `DetectionOptions.TrimReferenceTone` (`--trim-tone`) starts and ends on the video's timeline: the tone is timed from
  the file's start, as FrameReader reads the audio, so in a file whose audio starts before its video (a fragmented MP4
  with B-frames, a Matroska file with an offset) the trim started that much after the tone's end, a frame or more cut.
  Frames from the caller: the tone counts from `YuvVideo.FileStartPts` when it is set, as before without it.
