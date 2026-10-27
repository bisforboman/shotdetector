### New

- `ImageEncoder` writes WebP (`ImageFormat.WebP`, was refused), through FFmpeg's `libwebp` encoder: the file
  `ffmpeg -c:v libwebp -frames:v 1 out.webp` writes from the same raw frame, byte for byte with the same libraries.
  Its options are libwebp's, refused for the other formats: `WebPQuality` (`-quality`, 0 to 100, default 75),
  `WebPLossless` (`-lossless 1`) and `WebPCompressionLevel` (`-compression_level`, libwebp's method, 0 to 6).
  `Capability.WebP` for `FFmpegLibraries.Missing`.
- FrameReader.Native now carries libwebp 1.6.0 (BSD-3-Clause, `COPYING.libwebp`), linked into libavcodec with FFmpeg's
  `libwebp` encoder.
