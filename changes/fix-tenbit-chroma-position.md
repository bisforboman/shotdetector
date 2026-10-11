### Fixed

- 10-bit video now gives scenedetect's results exactly on Linux (it was ±1, and up to ±100 on sharp colour edges, in
  red and green). Converting it to BGR interpolates chroma from where the stream says the samples sit (left for
  h264), which OpenCV's converter takes from the frame and ffmpeg's command line drops; both decoding paths now use
  it. FrameReader's `VideoStreamInfo.ChromaLocation` reports it.
