# FrameReader speed-ups for extracting pictures (2026-10-09)

The user asked whether a picture every second (ffmpeg's fps) could be extracted faster, then to try the options. Measured
on Tears of Steel (12 min) as is and re-encoded with x264 defaults: ffmpeg's decoder tricks (keyframes only 10-15x less
CPU but different pictures; skipping deblocking -25%, different; D3D11VA hardware decoding 2.5x less CPU and identical
pictures on the user's GPU) and FrameReader's exact ways (seeking to each picture 2.6-9x less CPU at one per 10 s, worse at
one per second; 4 parallel segments the same CPU in 2-2.5x less wall time). Asked which to build: **all four** (smart skip
in TryReadForwardTo, recommended; parallel segments; a KeyframesOnly mode, documented as approximate; opt-in hardware
decoding on Windows). Hardware decoding thereby replaces the earlier "skip for now" (below).
