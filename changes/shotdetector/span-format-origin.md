### Fixed

- `DetectionResult.BlackSpans` and `FreezeSpans` time their spans from the file's start, as `ffmpeg -vf blackdetect`
  and `freezedetect` print them (ffmpeg subtracts the input's start time, the earliest stream's), not from the video
  stream's: in a file whose audio starts before the video (a fragmented MP4 with B-frames and no edit list, video two
  frames late) every time was that much early. Through the executable, in-process and for ranges; frames from the
  caller take the file's start from the new `YuvVideo.FileStartPts` (FrameReader: `-decoder.StartOffset`), without it
  the video stream's as before. `shotdetect qc`'s black and freeze spans move with them.
