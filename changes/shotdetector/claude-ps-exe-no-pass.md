### Faster

- MPEG-PS (VOB, .mpg: packets without timestamps) through the ffmpeg executable: the decoding pass for each frame's
  timestamp now runs alongside detection instead of before it, so where a core is free a detection no longer waits for
  it (the same CPU time). Results are unchanged: each frame takes its label from the pass's timestamps as they arrive,
  as in-process frames have since 3.19.0.
