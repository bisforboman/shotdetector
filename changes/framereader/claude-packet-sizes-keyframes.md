### New

- `MediaInfo.PacketDecodeTimestamps` and `PacketSizes`: every video packet's dts and size (ffprobe's `packet=dts`,
  `packet=size`), read along with `PacketTimestamps` (an MP4/MOV file's from its sample tables, as its timestamps).
- `ProbeOptions.PacketKeyframes` / `MediaInfo.PacketKeyframes`: whether each video packet is a keyframe, as the `K` in
  ffprobe's `packet=flags`: where the keyframes are and the bitrate, from one probe. It reads the media data, also of
  an MP4/MOV file, as FFmpeg's parser marks keyframes (an H.264 recovery point) that the sample table doesn't list.
