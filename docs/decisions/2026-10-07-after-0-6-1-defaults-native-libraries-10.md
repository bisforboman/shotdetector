# After 0.6.1: defaults, native libraries, 10-bit, 1.0 (2026-10-07)

### Question

With in-process decoding faster than scenedetect (Sintel, Tears of Steel) or within 4% (Big Buck Bunny) at half
the default path's memory, while the default ffmpeg-executable path stays 1.2-2x slower on 2 CPUs: what next?

### Choices

1. In-process by default when FFmpeg 8.1's libraries are found, the executable otherwise (reverses the opt-in).
2. A `ShotDetector.Native` package (LGPL) carrying FFmpeg 8.1's shared libraries per platform.
3. An experiment: does the in-process converter (OpenCV's legacy swscale interface) make 10-bit video exact?
4. A 1.0 plan (issue #18).

### Answer

**All four**, after 0.6.1, in that order.
