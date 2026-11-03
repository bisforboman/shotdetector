# yadif on one thread at 9 to 16 bits (2026-11-02)

FFmpeg 8.1's yadif at 9 to 16 bits gave a different picture on every run on x86, so the MIT default path's stats on
interlaced 10-bit video (AVC-Intra, ProRes) varied from run to run, and so did a plain `ffmpeg -vf yadif` lossless copy
for scenedetect. Only FastYuv's LGPL `Yadif` was deterministic (docs/decisions/2026-10-08-row-wise-yadif-in-fastyuv-9-16-bits.md).

## What the reproduction found

The cause is a race between yadif's slice threads, not its SIMD maths (docs/ffmpeg-yadif-16bit-report.md, the report
for the owner to file):
- With one filter thread (`yadif=threads=1`, the filter's own option, or `-filter_threads 1`), every run gives the
  same output, and it equals `-cpuflags 0` (the C code), so FastYuv's `Yadif` and the old test references too.
- The differing pixels are the first one to three pixels of each slice's first row, at widths whose rows have no
  padding (320 and 1280 vary; 352, 720 and 1920 don't). Every x86 16-bit version is affected, not only AVX-512.
- 8 bits is the same on every run with any number of threads.

## Choices

Put to the owner with what each costs:
1. `threads=1` at 9 to 16 bits only, in both paths: `-vf yadif=threads=1` through the executable, and FrameReader's
   in-process yadif graph given `threads=1` for frames above 8 bits (a FrameReader change too). 8-bit keeps threaded
   yadif. SIMD stays on. Cost: at 9 to 16 bits yadif runs on one core, so the same CPU time, more wall time on many
   cores (recommended).
2. `threads=1` at every depth: simpler, but 8-bit loses yadif's threading for nothing.
3. Document only: README says to use FastYuv's `Yadif` or a `yadif=threads=1` copy.

Ruled out before asking:
- `-cpuflags 0` (in-process, `av_force_cpu_flags`): global to the process, so swscale's x86 conversion to BGR would
  change too, and with it parity with OpenCV.
- bwdif: deterministic, but a different picture from scenedetect's yadif copy.
- Picking up FastYuv automatically: rejected on 2026-11-01 (docs/decisions/2026-11-01-fastyuv-clean-room.md).

## Answer

**1, `threads=1` at 9 to 16 bits.**

Done in the same PR: `VideoReader.Yadif` (the executable's filter: above 8 bits, a pixel format named with its byte
order) and FrameDecoder's graph (the first frame's depth). Tests check every decode equals `-cpuflags 0`
(`FrameDecoderTests.DeinterlacingIsTheSameOnEveryDecode`, `VideoTests.TheExecutablesYadifIsTheSameOnEveryRun`), with a
mutation line each. `RowWiseYadifEqualsFfmpegs` no longer needs a `-cpuflags 0` reference copy for 10-bit. README and
FrameReader's README tell scenedetect users to make the copy with `-vf yadif=threads=1`.
