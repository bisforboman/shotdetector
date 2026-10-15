### New

- URLs decode in-process when FFmpeg's libraries have the URL's protocol (`Auto`, or `InProcess`): with
  FrameReader.Native, http everywhere and https on Windows. Faster, and no ffmpeg executable needed for them; other URLs
  use the executable as before. The results are the same either way.
- URL inputs read only their own family of protocols, with the executable and in-process alike (http(s) URLs: http,
  https, tcp, tls, crypto; rtsp: rtsp, rtp, udp, tcp, tls): a second layer over FFmpeg's own checks, which already keep
  a remote playlist from reaching local files.

### Fixed

- An http(s) URL that stops sending fails after 30 s instead of waiting forever (FFmpeg's `rw_timeout`, with the
  executable and in-process), and cancellation stops an in-process read that waits on the network.
