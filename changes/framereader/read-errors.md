### Fixed

- `FrameDecoder`, `AudioReader` and `MediaProbe` (packet timestamps): a read that fails part way (a network reset, a
  `Stream` that throws, an I/O error) ended the input as if it were over, giving a silently shortened result; it's a
  `FrameReaderException` (`DecodeFailed`) now, with the stream's exception inside. A truncated or damaged file still
  ends where its data does, as in OpenCV.
