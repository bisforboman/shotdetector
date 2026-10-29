### Faster

- MPEG-PS (VOB, .mpg: packets without timestamps) through the ffmpeg executable: the decoding pass for each frame's
  timestamp now runs alongside detection rather than before it, so a detection on a 4-minute 576p file takes MEASURED.
  Results are unchanged: each frame takes its label from the pass's timestamps as they arrive, as in-process frames
  have since 3.19.0.
