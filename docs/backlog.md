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

- A deterministic default for interlaced 9-16-bit video
  - Why: FFmpeg 8.1's x86 SIMD yadif gives a slightly different picture on every run at 9 to 16 bits (its C code doesn't; docs/decisions/2026-10-08-row-wise-yadif-in-fastyuv-9-16-bits.md), so the MIT default path's stats on interlaced 10-bit (AVC-Intra, ProRes) vary from run to run, and so does the "lossless yadif copy" CI compares against; only FastYuv's LGPL `Yadif` is deterministic. Found in the 2026-10-31 decision review. First: a minimal reproduction for an FFmpeg report (the owner's to file, as the x264 one), then the options for the default (yadif with SIMD off for 9-16 bits only, if FFmpeg offers it per filter; bwdif differs from scenedetect's copy) put to the owner
  - Status: Open

- The executable's HLS warning with FFmpeg 9
  - Why: `VideoReader.ExecutableHlsWarning` says ffmpeg fetches https segments without checking certificates, whatever the executable's version; docs/decisions/2026-10-16-https-certificates-and-tls.md noted tls_verify's default is 0 "until libavformat 63", and CI's executable is FFmpeg 9.0 now. Check FFmpeg 9 against a self-signed segment server; if it checks them, warn only for libavformat 62 and older, and record it. Found in the 2026-10-31 decision review
  - Status: Open

