### Faster

- ShotDetector.FastYuv: 10-bit (yuv420p10le) video converts to BGR faster on every platform, with SIMD (`Vector128`: x64, ARM64 and WebAssembly) for the pixels, the vertical chroma filter and, where the samples read are next to each other, the horizontal one. The bytes are the same as before.
