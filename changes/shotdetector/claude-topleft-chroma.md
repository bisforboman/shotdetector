### Fixed

- Videos whose container says something else than the video stream about where the chroma samples sit or about the
  colours (for example an mkv whose header says "left" or BT.709 while its h264 stream says "top left" or BT.601):
  the ffmpeg executable path (10-bit video) and ShotDetector.FastYuv (both decoders) converted with the container's
  values and gave other stats than PySceneDetect, which converts with the stream's. They now take the first frame's,
  as the in-process default path already did.
