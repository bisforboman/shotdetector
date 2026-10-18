# FastYuv for 10-bit and 4:2:2 (ARM = x86)

Status: 4:2:2 done (2026-10-18): FastYuv converts yuv422p and yuvj422p through `IYuv422Converter`, rotated frames
included, and arch-diff requires ARM to equal x64 on them. 10-bit 4:2:0 (2026-10-18): a port reproduces x86's bytes
(`IYuv420P10Converter`, "As built (10-bit)" below), and arch-diff requires ARM to equal x64 on it; on x64 it is
slower than the default path on small video, merged anyway with a speed-up to follow
(docs/decisions/2026-10-18-fastyuv-10bit-x64.md).
The owner's answers to the open questions at the end: docs/decisions/2026-10-18-fastyuv-422.md. Backlog: "ARM's
results with FastYuv for 10-bit, 4:2:2, MPEG-2 and MJPEG video".

## As built (4:2:2)

- `IYuv422Converter` (ShotDetector), a new interface beside `IYuv420Converter` so package validation passes;
  `SwscaleYuv420` implements both, reading chroma row `y` for 4:2:2 (`y >> 1` for 4:2:0), as x86's SSSE3 converter.
- `VideoReader.Pipeline` takes `Yuv422Sampled` for yuv422p and yuvj422p with an even decoded height when the converter
  is an `IYuv422Converter`. The executable sends `-pix_fmt yuv422p` (or the stream's own format); in-process,
  `FrameWriter.WriteYuv` packs the decoded planes.
- Rotation: the YUV pipelines convert the frame as decoded and turn after converting (`CvResize.ResizeYuv` reads the
  turned image in source coordinates), as OpenCV's `cv::rotate` does after its conversion. So turned 4:2:2 takes the
  fast path at 90 and 270 too, and turned 4:2:0 of odd width equals the default path.
- A frame of another size or pixel format than the stream's (a resolution change; 4:2:0 frames in a 4:2:2 stream, or
  the reverse) takes the default conversion: in-process frame by frame, through the executable by reading again on a
  BGR pipeline.
- Proof: FastYuvTests and InProcessTests against the default path; arch-diff requires ARM with FastYuv to equal x64 on
  the unturned `sintel_422.mp4` and the turned `sintel_rotated_422.mp4`.

## As built (10-bit 4:2:0)

