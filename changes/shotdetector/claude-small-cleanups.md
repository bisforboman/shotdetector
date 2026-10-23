### Faster

- Detection no longer decodes the video's first frame an extra time, before it starts, where nothing uses that frame's
  chroma location and colour tags: in-process without FastYuv, which converts each frame with its own tags. They are
  read only when the ffmpeg executable path (10-bit and other formats whose conversion interpolates the chroma) or
  FastYuv's converter first needs them. That was about 0.14 s per detection on 4K 10-bit video, and for an http(s) URL
  decoded in-process a second connection that waited for the first frame. Same results.
