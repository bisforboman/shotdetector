# MPEG-4 Part 2 on ARM: the C IDCT unless FFmpeg takes the Xvid one (2026-10-18)

### Question

The first ARM table with MPEG-4 Part 2 samples (#400): ARM's default decode of FFmpeg's encoder's clip
(sintel_mpeg4.avi, FourCC FMP4) differed from x64's (765 of 1253 frames), and `-idct simple` on ARM gave x64's default
frames exactly; Xvid's clip (sintel_xvid.avi) already matched, and `-idct simple` would break it (1176 of 1253 differ on
x64 too). FFmpeg's MPEG-4 decoder takes the Xvid IDCT (the same code on every platform) for Xvid streams, and only with
idct=auto; every other MPEG-4 stream gets the platform's SIMD IDCT. On x64 `-idct simple` is a no-op for non-Xvid MPEG-4
(checked on FMP4 and DX50 clips). Which streams are Xvid is the decoder's own rule (libavcodec/mpeg4videodec.c,
decode_user_data and ff_mpeg4_workaround_bugs): an `XviD%d` signature in the user data (start code 00 00 01 B2) before
the first VOP, read from the extradata (which AVI carries too, not only MP4 and MKV) and each packet; else, with no
Xvid, DivX or libavcodec signature at all, an upper-cased FourCC of XVID, XVIX, RMP4, ZMP4 or SIPP; never in the studio
profile. What should ShotDetector do?

### Choices

1. The Xvid-aware C IDCT in both decoders: `idct=simple` for MPEG-4 unless FFmpeg's rule says Xvid, read from the codec
   tag, extradata and first packet (one more short probe, MPEG-4 only); both samples required in parity and arch-diff
   (recommended).
2. Document only: MPEG-4 Part 2 differs on ARM, reported in arch-diff as now.
3. Hold.

### Answer

**1**. ShotDetector's `Mpeg4Idct` reproduces the rule (sscanf's semantics included, checked against the decoder's own
`-debug bugs` output on crafted user data); the executable path reads the bytes with `ffprobe -show_data -read_intervals
%+#1`, the in-process one with FrameReader's `MediaProbe` (new: `VideoStreamInfo.ExtraData`, `MediaInfo.FirstVideoPacket`,
so also a FrameReader release); a Stream's come from its first bytes, a URL's from a second connection (ARM's CI showed
that without it a URL's MPEG-4 differed from its file). Not covered: a signature that first appears in a later packet
(the decoder would switch then).
x64's results are unchanged (byte-identical CSVs and stats on FMP4, DX50, Xvid and XVID-tagged clips in avi, mkv and mp4,
both decoders).
