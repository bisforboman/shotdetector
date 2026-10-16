### Fixed

- In-process, the https segments an HLS playlist names are checked too (FFmpeg didn't pass the check on to them); the
  ffmpeg executable still doesn't.
