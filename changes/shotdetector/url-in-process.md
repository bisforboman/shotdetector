### New

- URLs decode in-process when FFmpeg's libraries have the URL's protocol (`Auto`, or `InProcess`): with
  FrameReader.Native, http everywhere and https on Windows. Faster, and no ffmpeg executable needed for them; other URLs
  use the executable as before. The results are the same either way.
