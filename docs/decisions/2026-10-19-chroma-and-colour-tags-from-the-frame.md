# Chroma location and colour tags from the frame, through a FrameReader API (2026-10-19)

### Question

On a 10-bit h264 mkv whose Matroska header says the chroma samples sit `left` while the h264 stream's VUI (and so every
decoded frame) says `topleft`, the in-process default path matched scenedetect 0.7.2 (BgrConverter converts with each
frame's own tags, as OpenCV's `sws_scale_frame` does), but the executable path and FastYuv (both decoders) didn't: they
took the chroma location from the probe's stream entry, which is the container's. FastYuv took the colour matrix and
range from the stream too, so an 8-bit h264 mkv whose header says bt709 (or full range) and whose VUI says smpte170m (or
limited range) differed with FastYuv only. How should in-process FastYuv learn the decoded frame's chroma location and
colour tags?

### Choices

1. **(a)** An additive FrameReader API: `FrameDecoder` reports the current frame's chroma location, colour space and
   colour range; ShotDetector reads the first frame with it (ffprobe's `frame=` entries for one frame on the executable
   path) and prefers its values over the stream's. One PR with the ShotDetector fix; the next releases are FrameReader
   2.8.0 and ShotDetector 3.10.0.
2. **(b)** Parse the h264/HEVC SPS VUI from the extradata in ShotDetector: no FrameReader change, but a parser per codec
   to keep forever (VP9 and AV1 carry these elsewhere).
3. **(c)** Fix the executable path only now (a one-frame ffprobe read), FastYuv later.

### Answer

**(a)**. Known gap: the first frame's tags are applied to the whole video, where OpenCV converts every frame with its
own; a video whose tags change midway would still differ on the executable path and with FastYuv. That stays a Maybe in
docs/backlog.md ("FastYuv: convert with each frame's chroma location, not the stream's").
