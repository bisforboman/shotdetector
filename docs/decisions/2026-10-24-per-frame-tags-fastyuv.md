# Colour tags that change midway: FastYuv in-process follows each frame (2026-10-24)

### Question

The Maybe row "FastYuv and the executable path: convert with each frame's chroma location and colour tags, not the first
frame's" (parked on 2026-10-19, [2026-10-19-per-frame-tags.md](2026-10-19-per-frame-tags.md)): on a video whose colour
matrix, range or (10-bit) chroma siting changes midway, OpenCV converts every frame with its own tags, and so does the
in-process default path, but ShotDetector.FastYuv took the first frame's for the whole video. Its stats differed from
scenedetect's after the change (on the 2026-10-19 clips, in-process FastYuv: 8-bit matrix change 331 cells, range change
386, 10-bit siting 399, 10-bit matrix 348). Take it up now?

### Choices

1. **B1**: frames whose tags differ from the first frame's take the in-process BGR conversion (about 5 lines, no speed
   cost on x64, but ARM's FFmpeg rounding on those frames, so not x86's results there).
2. **B2**: a FastYuv converter per set of tags (`IYuv420Converter.ForColor`, the 4:2:2 and 10-bit equivalents), chosen
   for each frame from its own tags (30-60 lines; x86's results on ARM too, which is why FastYuv exists).
3. **B2 + C**: B2, and the executable path's FastYuv takes each frame's tags from showinfo's colour line (pairing two
   stderr lines per frame, then B2's choice).
4. **Keep it parked.**

### Answer

**2, B2** (the owner, 2026-10-24): B2 also gives x86's results on ARM, which is why FastYuv exists.

Built as `VideoReader.FrameConversions`: the decoding thread reads each frame's colour matrix, range and chroma siting
(`FrameDecoder.ColorSpace`/`ColorRange`/`ChromaLocation`, FrameReader's public API, unchanged) and hands them over with
the frame; a frame with the previous frame's tags takes its conversion at once (the common case: three string
comparisons per frame), other tags are resolved over the stream's as the first frame's are and get one converter per
set of colours (10-bit 4:2:0 per siting too, with the decoded rows its vertical chroma filter reads). A frame whose
colours FastYuv declines takes the in-process BGR conversion, as frames of another size do. `FastYuvTests.InProcessFollowsEachFramesTags`
checks it (each half of a joined clip gives the frames that half gives alone, and on x64 the default path's).

Measured on clips built as `tools/make-samples.ps1` builds the two tag-change samples (two 10 s h264 halves of the
Sintel trailer joined; the range and 10-bit matrix variants the same way), every detector with `--stats` against
scenedetect 0.7.2, in-process, our FFmpeg 8.1: stats cells that differ (all detectors), CSV cells, detectors whose cuts
differ:

| clip | in-process FastYuv before | in-process FastYuv after | in-process default (unchanged) | executable FastYuv (unchanged) |
|---|---|---|---|---|
| 8-bit, matrix change | 2202, 13, 1 | 0 | 0 | 2202, 13, 1 |
| 8-bit, range change | 2657, 68, 3 | 0 | 0 | 2657, 68, 3 |
| 10-bit, siting change | 2827, 72, 2 | 0 | 0 | 2827, 72, 2 |
| 10-bit, matrix change | 2278, 0, 0 | 0 | 0 | 2278, 0, 0 |

The executable's default path still differs on the 10-bit siting change (2827, 72, 2) and is 0 on the others. CI
compares the two tag-change samples exactly in-process (both paths) and informationally through the executable;
arch-diff holds ARM's in-process FastYuv to x64's default path on them again, the executable's FastYuv still to x64's
FastYuv.

Not in scope: **C** (the executable path's FastYuv from showinfo's per-frame colour line) and **D** (the executable's
default path on a siting change: ffmpeg's scale never follows a frame's siting, only a restart at the change would) stay
parked in the Maybe row. The frame-source API (`ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>)`) carries the
colour tags once, on `YuvVideo`, and `YuvFrame` has none, so per-frame tags don't apply there, by design.

Since 2026-10-27 ([2026-10-27-per-frame-tags-executable.md](2026-10-27-per-frame-tags-executable.md)) C and D are built,
and the executable path is compared exactly. `YuvFrame` has carried per-frame field flags since 2026-10-30
([2026-10-30-frame-source-deinterlace.md](2026-10-30-frame-source-deinterlace.md)); per-frame colour tags there are a
Maybe row.