- `IYuv420P10Converter` (ShotDetector), beside the other two; `SwscaleYuv420` implements it. Its `ForVideo` takes the
  colour tags, the chroma location (ffprobe's name, null as center) and the frame size, since swscale interpolates
  chroma here, and returns null for what it doesn't reproduce: odd widths (full chroma interpolation, C on x86),
  frames whose chroma is narrower than the 4-tap filter or whose vertical chroma filter has fewer than 3 taps (swscale's
  `yuv2packed1`/`yuv2packed2`; heights up to 8), unknown chroma locations and matrices.
- `SwscaleFilter` (FastYuv) ports initFilter's bicubic case with x86's alignment (horizontal 4, vertical 2, one tap
  unaligned); `SwscaleYuv420P10` per row: `hScale16To15` of the chroma (luma is `Y << 5`), then for rows above the
  last two the MMXEXT vertical scaler and output (from `vRounder` 4, `pmulhw` per tap added with 16-bit wrapping
  `paddw`, `YSCALEYUV2RGBX`, `packuswb`) and for the last two `yuv2rgb_X_c_template` with the 24-bit tables
  (chroma clipped to 0..255). The "large differences" above came from that: the asm doesn't clip chroma that the
  bicubic filter makes overshoot, C's tables do; the model reproduces both, so nothing was left unexplained.
- Plumbing as 4:2:2: `FramePipeline.Yuv420P10Sampled`, `-pix_fmt yuv420p10le` through the executable, 16-bit planes
  from `FrameWriter.WriteYuv` in-process; any height (the even-height rule is the 8-bit unscaled converter's); turned
  frames are converted before turning, so they take it too. Both FastYuv paths take the stream's `chroma_location`,
  as the executable's `in_chroma_loc` does (FrameDecoder doesn't expose a frame's own; a frame tagged otherwise than
  its stream would differ in-process, as the default paths would differ from each other).
- Proof on x64 (FFmpeg 8.1.3 executable and libraries, an AVX-512 Xeon): the prototype and the converter give
  ffmpeg's bytes, 0 differing, for noise and 0/1023 extremes at widths of every even residue mod 8, odd heights,
  chroma left, center, topleft, top, bottomleft and unspecified, every supported matrix and both ranges, and on all
  frames of 10-bit h264 clips at 854x480 and 1920x1080 (against `-cpuflags -mmxext`, i.e. C, 3188 of 6144 bytes
  differ on a 64x32 noise frame, so the oracle is the MMXEXT path). FastYuvTests: generated frames against the
  executable; whole clips (left, topleft, BT.709, full range, cropped, odd height, turned 90/180/270, a seek) equal to
  the default path's frames through both decoders. The CLI's CSV and stats are identical with and without FastYuv on
  10-bit sintel clips (plain and turned), a 1080p clip and ten.mp4, both decoders, adaptive and content.
- ARM: in #412's CI, arch-diff's table had ARM with FastYuv equal to x64's default on `sintel_10bit.mp4` and the
  turned `sintel_rotated_10bit.mp4`, 20/20 outputs each (in-process 10/10, executable 10/10), where ARM's own
  conversion differs on every frame (bgr24 1253/1253 and 480/480); arch-summary.py then made both required.
- Speed on x64 (4 shared vCPUs; not the owner's Ryzen): the 854x480 sintel clip took about 7.5 s instead of 3.1 s
  through the executable and 9.3 s instead of 2.6 s in-process; 1080p about the same either way. Each chroma sample
  the resize reads is filtered horizontally again for every output row that reads it (about 32 multiply-adds per pixel
  pair, scalar); a frame-scoped conversion (filter each needed chroma row once) would need a batch method on the
  interface (additive: a new interface beside `IYuv420P10Converter`) and CvResize's YUV path reworked. The owner chose
  to merge as is, a converter set being used as set, and to make it faster later
  (docs/decisions/2026-10-18-fastyuv-10bit-x64.md; the backlog's Next).

The sections below are the scoping written before 4:2:2 was built. Where they describe the code (4:2:2 still
differing, rotation turning the planes first, `WriteYuv420`, the only 4:2:2 sample being turned), the list above
replaces them; for 10-bit they still stand.

## Goal

On ARM, ShotDetector with ShotDetector.FastYuv gives x64's exact results on 8-bit 4:2:0 (docs/decisions/2026-10-17-arm-results.md;
ci.yml's `arch`/`arch-diff`). 10-bit 4:2:0 (`yuv420p10le`, h264 High 10) and 4:2:2 (`yuv422p`) still differ, because
x64's default BGR conversion of those runs x86 SIMD code whose rounding differs from swscale's C code and from ARM's.
The goal: FastYuv reproduces x86's bytes for those formats too, so ARM with FastYuv equals x64 there, as on 4:2:0.

## How it was checked

FFmpeg 8.1.3's libswscale source (the native build's version, tools/native/build-ffmpeg.sh) and BtbN's FFmpeg 8.1.3
and 9.0.2 executables on an x86-64 runner with AVX-512, on two h264 clips at 854x480: a 10-bit 4:2:0 and an 8-bit 4:2:2
(both chroma location `left`). The conversion was ShotDetector's own: `scale=W:H:flags=bicubic:in_chroma_loc=left,format=bgr24`
(VideoReader), which is what BgrConverter sets up in-process (`SWS_BICUBIC`, `src_h_chr_pos`/`src_v_chr_pos` from
the frame) and what OpenCV's `sws_scale_frame` does. Each was hashed (framemd5) with the default CPU flags, with
`-cpuflags 0` (C only) and with one x86 flag removed at a time (`-cpuflags -ssse3`, `-mmxext`, `-sse2`... `-avx512`).
`+print_info` in the scale flags names the path swscale takes.

## What x86 runs

| Input (same size, bicubic) | swscale path (x86-64) | Bytes change when removing | Code that differs from C |
|---|---|---|---|
| yuv420p, even height | unscaled special converter | `ssse3` only | `ff_yuv_420_bgr24_ssse3` (FastYuv's port, today) |
| yuv422p / yuvj422p, even height | unscaled special converter, **the same function** | `ssse3` only | `ff_yuv_420_bgr24_ssse3` with the chroma row `y`, not `y >> 1` |
| yuv420p10le, even width | scaled path, "bicubic scaler ... using MMXEXT" | `mmxext` only | `yuv2bgr24_X_mmxext` and its vertical filter |
| yuv422p, odd height | scaled path, MMXEXT | `mmxext` only | as 10-bit, with 8-bit input |
| yuv420p10le, odd width | scaled path, full chroma interpolation | nothing (default = `-cpuflags 0`) | none: C |
| yuv420p or yuv422p, odd width, even height | unscaled special converter (as for even widths) | `ssse3` only | as for even widths |

Removing SSE2, SSSE3, SSE4, AVX, AVX2 and AVX-512 together changes nothing on 10-bit, so the SIMD horizontal scalers
(`ff_hscale10to15_*`) are bit-exact with C there; only the MMXEXT inline assembly matters. FFmpeg 9.0.2 gave the same
hashes and paths as 8.1.3 for every row of the table and for center-sited 10-bit.

**4:2:2 (even height).** libswscale/swscale_unscaled.c (`ff_get_unscaled_swscale`) takes the special yuv→RGB converter for
`YUV420P`, `YUV422P` and `YUVA420P` when the height is even and `SWS_ACCURATE_RND` is off; `yuvj422p` is mapped to
`yuv422p` with full range first (`print_info` names the yuv422p converter for it). On x86, `ff_yuv2rgb_init_x86`
(libswscale/x86/yuv2rgb.c) returns `yuv420_bgr24_ssse3` for both, whose `YUV2RGB_LOOP` sets
`vshift = src_format != AV_PIX_FMT_YUV422P`: 4:2:2 reads chroma row `y`, 4:2:0 row `y >> 1`, and everything else is
the asm FastYuv already ports (libswscale/x86/yuv_2_rgb.asm). **Verified**: FastYuv's per-pixel arithmetic with the
chroma row changed, run in Python on 3 frames of the 4:2:2 clip (854 wide, not a multiple of 8), gives ffmpeg's
default bytes, 0 pixels off. `-cpuflags -ssse3` (the C table converter, yuv2rgb.c) gives other bytes.

**10-bit 4:2:0.** No unscaled converter exists for it, so libswscale/swscale.c's general scaler runs, per output row:

1. **Horizontal**, libswscale/swscale.c `hScale16To15_c` (x86: SSE2/SSSE3/AVX2 `ff_hscale10to15_*`, same bytes): luma
   1:1 (filter `[16384, 0, 0, 0]`, so `Y << 5`), chroma at half width (bgr24 without `SWS_FULL_CHR_H_INT` keeps
   `chrDstHSubSample = 1`) with a 4-tap bicubic phase shift when the chroma is `left` (identity when `center`);
   `val >> 9`, clipped to 15 bits.
2. **Vertical**: luma 1 tap; chroma 240 → 480, 4 bicubic taps. With MMXEXT and no `SWS_ACCURATE_RND`
   (`sws_init_swscale_mmxext` in libswscale/x86/swscale_template.c sets `use_mmx_vfilter = 1`),
   `ff_updateMMXDitherTables` (libswscale/x86/swscale.c) lays the taps out for the asm, and
   `YSCALEYUV2PACKEDX` accumulates in 16 bits: start at `vRounder` (4), add `pmulhw(sample, coeff)` per tap
   (each product truncated, `paddw` without saturation). C instead sums in 32 bits from `1 << 18` and shifts by 19.
3. **To RGB**: `YSCALEYUV2RGBX` (subtract `uOffset`/`yOffset`, `pmulhw` by the same `yCoeff`/`ubCoeff`/... that
   FastYuv already derives from `ff_yuv2rgb_c_init_tables`, add, `packuswb`), then `WRITEBGR24MMXEXT`, 8 pixels per
   iteration, each pair of pixels sharing U and V.
4. **The last two rows** use C: swscale.c switches to `ff_sws_init_output_funcs` when `dstY >= dst_h - 2`, so
   `yuv2rgb_X_c_template` and `yuv2rgb_write` (libswscale/output.c), with the 24-bit lookup tables from
   `ff_yuv2rgb_c_init_tables` (libswscale/yuv2rgb.c). **Verified**: against `-cpuflags -mmxext`, every row differs
   except rows 478 and 479.

Default against `-mmxext` (C) on one 10-bit frame: about 42% of bytes differ, mostly by 1-3, about 0.03% by more than 4
and a few by up to 25-30. The small differences fit per-tap truncation; **the large ones are not explained yet**
(chroma overshoot at strong edges, clipped by C's tables and not by the asm, is a guess). So the model above is read
from the source and not yet reproduced by a prototype, unlike 4:2:2's.

The filter coefficients come from libswscale/utils.c `initFilter` with x86's alignment: horizontal `filterAlign` 4,
vertical 2 (C alone: 1). Logged as "reducing / aligning filtersize 5 -> 4" for the bicubic chroma taps, so the reduce,
align and normalize steps decide the coefficients the port must have. NEON uses the same alignments (4 and 2).

**Odd sizes.** (Corrected while building 4:2:2: 8-bit yuv420p and yuv422p at an odd width and the same size take the
unscaled SSSE3 converter like even widths, `print_info` says so and FastYuv's arithmetic gives its bytes, 0 pixels off,
FFmpeg 8.1.3; the full chroma interpolation below applies to the scaled path only.) An odd width forces full chroma interpolation (`SWS_FULL_CHR_H_INT`, utils.c "Forcing full internal H
chroma due to odd output size"), which x86 runs in C (`yuv2bgr24_full_X_c`): x64 default equals `-cpuflags 0` there.
Whether ARM's default equals it too is **unverified** (no odd-width 10-bit or 4:2:2 sample). An odd-height 4:2:2 frame
can't take the unscaled converter and runs the same MMXEXT output as 10-bit.

## What to port

**4:2:2, even height** (small):

- FastYuv: the chroma row for 4:2:2 (`y` instead of `y >> 1`) and a 4:2:2 frame layout; the coefficients and the
  per-pixel lookups stay as they are. Under 20 lines in the converter.
- ShotDetector: `IYuv420Converter` is public and 4:2:0 only (`FrameSize`, `RowToBgr`'s plane offsets). Adding a member
  to a public interface breaks package validation against `ApiBaselineVersion`, so a new interface (or default interface
  members) for "planar YUV of this layout"; VideoReader's `Pipeline` (today `yuv420p`/`yuvj420p`, even height) admits
  `yuv422p`/`yuvj422p` with an even height; the executable path sends `-pix_fmt yuv422p`; in-process, FrameWriter packs
  the 4:2:2 planes (as `WriteYuv420`); CvResize's crop offsets for the chroma rows.
- Rotation: VideoReader turns the YUV planes before converting (`TurnFilters`), right for 4:2:0 because the unscaled
  conversion interpolates nothing. A 4:2:2 frame turned 90/270 is subsampled the other way (tools/make-samples.ps1
  says so), so those skip FastYuv (0 and 180 are fine), unless the converter reads in source coordinates.
- Estimate: 100-150 lines with plumbing, plus tests.

**10-bit 4:2:0, even width** (larger):

- `initFilter`'s bicubic subset (415 lines in utils.c in all; needed: 1:1 with a chroma phase, 2x upsampling, edge
  handling, reduce/align/normalize with x86's alignments): about 150-200 lines.
- `hScale16To15_c` for the needed chroma columns and rows; the MMXEXT vertical accumulation and `YSCALEYUV2RGBX` per
  pixel pair, reusing FastYuv's coefficients: about 40 lines.
- The C output and its 24-bit tables for the last two rows: about 60-80 lines.
- Plumbing as for 4:2:2, plus 16-bit samples, the chroma location (`ForColor` gets it; the stream's or the frame's), and
  the vertical filter's 4 chroma rows per output row. Rotation: only unturned frames (the bicubic phases aren't
  symmetric, so not even 180), unless in source coordinates.
- Estimate: 350-500 lines with plumbing, three to four times FastYuv's 124-line converter, plus tests. Odd-height 4:2:2
  reuses this path with 8-bit input (`hScale8To15_c`).

Not to port: odd widths (C on x86: nothing to reproduce, though ARM is unverified); `SWS_ACCURATE_RND`/`+bitexact`
variants (ShotDetector never sets them).

## Proof

- **Unit tests.** 4:2:2: the README's verification of FastYuv (all 2^24 Y/U/V for every matrix and range, against
  ffmpeg; done by hand, the tests keep spot values) carries over, since each pixel is the same map of its own Y, U, V;
  a 4:2:2 test would add spot values with different chroma on neighbouring rows. 10-bit can't be exhaustive (each pixel depends on neighbours):
  generated noise and edge frames against BgrConverter (in-process, with SHOTDETECTOR_FFMPEG_LIBS) at widths of
  every residue mod 8, heights that exercise the last-two-rows switch, chroma locations left, center and topleft,
  every supported matrix, both ranges; on x64 only, where x64 is the reference. Each new quirk gets its mutation line.
- **arch-diff.** tools/arch-summary.py makes a clip's FastYuv column required for `yuv420p`/`yuvj420p` (line 86);
  it would add `yuv420p10le` and `yuv422p`/`yuvj422p`. The 10-bit sample is there (`sintel_10bit.mp4`, left-sited).
  The only 4:2:2 sample is `sintel_rotated_422.mp4`, turned 90°, exactly the case that would skip FastYuv: a required
  4:2:2 row needs an unturned 4:2:2 sample (a new sample changes the samples cache key, so CI regenerates them once).
- **Parity.** compare.py's FastYuv runs (`--fast-yuv`, core and FastYuv paths on Linux x64) already cover these clips
  against scenedetect; they'd then go through the new converter instead of the BGR path.

## Effort

- 4:2:2, even height: small. 100-150 lines with plumbing and tests; the arithmetic is verified, the work is the API
  and the pipelines.
- 10-bit 4:2:0, even width: 350-500 lines, three to four times FastYuv's converter. Prototype first (a C# model
  checked against BgrConverter on a few frames): the large differences above aren't explained yet.
- Odd-height 4:2:2 comes with the 10-bit work (same output code, 8-bit input); odd widths need nothing on x86.

## Performance

The reason is ARM parity, not speed; no measurements yet. Expectations: 4:2:2 behaves as 4:2:0 does today (only the
pixels the resize reads are converted), with 2 bytes per pixel on the executable's pipe instead of BGR's 3 (4:2:0: 1.5).
10-bit gains less: raw `yuv420p10le` is 3 bytes per pixel, the same as BGR, so the pipe doesn't shrink, and in-process
BgrConverter already converts only the slices holding the rows the resize reads; per needed pixel the port does
about 32 multiply-adds of chroma filtering (2 planes, 4 taps across, 4 down), cacheable per chroma row.

## Licensing

The same LGPL-2.1-or-later package, ShotDetector.FastYuv; ShotDetector stays MIT. The ported code comes from more of
libswscale (utils.c, swscale.c, output.c, x86/swscale_template.c), so the README's and the source headers' copyright
lines gain those files' holders.

## Risks

- **FFmpeg moving these paths.** FFmpeg 8 already carries the new ops framework (libswscale/ops*.c), but these
  conversions still take the legacy paths in 8.1.3 and 9.0.2 (checked: same bytes). If a later FFmpeg routes them
  through new code, OpenCV's bytes move too, so ShotDetector's x64 reference moves with them, FastYuv or not; CI's
  parity jobs would show it, and upstream.yml opens an issue on a new FFmpeg in opencv-python's wheel.
- **The executable path and the in-process path** must both feed the converter the same planes; today's FastYuv
  covers both, the new layouts must too (and streams, where the executable sends them).
- **Windows wheels** (OpenCV with FFmpeg 7.1) presumably run the same old template code; unverified, and Linux is
  the reference anyway.
- **The large 10-bit differences** above aren't understood yet; a prototype could find more to port than estimated.

## Open questions for the owner

1. Do 4:2:2 (even height) on its own first? Small and verified; 10-bit is the larger, unproven half.
2. 10-bit at all? About three to four times FastYuv's converter, for ARM parity on 10-bit 4:2:0 (in-process speed
   gains are small).
3. API: a new public converter interface beside `IYuv420Converter`, or default interface members on it?
4. Rotation: skip FastYuv for turned 4:2:2 (90/270) and turned 10-bit, or a converter that reads in source coordinates?
5. An unturned 4:2:2 sample, so arch-diff can require 4:2:2 (one regeneration of CI's samples cache)?
6. Odd widths (C on x86): assume ARM equals them, or add an odd-width sample to the arch comparison to show it?