- arch-diff: require 8-bit odd widths
  - Why: docs/decisions/2026-10-18-fastyuv-422.md left sintel_oddwidth.mkv reported, not required, "until its first ARM table is read" (tools/arch-summary.py, ci.yml's `--informational`); the same decision found 8-bit odd widths take the SSSE3 path FastYuv reproduces. Read a run's table: at 100 %, make it required; else record why not. Found in the 2026-10-31 decision review
  - Status: Open

- compare.py's `--always-bt601` silently turns off in-process decoding
  - Why: `_alwaysBt601` makes VideoReader use the executable (VideoReader.InProcess.cs), and compare.py passes it on every Windows run, so a local `--decoder inprocess` comparison on Windows checks the executable instead. Refuse the combination with a clear error, or say so in compare.py's report. Found in the 2026-10-31 decision review
  - Status: Open

- Sequential runs with twice the decoder threads: measure again
  - Why: 8 threads instead of 4 made sequential 1080p h264 1.19-1.22x as fast on 4 vCPUs with identical results on clean files (2026-10-21), kept off for damaged files' concealment, which the next day turned out to vary from run to run in both tools anyway; chunked runs already get it (#461). A PR with twice the CPUs (at most 16) for frame-threaded decoders up to 1080p sequentially too, CI's exact set (truncated.ts included) as the guard, timings on CI's runners; the owner decides on the result (docs/decisions/2026-10-31-sequential-decoder-threads-remeasure.md)
  - Status: Open

- Frame sources: LoadScenes for frames from the caller (#647)
  - Why: `ShotDetection.LoadScenes` takes only a video path; frames from the caller (`YuvVideo` + `YuvFrame`s) have no equivalent. shotdetector-web#90 (correcting cuts on the page) needs the exports from an edited cut list for a video read in the browser; done when they equal `LoadScenes(path)`'s byte for byte (docs/frame-source-parity.md lists the gap)
  - Status: Open, after #628, frame-source session

- VideoFrameReader Yuv420p: last chroma row zero at odd heights (#656); fix and test against ffmpeg -f rawvideo
  - Why: VideoFrameReader with FrameFormat.Yuv420p leaves the last chroma row as zeros at odd heights (321x241: U/V row 120), likely swscale's unscaled copy in Convert(); found by the #646 session, near #630
  - Status: Open, after #647, frame-source session

- FrameReader: managed picture and loudness measures (#646)
  - Why: The owner chose (2026-10-31) to move shotdetector-web's managed measures (signalstats, blackdetect, cropdetect and colour-bar equivalents; a BS.1770-4 loudness meter; silences and peaks) into FrameReader, so the page's QC report (shotdetector-web#94) and the delivery-QC command (#636) share one implementation checked against FFmpeg once; FrameReader's own `Loudness` (#642) runs on FFmpeg's ebur128, which the browser can't use
  - Status: Building: slice 1 (LoudnessMeter) merged in #655; slices 2-3 (SilenceFinder, PictureMeter) next

- Timeline: YouTube chapters export (#648)
  - Why: A chapters export beside Edl, Fcpx, Otio, Qp and ShotChanges, for the CLI and shotdetector-web's page (shotdetector-web#84's chapters part); YouTube's rules (first chapter at 0:00, at least three, each at least 10 s) to verify before building, and how shots merge into chapters that meet them to decide
  - Status: #654 merged 2026-10-31; #649 next

- CLI: exact contact sheet next to --save-images (#649)
  - Why: A grid of each shot's frames with its number, timecode and length, as PNG or PDF, built from `--save-images`' exact frames (the page's version, shotdetector-web#93, stays there with the browser's stills); format, layout and what it shares with `--save-html` to scope
  - Status: Open, after #645, one CLI session, #648 first

## Waiting

- Ask the owner whether to split FrameReader into its own repository, when one of these happens: a FrameReader issue or pull request from outside the project; FrameReader needing a release pace of its own; a consumer wanting an FFmpeg-free FrameReader surface on its own (a package question)
  - Why: The feature count (docs/frame-reader-product.md, "When to reconsider") was met four times by 2.17.0, each answered "don't split" for reasons it doesn't measure; since docs/decisions/2026-10-31-split-trigger-criteria.md each FrameReader release records the count as information only
  - Status: Waiting (on an outside event; none so far: shotdetector-web uses FrameReader's FFmpeg-free tone and waveform code, #494, without asking for a package)

- Optional: the owner times the sequential conversion thread on the 16-thread Ryzen
  - Why: docs/decisions/2026-10-22-decode-split.md made the conversion thread the default for sequential HD from 4-vCPU measurements, "unless [the desktop check] shows a slowdown"; the 2026-10-17 conversion-worker trial there was 1.04-1.07x slower. The commands are in that PR's body. Work on the owner's computer is theirs to start
  - Status: Waiting (on the owner)

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

- Frame sources: scale a frame of another size to the video's, as the file path does (port swscale's scaled 8-bit yuv420p to BGR path, x86's, to FastYuv)
  - Why: since #625 a stream whose resolution changes midway is refused through the frame source (`YuvFrame.Width`/`Height`), where Detect(path) scales such frames (bicubic, as OpenCV) and gives a result; FastYuv already has the bicubic filter set-up and the MMXEXT vertical scaler and output from its 10-bit port. Worth it once a frame-source caller meets resolution changes (adaptive streams, splices)
  - Status: Maybe (the owner chose refusing first, docs/decisions/2026-10-31-frame-source-size-change.md)

- Frame sources: timestamps that start over (two encodes joined)
  - Why: Detect(path) takes such a stream (frames labelled by their packets, shots told at the end: TimestampResetTests), but the frame source refuses it: YuvVideo.FrameTimestamps must rise, and without them the frames must come at the constant rate from frame 0's pts. Found checking #627 on the samples sintel_matrix_change.mkv and sintel_10bit_siting_change.mkv (docs/frame-source-parity.md). A caller can leave TimeBase null, at the cost of times that can differ from the file's
  - Status: Maybe (until a frame-source caller meets one)

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

- Frame sources: optional per-frame colour tags on `YuvFrame`
  - Why: `Detect(YuvVideo, frames)` takes the colour matrix and range once, on `YuvVideo` (docs/decisions/2026-10-24-per-frame-tags-fastyuv.md, "by design"), while `Detect(path)` follows each frame's tags since 2026-10-24/27; a spliced source whose matrix or range changes midway is the case where the two differ on x86. `YuvFrame` has carried per-frame field flags since docs/decisions/2026-10-30-frame-source-deinterlace.md, so nullable `ColorSpace`/`ColorRange` (null: the video's) would follow the same shape. Found in the 2026-10-31 decision review
  - Status: Maybe

- The CLI as a dotnet tool with FastYuv beside it
  - Why: The `shotdetect` binaries leave FastYuv (LGPL) out, so on ARM (Apple silicon too) they give ARM's default results, not x86's (docs/decisions/2026-10-17-arm-results.md). The CLI already ships LGPL FFmpeg libraries as separate, replaceable files; FastYuv as its own DLL beside the dotnet tool (not inside a Native AOT binary) could follow the same LGPL terms (docs/decisions/2026-10-01-cli-and-licensing.md). A licensing question for the owner before any work. Found in the 2026-10-31 decision review
  - Status: Maybe

- ShotDetector as Subtitle Edit's shot-change engine (#633)
  - Why: Subtitlers snap cues to shot changes; Subtitle Edit uses FFmpeg's scene score. Scoping: docs/ideas/subtitle-edit.md (no engine seam in SE; SE5 MIT, SE4 GPL-3.0; 3-5 PRs here plus 1 in SE)
  - Status: Maybe; first slice done 2026-10-31 (`--save-shotchanges` and `Timeline.ShotChanges`, SE's .shotchanges format); next, machine-readable progress for SE; the SE side waits on the owner

- A Jellyfin plugin: chapters from shots, credits and black segments (#634)
  - Why: Most rips have no chapters; Jellyfin 12 is net10.0 and ships jellyfin-ffmpeg 8.1. Scoping: docs/ideas/jellyfin-plugin.md (4-6 PRs; six owner questions)
  - Status: Maybe; first slice (scheduled task saving the strongest cut per N minutes as chapters) waits on the owner creating bisforboman/jellyfin-plugin-shotdetector (GPL-3.0)

- A video reader for Microsoft.Extensions.DataIngestion (#635)
  - Why: Video as searchable, timestamped chunks in .NET RAG pipelines. Scoping: docs/ideas/dataingestion-video-reader.md (verdict park: a preview API, waits on StanzaSharp#233)
  - Status: Maybe; first slice (examples/VideoRag, PR #641) in progress 2026-10-31

- A delivery-QC command: black, bars and tone, broadcast range, loudness (#636)
  - Why: What broadcasters check before accepting a file, mostly built already (FFmpeg's ebur128 through FrameReader, shotdetector-web's picture measures). Scoping: docs/ideas/delivery-qc.md (6-8 PRs; owner questions on where PictureMeter lives, subcommand or tool, freeze detection)
  - Status: Maybe; first slice (`shotdetect --loudness`, PR #642) in progress 2026-10-31

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
