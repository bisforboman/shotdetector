# Interlaced video: reading the real frames, confirmed (2026-10-31)

### Question

[2026-10-08-interlaced-video-not-reproducing-opencv-s-black-frames.md](2026-10-08-interlaced-video-not-reproducing-opencv-s-black-frames.md)
was decided while the owner was away and marked "open to revisit": on interlaced video opencv-python's FFmpeg 8
swscale refuses the conversion and gives scenedetect one nearly black frame throughout, and ShotDetector reads the real
frames instead. Never confirmed since; asked in the review of every decision. Does it stand?

### Choices

1. It stands: real frames; CI compares informationally with deinterlacing off and exactly with `--deinterlace auto`
   against scenedetect on a lossless yadif copy (recommended).
2. Revisit: reproduce OpenCV's black frames by default, or as an option.

### Answer

**1** (the owner, 2026-10-31).
