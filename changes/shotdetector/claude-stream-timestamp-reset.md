### Fixed

- A Stream whose timestamps start over (two MPEG-TS encodes joined), read through the ffmpeg executable, now gives the
  shots in-process decoding and scenedetect give (OpenCV reading the same bytes from a pipe): the executable runs streamed
  input with `-copyts`, so ffmpeg no longer makes such timestamps continue (cuts 50, 100, 125, 175 where scenedetect
  finds 50, 75, 100).
