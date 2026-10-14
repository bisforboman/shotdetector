# scenedetect on ARM (2026-10-13)

parity-os showed scenedetect on macOS differing on most frames of 5 clips. Investigated (debug runs on Linux x64,
Linux ARM, macOS ARM and Intel): opencv-python's ARM wheels build in KleidiCV 26.03 and Carotene, which replace
cv2.resize INTER_LINEAR for downscales under 3x and BGR2HSV with their own rounding (resize: 1-2 on many pixels;
HSV: saturation 1 lower on 0.04% of colours, depending on image width); threads don't matter; the x86 wheels use IPP
and agree with our ports. Asked what to do: **x86 stays the reference**, documented, over an ARM compatibility mode
porting KleidiCV's rules (tracks a library version any wheel can change) or also reporting it upstream.
