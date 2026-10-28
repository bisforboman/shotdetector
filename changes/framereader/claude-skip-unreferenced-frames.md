### Faster

- `VideoFrameReader`: reading forward (`TryReadForwardTo`, `TryTakeForwardTo`, `ReadAt`) and seeking (`Seek`,
  `TryReadAt`, `TryTakeAt`) no longer decode the frames before the wanted one that no other frame references: H.264
  slices with nal_ref_idc 0, HEVC sub-layer non-reference pictures, MPEG-1/2 B-pictures and MPEG-4 Part 2 B-VOPs, left
  out of the decoder by their packet headers. Every frame given, its time and its tags are still those of decoding every
  packet, byte for byte. A picture a second of an H.264 film now takes less time than
  `ffmpeg -vf fps=1,scale=... frame_%06d.jpg` on the same cores (issue #566's comparison). `ReadAt` tells each reader
  its next time early, so decoding ahead leaves those frames out too. Not when deinterlacing or decoding on the GPU, nor
  for interlaced MPEG-2, field-coded H.264, H.264 whose SPS gives no reorder depth, DivX packed B-frames, packets
  without a timestamp, VP9 or AV1 (docs/decisions/2026-10-28-skip-unreferenced-frames.md).
