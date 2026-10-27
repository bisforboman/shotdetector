# Colour tags that change midway: the executable path follows each frame too (2026-10-27)

### Question

The last known x86 divergence (docs/backlog.md, "Fix every divergence the real-world check finds"): on a video whose
colour matrix, range or (10-bit) chroma siting changes midway, OpenCV converts every frame with its own tags, and so do
the in-process default path and, since [2026-10-24-per-frame-tags-fastyuv.md](2026-10-24-per-frame-tags-fastyuv.md)
(B2), in-process FastYuv. Through the ffmpeg executable, FastYuv took the first frame's tags for the whole video, and the
default path the first frame's chroma siting (ffmpeg's scale filter never follows a frame's siting:
[2026-10-19-per-frame-tags.md](2026-10-19-per-frame-tags.md)). On the built clips (samples/informational/), stats
differed after the change, cuts didn't: the executable's FastYuv on sintel_matrix_change.mkv and
sintel_10bit_siting_change.mkv, its default path on the siting change. Take up the rest of the parked row?

### Choices

1. **C: FastYuv per-frame tags**: the executable path's FastYuv takes each frame's colour matrix and range from
   showinfo's colour line (pairing it with the frame's line on stderr) and its siting from the frame's line, then B2's
   converter per set of tags.
2. **C and D**: C, and the executable's default path on a 10-bit siting change restarts ffmpeg at the change with the
   new `in_chroma_loc` (files only, with the exact seek: a restart and a GOP per change; a stream can't restart and keeps
   the first frame's siting).
3. **Leave it parked.**

### Answer

**2, C and D** (the owner, 2026-10-27). Two pull requests, C first (both change `VideoReader.cs`).

**C, built as**: on the executable's YUV pipelines, a frame's showinfo line is held until its colour line
(`color_range:... color_space:...`, which showinfo prints for every frame after the frame's line and any side data) and
handed over with both: matrix and range from that line, siting from the frame's `cl:`. Each frame then takes
`FrameConversions`' conversion for its tags over the stream's, the same choice as in-process (a frame with the previous
frame's tags at once, one converter per set of colours). The raw-YUV pipe's labels stay the first frame's
(`setparams`, #432): they keep ffmpeg from converting the samples, and showinfo sits before them, so it reports the
decoder's tags. A frame whose colours FastYuv declines (a matrix it lacks, such as GBR) sends the video to the BGR
pipelines from the start, as a frame of another size does: the executable default path's frames for that video. A
stream can't be read again, so there it keeps the first frame's conversion. `FastYuvTests.ExecutableFollowsEachFramesTags`
(each half of a joined clip gives the frames that half gives alone, and on x64 the default path's) and
`ExecutableTakesTheDefaultPathForColoursFastYuvDeclines` check it.

Measured on the two built clips, every detector with `--stats` against scenedetect 0.7.2, `--decoder process
--fast-yuv`, FFmpeg 8.1: 0 stats cells, 0 CSV cells and every cut exact on both (before: stats differing after the
change on both, as in the 2026-10-24 table). Every other sample gives byte-identical scene lists and stats through the
executable, default path and FastYuv. CI compares the two clips exactly through the executable with FastYuv; the
executable's default path stays informational on them until D.
