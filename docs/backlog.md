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

## Waiting

- Parallel detection above 8 CPUs: more than 4 chunks and 16 decoder threads in total
  - Why: `AutoChunks` = min(4, CPUs / 2, whole minutes) and `DefaultDecodeThreads` = min(CPUs, 16): on 16 CPUs 4 chunks of 4 threads, on 32 still 4 x 4. A chunk keeps about 1.5-1.75 cores busy (profiled on 4 vCPUs), so by that estimate half of 32 cores sit idle. Not measured: the weekly benchmark only runs on 4-vCPU runners, so measure first on a larger machine (the owner's 16-thread desktop, asked for, or a larger runner), then raise the caps if it pays; results stay identical (CI compares chunked with sequential)
  - Status: Waiting (on a measurement above 8 CPUs)

- At the next FrameReader release, count the commits touching both products again (docs/frame-reader-product.md, "When to reconsider")
  - Why: 2.8.0 counted 1 of 21, under 1 in 20 for the first time; the trigger needs two releases in a row over at least 20 counted commits, and then means asking the owner, not splitting. 2.9.0 counted 0 of 6 and 2.10.0 0 of 4 (0 of 10 joined), too few: the window joins the next release's
  - Status: Waiting (on the next FrameReader release; at 2.11.0 the trigger was met and the owner chose not to split, docs/decisions/2026-10-24-framereader-stays.md)

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

- mkv/ts: the packet scan alongside decoding on the sequential path
  - Why: A frame's label is final once the scan's latest dts is past its time, so the scan can run beside decoding: max(read, decode) instead of the sum where reading is slow and the file fits the page cache; nearly nothing on local disks. Where dts ever goes backwards (joined or corrupt files), wait for the whole scan; chunked runs keep it first. An additive incremental FrameReader API (`ProbeOptions.OnPacket`). Choice 3 of docs/decisions/2026-10-27-pre-scan.md
  - Status: Maybe (the owner, 2026-10-27: parked). The rule isn't exact on joined files: once the second half's timestamps reach the sorted list, frames already labelled get other packets' labels (and, on a 25+30 fps join, other times); the dts-goes-backwards guard only covers frames decided after that. Simulated on joined TS files: 96 of 225 and 97 of 250 frames labelled differently, 0 on a clean file. An exact version would hold labels as provisional and restart on a mismatch (shots and warnings held until the scan ends, so streaming on mkv/ts emits nothing until then); docs/decisions/2026-10-27-pre-scan.md. A FrameReader start (`ProbeOptions.OnPacket`, `PacketTiming`) was built but not pushed; Matroska's first packets have no dts

- Option B: a FrameReader deferred take so FastYuv's row yadif converts off the decode thread
  - Why: The conversion thread (C, on by default for sequential HD since 2026-10-22) can't serve row-wise yadif: taking a frame deinterlaces every row, so those runs still decode, deinterlace and convert on one thread. A deferred take that keeps yadif's previous, current and next frames referenced would let the conversion thread compute only the rows it reads, deinterlacing included (then K workers): expected 1080i MXF sequential with row yadif from ~16.6 s to about 12-13 s. Only for the library configuration with FastYuv's `Yadif` (the CLI deinterlaces with FFmpeg's yadif, which C already covers); parallel runs gain little (the chunks keep the cores busy). A FrameReader change (docs/decisions/2026-10-22-decode-split.md, docs/frame-references.md)
  - Status: Maybe (the owner, 2026-10-22: parked)

