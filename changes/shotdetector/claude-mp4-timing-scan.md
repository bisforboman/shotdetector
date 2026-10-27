### Faster

- In-process, an MP4/MOV file's probe no longer reads the whole file for the packets' timestamps before detection
  (FrameReader's packet scan reads only the sample tables): one read of the file instead of two, which matters on slow
  network shares. Results are unchanged.
