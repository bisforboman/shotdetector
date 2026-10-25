### Faster

- ShotDetector.FastYuv: 8-bit yuv420p and yuv422p rows convert to BGR faster, with SIMD where the columns read are close together (`Vector128`: x64, ARM64 and WebAssembly) and without range checks elsewhere. The bytes are the same as before.
