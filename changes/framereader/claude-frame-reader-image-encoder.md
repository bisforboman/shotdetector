### New

- `ImageEncoder`: a frame as a JPEG (FFmpeg's `mjpeg`) or PNG file, to a `Stream` or a `byte[]`, one encoder for any
  number of frames, without the ffmpeg executable or another image library (issue #542). The bytes are those ffmpeg
  writes from the same raw frame with `-frames:v 1 out.jpg` (or `.png`), converted to the pixel format ffmpeg picks for
  the encoder. Options are ffmpeg's: `JpegQscale` (`-q:v`, 2-31) and `PngCompressionLevel` (`-compression_level`, 0-9);
  without a qscale every JPEG is what a one-frame ffmpeg run writes. WebP isn't available yet. `Capability.Jpeg` and
  `Capability.Png` for `FFmpegLibraries.Missing`.
- `VideoFrame.SampleAspectRatio`, `ColorRange`, `ColorSpace`, `ColorPrimaries` and `ColorTransfer`: the frame's aspect
  ratio and colour tags, as ffmpeg's frame has them after the same filters (the encoder writes them into the file).
- FrameReader.Native now carries FFmpeg's `mjpeg` encoder.
