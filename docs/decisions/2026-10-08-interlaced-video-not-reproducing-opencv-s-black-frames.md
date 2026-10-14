# Interlaced video: not reproducing OpenCV's black frames (2026-10-08)

Found by the broadcast samples (issue #42): on interlaced video, opencv-python 5.0.0.93's FFmpeg 8 swscale refuses
the conversion ("Cannot convert interlaced to progressive frames or vice versa") and OpenCV returns the same nearly
black image (min 0, max 97, mean 0.0) for every frame; ffmpeg decodes the same files correctly. Progressive video is
pixel-identical. Reproducing it would mean analysing black frames, so ShotDetector reads the real ones: a documented
difference (README), compared informationally in CI with deinterlacing off and exactly with Auto against a lossless
yadif copy. Decided during unattended work (the user was away), as the only sensible behaviour; open to revisit.
