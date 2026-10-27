### Faster

- MPEG-PS files (`.mpg`, `.vob`: packets without a pts) decoded in-process no longer take a whole decoding pass for the
  frames' timestamps before detection: each frame's timestamp comes with the frame. Results are unchanged. Meanwhile
  `DetectionProgress.ExpectedFrames` counts against the stream's frame count, else its duration × frame rate, instead of
  the decoded frames. A start time, deinterlacing and the ffmpeg executable still take the pass first.
