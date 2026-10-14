# In-process decoding (2026-10-06, issue #12)

### Question

On Linux with 2 CPUs ShotDetector used ~1.5x scenedetect's CPU, two thirds of it in the ffmpeg command line. A
prototype decoding in-process with FFmpeg.AutoGen (MIT bindings, FFmpeg 8.1 libraries) gave byte-identical pixels for
40% less CPU than the command line (1 thread), and OpenCV's speed with 2 threads. But it needs FFmpeg's shared
libraries in the version the bindings were built for. How should it ship?

### Choices

1. **Opt-in backend**, the ffmpeg-executable pipeline stays the default and the fallback.
2. Automatic when compatible libraries are found.
3. In-process only.
4. Not now.

### Answer

**Choice 1.** It lives in the core package (the bindings are MIT); inputs it doesn't handle yet fall back to the
ffmpeg executable.
