# ShotDetector's own results on ARM (2026-10-17)

### Question

The new CI check (ci.yml's `arch`/`arch-diff`, #381) found ShotDetector on ARM (Linux arm64; Apple Silicon is the same
code) differing from x64 on most sample clips: per-frame stats slightly on most frames, and weak cuts (e.g. one fewer on
the BBB trailer), through both decoders. Diagnosed with frame checksums on both runners: the input files and the
decoded frames are identical (except MPEG-2 and MJPEG, whose IDCTs round per platform); the difference is FFmpeg's
yuv420p to BGR conversion, whose x86 and NEON assembly round differently. swscale's C code (`-cpuflags 0`) is the same
on both, but it isn't x86's result either (x86's reference is its assembly), and `+bitexact` changes x86's own output on
10-bit, so FFmpeg has no portable setting that gives x86's bytes. ShotDetector.FastYuv, the LGPL port of the x86
conversion, does: on ARM it gave exactly x64's default results on every 8-bit 4:2:0 clip (H.264, Theora, full range),
for every detector and both decoders. Not covered: 10-bit and 4:2:2 (other swscale paths), MPEG-2 and MJPEG (decoder).
The CLI's arm64 binaries are affected too. What to do?

### Choices

1. Document; FastYuv for exact ARM results: README says identical to scenedetect on x86, and on ARM adding
   ShotDetector.FastYuv gives x86's results for 8-bit 4:2:0; CI requires that and reports the rest. Library and CLI
   stay MIT.
2. As 1, and bundle FastYuv in the arm64 CLI builds (making them LGPL; revisits 2026-10-01-cli-and-licensing).
3. Merge the check as informational now, decide later.
4. x86 only, no FastYuv guidance (as for scenedetect on ARM, 2026-10-13).

### Answer

**1**: documented in README ("Where this differs from PySceneDetect", ARM); `arch-diff` fails unless ARM with FastYuv
equals x64 on the 8-bit 4:2:0 clips, and prints the rest (default ARM, 10-bit, 4:2:2, MPEG-2, MJPEG) for information.
