### New

- `FrameDecoder.CopyRows(plane, rows, destination, destinationStride)`: copies only the rows you ask for of one plane
  of the current frame. With a row deinterlacer (`FrameDecoderOptions.RowDeinterlacer`), only those rows are
  deinterlaced, where `GetPlane` deinterlaces every row: for readers that use a few rows of each frame.
- `FrameDecoder.HasFrameThreads(codec)`: whether the decoder FrameDecoder opens for a codec decodes several frames at
  once on its threads (FFmpeg's frame threading: h264, HEVC, VP9, ProRes, ...; not MPEG-2's slice threads or dav1d's
  own). Known before opening, so you can choose `FrameDecoderOptions.Threads` by it: such decoders can use more threads
  than there are CPUs.
