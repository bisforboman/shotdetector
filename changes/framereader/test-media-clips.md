### New

- `RemuxOptions.DisplayRotation`: the video's display rotation in place of the input's, as ffmpeg's `-display_rotation`.
- `TestMediaOptions.VideoQuality`: a fixed quantizer, as ffmpeg's `-q:v`.
- `MediaInfo.PacketPositions`: each video packet's byte position (ffprobe's `packet=pos`), read along with
  `PacketTimestamps`.
- FrameReader.Native: the png encoder and the mpeg (program stream) and image2 (image sequence) muxers, so
  `TestMedia` and `VideoWriter` can write .mpg files and PNG frames.
