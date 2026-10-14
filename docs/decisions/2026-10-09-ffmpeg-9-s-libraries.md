# FFmpeg 9's libraries (2026-10-09)

FFmpeg 9.0.2 and FFmpeg.AutoGen 9.0.1 are out; our bindings (AutoGen 8.1) load only FFmpeg 8's libraries (avcodec
62), so with only FFmpeg 9 present `Auto` falls back to the executable. Asked how far to go: **test and document the
fallback, and move to 9 when OpenCV's wheels do (recommended)**; not chosen: move everything to 9 now (system FFmpeg 8
would stop working in-process, and scenedetect's reference frames come from OpenCV's FFmpeg 8), or support both
(our own bindings for ~90 functions and two struct layouts, or two FrameReader builds picked at runtime).
