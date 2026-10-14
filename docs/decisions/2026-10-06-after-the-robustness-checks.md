# After the robustness checks (2026-10-06)

### Question

Every decode path tried matches scenedetect (after the MPEG-PS fix), and profiling showed large frames are bound by
ffmpeg's decoding, not by our code. What next?

### Choices

1. Speed on large frames (decode threads, pipe bytes, probing).
2. Missing scenedetect features (`-f/--framerate`, min-scene-len as time, export-html, list-scenes/split-video options).
3. API freeze and docs for 1.0.
4. Broken-file robustness (corrupt packets, duplicate timestamps, stream switches).

### Answer

**All four.** Order: 1, 2, 4, then 3 last, since the new features touch the API.
