### Faster

- Parallel detection uses less memory: the chunks share a sequential run's 8 frame buffers (4 each in 2 chunks, 2 in 3
  or 4) instead of keeping 8 each, at the same speed. Peak memory in 2 / 4 chunks, measured on 4 CPUs: 1080p 187 to
  175 / 259 to 218 MB; with ShotDetector.FastYuv, 1080p 202 to 178 / 292 to 221 MB and 2160p 553 to 458 / 868 to 583
  MB.