- An opt-in fast mode that doesn't promise scenedetect's results: measure first
  - Why: The 2026-10-21 profile put 45-87% of CPU in decoding and up to 40% in deinterlacing (1080i), with scenedetect's own maths at a few percent, so exactness costs most where every full frame must be decoded and deinterlaced. Candidates, each changing some output: FFmpeg's `lowres` decoding at 1/2 or 1/4 size (MPEG-1/2, MPEG-4 Part 2, MJPEG: broadcast MPEG-2 above all), skipping h264's deblocking (`skip_loop_filter`), one field instead of deinterlacing on 1080i, scoring on YUV or luma without BGR/HSV. Measure on real broadcast files (MXF/TS MPEG-2, h264): the speed-up and how many cuts move against the exact run, before deciding whether a mode is worth having. The default stays exact (the owner, 2026-10-22: "Maybe row, measure first"); any mode would be opt-in with its deviations documented
  - Status: Maybe; measured 2026-10-27 (docs/decisions/2026-10-27-fast-mode.md): only one field instead of yadif plus `lowres` on MPEG-1/2/4/MJPEG is a candidate (about 2-2.8x on broadcast MPEG-2 and 1080i, adaptive about no cuts moved, content up to ~15% moved); `skip_loop_filter` and luma-only dropped; revisit with a real off-air TS/MXF with cuts

- Damaged MPEG-2 decodes differently from run to run with slice threads
  - Why: A PS with bytes cut out of its middle gave another frame (and other content scores) about 1 run in 15 with 8 decoding threads, on `main` too: FFmpeg's error concealment across slices depends on which thread finishes first. Undamaged files are unaffected, and OpenCV's decoder threads the same way, so scenedetect's own results on such a file aren't stable either. Find out whether one slice thread for MPEG-1/2 (or a concealment option) makes it repeatable and what it costs (found 2026-10-27 by ArrivingTimestampsTests, which now decodes with one thread)
  - Status: Maybe

- FastYuv's row yadif on 9-16-bit video with Vector128
  - Why: Its 8-bit rows already use portable `Vector128` (and `Vector256`), but `Row16` (interlaced 10-bit such as AVC-Intra and ProRes, with the in-process FastYuv `Yadif`) is scalar on every platform. The 10-bit conversion it feeds became 2-4x faster in #508. A port would need 32-bit lanes (16-bit samples' differences don't fit 16 bits), its own exactness argument, and timings on CI's ARM runners. It pays only for interlaced 10-bit sources deinterlaced in-process, which CI treats as informational
  - Status: Maybe

- FastYuv: 10-bit 4:2:0 of odd width
  - Why: FastYuv returns null there (swscale's scaled path forces full chroma interpolation, done in C; docs/fastyuv-10bit-422.md, "Odd sizes"), so on ARM such videos keep ARM's conversion rounding instead of x86's results; the one ARM gap left in the formats FastYuv covers. Rare (odd widths are unusual in 10-bit sources)
  - Status: Maybe

- corrupt.ts: two decoded frames with the same pts — OpenCV numbers both, we keep one
  - Why: Found 2026-10-22 measuring damaged files (docs/decisions/2026-10-22-damaged-files-best-effort.md): on a stand-in clip's corrupt.ts (a 640x360 29.97 fps Sintel trailer through tools/make-broken.py; the real trailer couldn't be downloaded), the decoder outputs two frames with the same pts (291). OpenCV numbers both (the first as frame 237 at 7.875 s), ShotDetector keeps one, so our cuts come one frame later (09.643 against 09.610) and ~236 stats rows differ; deterministic, at any thread count and on both decoder paths. It is the "frame 236 at 8.24 s, expected 7.87 s" of a chunk fallback message. Check first whether it happens on CI's real samples (corrupt.ts is informational there)
  - Status: Maybe (the owner, 2026-10-22: Maybe row). Not on CI's real samples (checked 2026-10-23, #482, with a throwaway debug workflow on ubuntu-26.04, as the proxy here blocks download.blender.org: the 854x480 trailer, SHA-256 b670602f..., FFmpeg 9.0, opencv-python-headless 5.0.0.93): corrupt.ts has 718 packets and 718 decoded frames with no repeated pts, at one decoder thread and at FFmpeg's default (ffprobe's frames and `-f framemd5`), and OpenCV reads 718 frames with no repeated time at one thread (`OPENCV_FFMPEG_CAPTURE_OPTIONS=threads;1`) and by default. compare.py there: 9 cuts, all exact, scene list CSV identical; stats differ in 288 rows from frame 104 on, ours at one thread or at the default (informational in CI; whether scenedetect's run there was really at one decoder thread wasn't confirmed, as its values didn't change with the setting). So only the stand-in clip shows the duplicate; left Maybe for a real file that does

