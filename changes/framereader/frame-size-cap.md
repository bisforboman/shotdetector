### New

- `FrameDecoderOptions.MaxPixels`: the largest frame decoded (FFmpeg's `max_pixels`), 8192 x 8192 by default, so a
  crafted file claiming a huge size fails to decode instead of exhausting memory; 0 lifts it.
