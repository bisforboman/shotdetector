### Fixed

- The warning that the ffmpeg executable fetches an HLS playlist's https segments without checking their certificates
  now covers playlists opened over http too, whose https segments, keys and variants went unchecked without a word.
