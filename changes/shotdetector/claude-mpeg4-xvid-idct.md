### Fixed

- ARM: MPEG-4 Part 2 video not from Xvid (FFmpeg's encoder, DivX, ...) now decodes as on x86 (FFmpeg's C IDCT), through
  both decoders. Xvid video already did: FFmpeg decodes it with the Xvid IDCT on every platform, and which streams are
  Xvid follows FFmpeg's own rule (the encoder's signature, else the FourCC). Unchanged on x86.
