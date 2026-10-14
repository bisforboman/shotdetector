# Colour conversion with FFmpeg 8 (2026-10-02)

FFmpeg 8+ ignores `in_color_matrix`, so OpenCV built on it follows the stream's colour tags, and so does the
scenedetect reference on Linux. **Decision:** follow the colour tags (BT.601/709, limited/full range) like
OpenCV + FFmpeg 8; the Windows OpenCV wheel (FFmpeg 7.1) differs on BT.709 and is documented, not matched.
