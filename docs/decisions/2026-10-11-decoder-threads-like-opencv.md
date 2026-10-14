# Decoder threads like OpenCV (2026-10-11)

Damaged files decoded like scenedetect's only with OpenCV's thread count (Linux, 4 CPUs: exact at 4, different at
1-3 and 16). Asked: **match OpenCV, min(CPUs, 16), on both paths** (over an opt-in, and leaving it), replacing
issue #12's CPUs - 1 (executable) and the 4/8 caps.
