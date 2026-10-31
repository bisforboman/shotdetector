# Colour tags that change midway: fix the raw-YUV pipe, park per-frame tags (2026-10-19)

### Question

The Maybe row "FastYuv and the executable path: convert with each frame's chroma location and colour tags, not the first
frame's" was investigated on h264 clips whose halves carry different tags (two libx264 streams joined: colour matrix
BT.601 to BT.709, range limited to full, chroma siting left to top left; 8-bit and 10-bit; 894 frames, 447 after the
change). OpenCV converts every frame with its own tags. ffmpeg's `scale` follows each frame's matrix and range but never
its chroma siting (without `in_chroma_loc` it takes center; `in_chroma_loc=auto`, `-reinit_filter 0`, `setparams`, the
`movie=` source and `sendcmd` don't change that). The investigation also found a second bug: on the executable path's
raw-YUV pipe (ShotDetector.FastYuv), ffmpeg sets its output up with the first frame's tags and, when they change,
converts the later frames' samples to them (through RGB), so FastYuv read other samples than the decoder's. What now?

Content detector, stats cells that differ (content_val, of the 447 frames after the change), before the fix:

| clip | process, default | process, FastYuv | in-process, default | in-process, FastYuv |
|---|---|---|---|---|
| 8-bit, siting change | 0 | 0 | 0 | 0 |
| 8-bit, matrix change | 0 | 392; a cut 2 frames off, 13 CSV cells | 0 | 331 |
| 8-bit, range change | 0 | 386 | 0 | 386 |
| 10-bit, siting change | 399 | 399 | 0 | 399 |
| 10-bit, matrix change | 0 | 400; a cut missing, 26 CSV cells | 0 | 348 |
| 10-bit, no change | 0 | 0 | 0 | 0 |

Cuts moved only where ffmpeg rewrote the samples. With the pipe fixed (`setparams` labelling every frame with the first
frame's tags), process + FastYuv gives in-process FastYuv's numbers: stats only, every cut exact and 0 CSV cells on every
path.

### Choices

1. **Fix the raw-YUV pipe now, park the rest**: the per-frame-tags row stays a Maybe with its options and costs (B1:
   frames whose tags differ take the in-process BGR fallback, about 5 lines, but ARM's rounding on those frames; B2: a
   FastYuv converter per tag set, chosen per frame, 30-60 lines; C: per-frame tags on the executable path from
   showinfo's colour line, moderate; D: the executable's default path on a siting change, only by restarting ffmpeg at
   the change, files only).
2. Fix the pipe, close the per-frame row as Won't do.
3. Fix the pipe and B2 now.

### Answer

**1** (the owner, 2026-10-19): the pipe fixed in #432; the per-frame row stays in Maybe. Not seen in a real file: only
built clips show it (spliced broadcast streams, `-c copy` concatenations and stream discontinuities could).

Since 2026-10-24 ([2026-10-24-per-frame-tags-fastyuv.md](2026-10-24-per-frame-tags-fastyuv.md)) and 2026-10-27
([2026-10-27-per-frame-tags-executable.md](2026-10-27-per-frame-tags-executable.md)) per-frame tags are followed on
every path; the Maybe row is done.
