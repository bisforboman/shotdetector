### Fixed

- An rtsp input with `InputOptions` `rtsp_flags` = `listen` and no `listen_timeout` failed to open with "Numerical result
  out of range" (FFmpeg 8.1 passes its own default, -1 s, on to tcp as -1000 ms, which tcp refuses; `ffmpeg -rtsp_flags
  listen -i rtsp://...` fails the same way). FrameReader now passes `listen_timeout` = `0`, which waits for a publisher
  without end as -1 meant to; a `listen_timeout` of your own is used as it is.
