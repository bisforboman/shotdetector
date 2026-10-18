### Fixed

- FastYuv: video whose pixel format changes part-way (4:2:0 frames in a 4:2:2 stream, or the reverse) gives the default
  path's results, through both decoders: those frames are converted from their own format, as OpenCV does. Before,
  they were converted as the stream's format, so their colours were wrong. (A stream read through the ffmpeg
  executable still converts them as the stream's format: it can't be read again.)
- In-process decoding from a start time inside a part of the video at another size (a resolution change) failed with
  "Specified argument was out of the range of valid values": the frames are now scaled to the stream's size, as the
  ffmpeg executable path does.
