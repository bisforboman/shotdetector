# FrameReader's audio; no hardware decoding for now (2026-10-09)

Asked before milestone 6's audio:

- API: **an AudioReader class (recommended)**, like VideoFrameReader (TryRead, Seek), over one MediaReader
  interleaving video and audio.
- Samples: **float32 at a chosen rate and channel count (recommended)**, the samples of `ffmpeg -ar R -ac C -f f32le`
  (checked byte for byte, seeking included), over also offering int16.
- Hardware decoding: **skip for now (recommended)**, over an opt-in that may differ from software decoding: worth it
  only if bit-exact, which needs proving per codec and GPU, and CI runners have no GPUs.
