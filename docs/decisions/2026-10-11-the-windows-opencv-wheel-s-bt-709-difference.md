# The Windows OpenCV wheel's BT.709 difference (2026-10-11)

Probed locally (opencv-python 5.0.0.93 for Windows, FFmpeg 7.1): the wheel converts with BT.601 coefficients
whatever the colour tags say, and follows the range; ffmpeg with `in_color_matrix=bt601` gives its frames bit
for bit (0 differing pixels in 120 frames of a BT.709-tagged trailer). Asked: **document it and add a dev flag**
(over a public `ColorMatrix` option, or documenting only). The CLI's hidden `--always-bt601` (internal
`DetectionOptions.AlwaysBt601`) converts that way, with the ffmpeg executable; compare.py passes it on Windows,
so local comparisons there are exact. Linux scenedetect stays the reference, and ShotDetector follows the tags.
