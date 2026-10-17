### New

- `FrameDecoder.TakeFrame()` returns a `DecodedFrame`: the current frame as a reference to its buffers (no copy) that
  stays valid after `Next()` and after the decoder is disposed, until it is. `new BgrConverter()` with
  `Convert(DecodedFrame, ...)` converts one to BGR on any thread (one converter per thread), the same bytes as
  converting the decoder's current frame.
