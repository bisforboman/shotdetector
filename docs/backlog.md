# Backlog

What ShotDetector does next, in priority order. Three sections: **Next** (open work, the first row first),
**Waiting** (rows that wait on the owner or an outside event) and **Maybe** (ideas not committed to). Update a
status when work starts or lands; add an item when a divergence or a gap turns up, with why it matters, in Next or
Maybe by its status. A row moves to [backlog-done.md](backlog-done.md) when it is Done, Dropped or Won't do.
Design questions go to [decisions/](decisions/).

Status: **Open**, **In progress**, **Maybe**; finished rows (in backlog-done.md): **Released** (on nuget.org),
**Done** (on `main`, in the next release), **Dropped**, **Won't do**.

## Next

- Fix every divergence the real-world check finds
  - Why: The promise is identical results
  - Status: Open (2026-10-27: on x86 none known: a video whose colour tags change midway is exact on every path since docs/decisions/2026-10-27-per-frame-tags-executable.md (the samples sintel_matrix_change.mkv and sintel_10bit_siting_change.mkv, required in CI), except a stream whose chroma siting changes midway through the executable's default path, which can't be read again and warns; on ARM the default results differ by FFmpeg's conversion rounding, documented in docs/decisions/2026-10-17-arm-results.md, while ShotDetector.FastYuv gives x86's results on 8-bit 4:2:0 and 4:2:2 and 10-bit 4:2:0 of even width, and MPEG-1/2, MJPEG and MPEG-4 Part 2 decode as on x86)

- Frame-source parity audit: list in one pass what `Detect(path)` handles or reports that `YuvVideo`/`YuvFrame` can't express, and file what's missing
  - Why: #608 (rotation), #609 (variable frame rate) and #614 (deinterlacing) were filed on one day, one at a time, from the same caller (shotdetector-web's ManagedCodecs path). One audit of the file path's inputs and `DetectedVideo`'s fields against the frame-source path's (colour tags, chroma location, field order, rotation, timing, start time, crop and scale inputs, warnings) finds the rest at once. After #614
  - Status: Open

## Waiting

- At the next FrameReader release, count the FrameReader features that needed ShotDetector too (docs/frame-reader-product.md, "When to reconsider"; features, not commits, since 2026-10-28)
  - Why: 2.8.0 counted 1 of 21, under 1 in 20 for the first time; the trigger needs two releases in a row over at least 20 counted commits, and then means asking the owner, not splitting. 2.9.0 counted 0 of 6 and 2.10.0 0 of 4 (0 of 10 joined), too few: the window joins the next release's
  - Status: Waiting (on the next FrameReader release; at 2.17.0 the trigger was met, the first time by features, and the owner chose not to split and to keep counting, docs/decisions/2026-10-30-framereader-stays-by-features.md)