- The last x86-only SIMD paths as Vector128: `Hsv.Convert` (AVX2/NEON only), `CvResize.HResize` (SSE2 only), `HashDetector` (scalar everywhere)
  - Why: On browser-wasm these run scalar (wasm profile, 2026-10-25). The ceiling is small: a few hundred ms of a 4.5 s Sintel run in the browser (decoding is ~80 %), nothing natively, where the existing x86/ARM paths stay. Worth doing only alongside other wasm work, with byte-identical results and no native slowdown
  - Status: Maybe

- Shots told before timestamps start over are not told again
  - Why: `DetectionOptions.OnShot` and `DetectStreamAsync` tell shots in order as cuts are confirmed; when timestamps start over (two streams joined) the cuts come out of order, the result's shots are sorted, and the shots told before that point aren't told again, so a listener's list can differ from `DetectionResult.Shots`. Documented in OnShot's XML doc (#497) but not fixed: e.g. tell a correction, or hold shots back once an out-of-order cut is seen
  - Status: Maybe

- Re-tune `SampledPays`' crossover after the loop filter
  - Why: Before the maps went through the loop filter, whole frames were 9% faster than remap on Tears of Steel (1280x534, 16% of the pixels read) and 7% slower on Sintel 1080p; the loop filter took about a seventh of ffmpeg's CPU off remap's side, so the two now look about equal at 1280x534 (docs/decisions/2026-10-27-executable-path-fixes.md). 0-9%, on 720p-class video only; measure on CI's x64 and ARM runners before moving the 0.25. Same results either way (both pipelines are CI-compared). The weekly benchmark's first `--decoder process --fast-yuv` row (run 40462238268, 2026-10-27) was 27-28% slower by wall than plain `--decoder process` at 640x360 (BBB) with equal CPU, i.e. waiting rather than work, so the crossover look covers FastYuv over the pipe too (the owner's choice, 2026-10-27)
  - Status: Maybe

- FrameReader's `ImageEncoder` to WebP
  - Why: Issue #542's encoder writes JPEG and PNG; WebP is refused ("isn't available yet") because FrameReader.Native has no WebP encoder. It needs libwebp built and linked into every native variant (linux glibc, musl and arm64, win-x64 cross-compiled, osx-arm64), with its licence (BSD-3-Clause) in the SBOM, then the `webp` format with libwebp's own options and `Capability.WebP` (docs/decisions/2026-10-27-image-encoder.md: JPEG and PNG first, WebP later)
  - Status: Maybe (the owner, 2026-10-27)

- An image encoder that scales from a decoded frame, exactly as ffmpeg's one command
  - Why: `ImageEncoder` gives the file ffmpeg writes from the same raw frame. ffmpeg's one command `-vf scale=...,format=F out.jpg` differs once it scales: its format negotiation folds the conversion to the encoder's full range into the user's scale (one swscale pass, where the reader scales and the encoder converts), and from an RGB frame of a BT.709-tagged source it keeps BT.709 for the YUV (a raw RGB frame gets BT.601). Encoding a decoded frame (`FrameDecoder.TakeFrame`, or a `VideoFrameReader` that hands out its frames before converting, through its fps graph) with the encoder's own graph `buffer -> scale=...:flags=bicubic -> format -> buffersink` would be ffmpeg's graph itself, byte for byte with `ffmpeg [-ss T] -i X -vf [fps=R,]scale=... -frames:v 1 out.jpg` (docs/decisions/2026-10-27-image-encoder-input.md)
  - Status: Maybe (the owner, 2026-10-27)

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
