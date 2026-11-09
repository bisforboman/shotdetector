### Fixed

- `DetectionOptions.StartTime` (`-s`) lands where scenedetect's does on a video whose first frame is past FFmpeg's
  probe (one that starts about 5 s or more after the audio): FFmpeg then gives the video the file's start, and
  OpenCV's seek counts frames from the first frame while its frame times count from that start. ShotDetector started
  that much earlier. `TrimReferenceTone` moves its start with the seek.
