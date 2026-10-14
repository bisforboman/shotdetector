# Remaining scenedetect options (2026-10-05)

Picked from the backlog: API review toward 1.0, repo polish, and the remaining scenedetect options (`time`,
`--frame-skip`, `--crop`, image sizing). Decisions inside: the end of a range is compared as frame numbers
(scenedetect's `FrameTimecode` falls back to that), VFR seeking takes the first frame at or after half a frame
before the target (OpenCV's own seek is an artifact, 0.4 s off on the 24→48 fps clip).
