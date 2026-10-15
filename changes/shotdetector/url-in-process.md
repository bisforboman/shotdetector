### New

- URLs decode in-process when FFmpeg's libraries have the URL's protocol (`Auto`, or `InProcess`): with
  FrameReader.Native, http everywhere and https on Windows. Faster, and no ffmpeg executable needed for them; other URLs
  use the executable as before. The results are the same either way.

### Fixed

- URL inputs read only their own family of protocols (http(s): http, https, tcp, tls, crypto; rtsp: rtsp, rtp, udp,
  tcp, tls), with the executable and in-process: a remote HLS playlist could make FFmpeg read local files through
  `file:` entries with a media extension.
- An http(s) URL that stops sending fails after 30 s instead of waiting forever (FFmpeg's `rw_timeout`, with the
  executable and in-process), and cancellation stops an in-process read that waits on the network.