- Move to FFmpeg 9's libraries (FFmpeg.AutoGen 9, Native packages on 9.0.x) when opencv-python's Linux wheels bundle FFmpeg 9
  - Why: scenedetect's frames come from OpenCV's FFmpeg; moving earlier risks bytes OpenCV doesn't give, and drops system FFmpeg 8 in-process
  - Status: Waiting (2026-10-14: FFmpeg.AutoGen 9.0.1.1 is out; opencv-python 5.0.0.93 and 4.14.0.94 still bundle libavcodec 62, so the trigger hasn't fired; the user: wait for OpenCV)

- The owner files the x264 AVX-512 report with VideoLAN (x264-avx512-report.md in the main checkout)
  - Why: CI's VideoWriter step needs DOTNET_TieredCompilation=0 because x264's AVX-512 code and .NET's tiered JIT differ about 1 run in 250 (CLAUDE.md); the report was written but not filed as of the 2026-10-15 reminder (it waited on a VideoLAN GitLab account). A public post from the owner's account: theirs to file, never a session's
  - Status: Open

- After .NET 11's release (November 2026), an informational CI run of the tests and parity on net11
  - Why: Most time is in FFmpeg's native code, so .NET 11 promises little speed here (its raised hardware baseline: ReadyToRun at x86-64-v3; faster Native AOT interface dispatch); the risk is a JIT that changes results (the AVX-512 + tiered-JIT VideoWriter flake shows it can). Stay on net10.0 (LTS); run the tests and the parity comparison on net11, informational, then compare the weekly benchmark, and retarget only for a measured gain with identical results (the owner, 2026-10-22)
  - Status: Waiting (on .NET 11's release)

## Maybe

- MPEG-PS chunking: long DVD/VOB rips on several cores
  - Why: Files whose packets lack timestamps (MPEG-PS) never run in chunks (`NotChunkable`: "packets without timestamps"), because chunk bounds need every frame's time up front, the pass #546 removed in-process. Long rips can't use more than one decoder. Measure the pass's cost against what chunking gains (1.3-1.5x on 4 CPUs elsewhere) before deciding whether to bring the pass back for long files
  - Status: Maybe

- Segment decoders inside `ReadAhead`: several decoders on different parts of the file
  - Why: ffmpeg caps frame threads at 16, so above that cores sit idle; issue #566's own run with 4 `ReadAt` readers (3.2 s against 3.6-4.4 s for the ffmpeg command, 22 CPUs) shows the gain. Building it into `ReadAhead` would give `TryReadForwardTo` callers the same without a time list up front, at the memory of one decoder per segment. Pays only above ~16 cores (offered 2026-10-28)
  - Status: Maybe

- FrameReader read-ahead: convert ahead too, for TryRead
  - Why: `VideoFrameReaderOptions.ReadAhead` (#568, issue #566) decodes, deinterlaces and rotates frames ahead on a background thread but converts each frame in the read, because `TryReadForwardTo` only knows which frame it wants when asked (at 1 fps, converting ahead would waste 24 of 25 conversions). Plain `TryRead` wants every frame, so its conversion could run ahead too, hiding the conversion's share of each frame; it would need a ring of output buffers to keep the rule that a frame is valid until the next read. Measure first on a caller whose work per frame is shorter than the decode
  - Status: Maybe (the owner, 2026-10-28)

- mkv/ts: the packet scan alongside decoding on the sequential path
  - Why: A frame's label is final once the scan's latest dts is past its time, so the scan can run beside decoding: max(read, decode) instead of the sum where reading is slow and the file fits the page cache; nearly nothing on local disks. Where dts ever goes backwards (joined or corrupt files), wait for the whole scan; chunked runs keep it first. An additive incremental FrameReader API (`ProbeOptions.OnPacket`). Choice 3 of docs/decisions/2026-10-27-pre-scan.md
  - Status: Maybe (the owner, 2026-10-27: parked). The rule isn't exact on joined files: once the second half's timestamps reach the sorted list, frames already labelled get other packets' labels (and, on a 25+30 fps join, other times); the dts-goes-backwards guard only covers frames decided after that. Simulated on joined TS files: 96 of 225 and 97 of 250 frames labelled differently, 0 on a clean file. An exact version would hold labels as provisional and restart on a mismatch (shots and warnings held until the scan ends, so streaming on mkv/ts emits nothing until then); docs/decisions/2026-10-27-pre-scan.md. A FrameReader start (`ProbeOptions.OnPacket`, `PacketTiming`) was built but not pushed; Matroska's first packets have no dts

- Option B: a FrameReader deferred take so FastYuv's row yadif converts off the decode thread
  - Why: The conversion thread (C, on by default for sequential HD since 2026-10-22) can't serve row-wise yadif: taking a frame deinterlaces every row, so those runs still decode, deinterlace and convert on one thread. A deferred take that keeps yadif's previous, current and next frames referenced would let the conversion thread compute only the rows it reads, deinterlacing included (then K workers): expected 1080i MXF sequential with row yadif from ~16.6 s to about 12-13 s. Only for the library configuration with FastYuv's `Yadif` (the CLI deinterlaces with FFmpeg's yadif, which C already covers); parallel runs gain little (the chunks keep the cores busy). A FrameReader change (docs/decisions/2026-10-22-decode-split.md, docs/frame-references.md)
  - Status: Maybe (the owner, 2026-10-22: parked)

- An opt-in fast mode that doesn't promise scenedetect's results: measure first
  - Why: The 2026-10-21 profile put 45-87% of CPU in decoding and up to 40% in deinterlacing (1080i), with scenedetect's own maths at a few percent, so exactness costs most where every full frame must be decoded and deinterlaced. Candidates, each changing some output: FFmpeg's `lowres` decoding at 1/2 or 1/4 size (MPEG-1/2, MPEG-4 Part 2, MJPEG: broadcast MPEG-2 above all), skipping h264's deblocking (`skip_loop_filter`), one field instead of deinterlacing on 1080i, scoring on YUV or luma without BGR/HSV. Measure on real broadcast files (MXF/TS MPEG-2, h264): the speed-up and how many cuts move against the exact run, before deciding whether a mode is worth having. The default stays exact (the owner, 2026-10-22: "Maybe row, measure first"); any mode would be opt-in with its deviations documented
  - Status: Maybe; measured 2026-10-27 (docs/decisions/2026-10-27-fast-mode.md): only one field instead of yadif plus `lowres` on MPEG-1/2/4/MJPEG is a candidate (about 2-2.8x on broadcast MPEG-2 and 1080i, adaptive about no cuts moved, content up to ~15% moved); `skip_loop_filter` and luma-only dropped; revisit with a real off-air TS/MXF with cuts

- FastYuv's row yadif on 9-16-bit video with Vector128
  - Why: Its 8-bit rows already use portable `Vector128` (and `Vector256`), but `Row16` (interlaced 10-bit such as AVC-Intra and ProRes, with the in-process FastYuv `Yadif`) is scalar on every platform. The 10-bit conversion it feeds became 2-4x faster in #508. A port would need 32-bit lanes (16-bit samples' differences don't fit 16 bits), its own exactness argument, and timings on CI's ARM runners. It pays only for interlaced 10-bit sources deinterlaced in-process, which CI treats as informational
  - Status: Maybe

- FastYuv: 10-bit 4:2:0 of odd width
  - Why: FastYuv returns null there (swscale's scaled path forces full chroma interpolation, done in C; docs/fastyuv-10bit-422.md, "Odd sizes"), so on ARM such videos keep ARM's conversion rounding instead of x86's results; the one ARM gap left in the formats FastYuv covers. Rare (odd widths are unusual in 10-bit sources)
  - Status: Maybe

- OpenCV stops early on some damaged files: scenedetect reads only the frames before the damage
  - Why: Found 2026-10-28 (docs/decisions/2026-10-28-damaged-frame-pts.md): with tools/make-broken.py's corruption on re-encoded Sintel bases, OpenCV read 69 of 422 frames (640x360 stand-in, seed 1), 75 of 404 (seed 2) and 380 of 706 (1080p, seed 1) of corrupt mp4s, and 218 of 407 of a TS with denser damage (the stand-in, 100 runs of 512 bytes, seed 1), where ffprobe and ShotDetector decode them all. Not checked yet: where OpenCV's read stops (a read error it treats as the end?) and whether to follow it
  - Status: Maybe

- The last x86-only SIMD paths as Vector128: `Hsv.Convert` (AVX2/NEON only), `CvResize.HResize` (SSE2 only), `HashDetector` (scalar everywhere)
  - Why: On browser-wasm these run scalar (wasm profile, 2026-10-25). The ceiling is small: a few hundred ms of a 4.5 s Sintel run in the browser (decoding is ~80 %), nothing natively, where the existing x86/ARM paths stay. Worth doing only alongside other wasm work, with byte-identical results and no native slowdown
  - Status: Maybe

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
