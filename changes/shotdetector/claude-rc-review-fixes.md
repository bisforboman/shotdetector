### Fixed

- FastYuv: video whose pixel format changes part-way (4:2:0 frames in a 4:2:2 stream, or the reverse) gives the default
  path's results. Those frames are converted from their own format, as OpenCV does. Before, they were converted as the
  stream's format, so their colours were wrong (both decoders).
