# Idea: making ShotDetector.FastYuv an option rather than a must

This is a scoping note written at the owner's request on 2026-11-01. The owner asked whether ShotDetector could get
what FastYuv does, so that FastYuv becomes just another option rather than something some setups must install. The
note contains no feature code. It ends with the choices to put to the owner.

**Verdict: needs the owner's decision.** FastYuv cannot be moved into the MIT package as it is: every part of it is a
port of FFmpeg's code. An MIT replacement would need a clean-room rewrite, which is a legal question rather than an
engineering one. What can be done without changing any licence is to narrow the cases where FastYuv matters, and to
make it automatic where it does.

## 1. What FastYuv does that the MIT package doesn't

There are four things, and all of them are ports of FFmpeg source (README of `src/ShotDetector.FastYuv`):

1. **Exact x86 colour conversion in managed code.** It converts to BGR exactly as libswscale does on x86 for:
   - 8-bit yuv420p and yuv422p (the unscaled converter, verified for all 2^24 Y/U/V values);
   - 10-bit 4:2:0 (the bicubic chroma filters, the MMXEXT vertical scaler and the C output for the last two rows);
   - odd heights (the scaled path).
2. **Sampled conversion.** It converts only the pixels the resize reads, about 7% of a frame. On the executable path,
   ffmpeg otherwise converts every full frame to BGR (`VideoReader.Yuv420Sampled` and its 4:2:2 and 10-bit variants).
3. **Row-by-row yadif (`Yadif`).** It deinterlaces only the rows that get converted. At 9 to 16 bits it follows FFmpeg's
   C code, so results are deterministic. FFmpeg 8.1's x86 SIMD yadif differs from run to run there (README, interlaced
   sources).
4. **Copying only the rows it needs (`IYuvChromaRows`).** The in-process decoder copies, and deinterlaces, only the rows
   a converter says it reads.

## 2. Where it is a must, and where it isn't

| Setup | Without FastYuv | FastYuv needed? |
|---|---|---|
| x64, in-process (FrameReader.Native) | Exact: `BgrConverter` runs FFmpeg's own swscale on the rows the resize reads | No. The default uses less CPU (decisions/2026-10-18-fastyuv-x64-in-process.md) |
| x64, ffmpeg executable | Exact: ffmpeg converts whole frames | No, only for speed on large video |
| ARM (Linux arm64, Apple Silicon) | Some pixels differ: FFmpeg's NEON conversion rounds differently, and no FFmpeg setting gives x86's bytes | **Yes**, for x86's results (decisions/2026-10-17-arm-results.md; `arch-diff` requires it) |
| No FFmpeg at all (`Detect(YuvVideo, …)`, shotdetector-web in the browser) | Nothing can convert | **Yes** |
| 9 to 16-bit interlaced | Runs, but results can change between runs | For deterministic results |

So "a must" means ARM, no FFmpeg, and deterministic high-bit-depth deinterlacing. On x64 with FFmpeg it is an
optimisation at most.

## 3. Why it can't simply join the MIT package

Being exact means matching swscale's bytes, and those come from swscale's specific arithmetic:
- table construction and rounding in the 8-bit converter;
- the filter taps, intermediate precision and output rounding of the 10-bit scaled path;
- yadif's filter.

FastYuv implements these by porting FFmpeg's source, so it is LGPL-2.1-or-later (decisions/2026-10-01-cli-and-licensing.md).
Moving that code into ShotDetector would make ShotDetector LGPL.

Textbook BT.601/709 maths written from scratch would be MIT, but it is not exact. It gives different bytes on some
pixels, so cuts and stats would differ from scenedetect's. That goes against the repository's core idea, and doesn't
answer the question.

An MIT converter with the same bytes would need a **clean-room** implementation, for example:
- a specification of the arithmetic written from observed behaviour (black-box output of FFmpeg) and public standards;
- code written from that specification by someone who has not read swscale or the port.

Everyone who has worked in this repository has read the port. Whether such a process is worth it, and whether a
specification derived from measured output is free of FFmpeg's copyright, are legal questions. They are the owner's
call, possibly with legal advice, and this note doesn't answer them.

How big a clean-room job would be, by part:
- the 8-bit unscaled converter is small (per-channel tables);
- the 10-bit scaled path is larger (filters, two scaler stages, edge rows);
- yadif is a separate filter of its own.

The ARM case needs only the converter. A yadif rewrite only matters for determinism at 9 to 16 bits.

## 4. Options that don't change any licence

**A. Pick FastYuv up automatically.** When `ShotDetector.FastYuv.dll` is loadable and `Yuv420Converter`/`Yadif` are
unset, ShotDetector would use it where it changes the result or the cost:
- on ARM (any path);
- on the executable path (sampled conversion);
- for 9 to 16-bit deinterlacing.

On x64 in-process it would stay off, because the default is exact and cheaper there.

Design points to settle:
- loading by name (reflection, so ShotDetector keeps no reference to FastYuv, AOT and trimming included);
- an explicit converter set by the caller always wins;
- `Warnings` or `DetectionResult` should say which converter ran;
- a way to turn it off.

The licensing stays the same: users who install FastYuv have accepted LGPL already.

**B. Bundle FastYuv in the arm64 CLI binaries.** This is option 2 of decisions/2026-10-17-arm-results.md, which the
owner declined then. Those binaries would become LGPL. Native AOT links FastYuv into the executable, so the LGPL's
relinking requirement would need the object files, or a non-AOT build, to be offered as well.

**C. Say it more plainly in the README.** Add a short "Do I need FastYuv?" table (the one in section 2) near the
package list.

**D. Leave as is.**

## 5. Questions for the owner

1. Licence: look into a clean-room MIT converter (needs legal judgement and someone who hasn't read the port), or keep
   FastYuv LGPL?
2. If FastYuv stays LGPL: should it be picked up automatically (A), bundled in the arm64 CLI (B), documented more
   plainly (C), or left as is (D)? These can be combined.
