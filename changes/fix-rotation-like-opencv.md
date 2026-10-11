### Fixed

- Rotated video turns as OpenCV turns it: the converted BGR image, by the display matrix's angle (flips aside), not
  ffmpeg's autorotate, which turns the planes before converting. Rotated 10-bit video and 4:2:2 at 90 or 270 degrees
  now give scenedetect's results (they differed on both decoding paths); checked against OpenCV on Linux for 8- and
  10-bit 4:2:0 and 4:2:2 at 90 and 180 degrees.

### Faster

- Rotated 4:2:2 and rotated, deinterlaced video decode in-process (they needed the ffmpeg executable).

### New

- FrameReader: `FrameDecoderOptions.Autorotate` (default true): false gives the frames as coded, as `-noautorotate`.
