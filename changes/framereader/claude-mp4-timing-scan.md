### Faster

- `MediaProbe.Probe` with `PacketTimestamps` on a local MP4/MOV file reads the packets' timestamps from the sample tables
  without reading the media data (h264, HEVC, MPEG-2, MPEG-4 Part 2, MJPEG and ProRes video; not fragmented files): a
  290 MB file's scan reads 0.5 MB instead of all of it. The timestamps, positions and every other value are the same.
