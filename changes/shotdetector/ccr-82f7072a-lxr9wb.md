### Fixed

- The warning that the ffmpeg executable fetches an HLS playlist's https segments, keys and variants without checking
  their certificates is now given only for an ffmpeg older than FFmpeg 9. FFmpeg 9 checks every https connection by
  default, so the warning was wrong there.
