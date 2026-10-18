# MPEG-1/2 and MJPEG on ARM: the C IDCT, opt-in in FrameReader (2026-10-18)

### Question

ARM decoded MPEG-2 and MJPEG to other frames than x64 (888 and 735 of 1253 differing; ci.yml's arch-diff), before
any conversion, so FastYuv couldn't help. #384 showed that FFmpeg's C IDCT (`-idct simple`) on ARM gives exactly x64's
default frames, which x64 already gives with it. #387 makes ShotDetector use it on both paths. Forcing it inside
FrameReader's FrameDecoder broke FrameReader's promise of plain ffmpeg's bytes on ARM (10 seek tests on the arm job),
so the pushed version adds `FrameDecoderOptions.CodecOptions` (ffmpeg's decoder options, empty by default) and
ShotDetector sets `idct=simple`. How should it go in?

### Choices

1. Merge as pushed: the opt-in option; FrameReader still gives plain ffmpeg's bytes everywhere (recommended).
2. The C IDCT by default in FrameReader: x86's frames everywhere, but no longer plain ffmpeg's on ARM; no new option.
3. Hold.

### Answer

**1**: merged as pushed, with auto-merge; a FrameReader minor release (the new option) and a ShotDetector fix
(ARM with FastYuv now equals x64 on MPEG-1/2 and MJPEG; arch-diff requires it).
