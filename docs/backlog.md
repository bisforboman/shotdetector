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
  - Status: Open (2026-10-24: on x86 one known, stats only: a video whose colour tags change midway, through the executable with FastYuv or, on a 10-bit siting change, with the default path (the per-frame-tags row in Maybe; the samples sintel_matrix_change.mkv and sintel_10bit_siting_change.mkv are informational there, exact in-process since FastYuv follows each frame's tags there, docs/decisions/2026-10-24-per-frame-tags-fastyuv.md); on ARM the default results differ by FFmpeg's conversion rounding, documented in docs/decisions/2026-10-17-arm-results.md, while ShotDetector.FastYuv gives x86's results on 8-bit 4:2:0 and 4:2:2 and 10-bit 4:2:0 of even width, and MPEG-1/2, MJPEG and MPEG-4 Part 2 decode as on x86)

- Shot detection in the browser: a showcase app on browser-wasm decoding with ManagedCodecs (github.com/bisforboman/ManagedCodecs) and converting with ShotDetector.FastYuv, fed to ShotDetector through a public frame-source API
  - Why: ManagedCodecs' H.264 and MJPEG decoders are bit-exact with FFmpeg and already run on browser-wasm, and FastYuv converts in managed code, so detection can run with no native binaries at all and give a file's results (docs/decisions/2026-10-24-managedcodecs-showcase.md). The owner's shape (2026-10-24): ShotDetector takes decoded YUV frames from any decoder (not a ManagedCodecs backend inside FrameReader); the showcase lives in a new repository (e.g. bisforboman/shotdetector-web, own CI and GitHub Pages, ManagedCodecs as a submodule until it is on NuGet), plain HTML + JSExport with decoding in a web worker, MIT with FastYuv a separate, replaceable DLL in the bundle. First version: 8-bit 4:2:0 H.264 in MP4 at a constant frame rate, trailers rather than films (ManagedCodecs decodes at 2.3-3.6x FFmpeg's time on one thread). Exactness first: before any UI, ManagedCodecs + FastYuv must give the same scene and stats CSVs as the FFmpeg path, natively and under WASM
  - Status: In progress (2026-10-24: the frame-source API shipped in 3.14.0; stage 1 of the showcase (core library, native and WASM exactness checks: 120 of 120 CSVs identical) handed off on branch handoff/shotdetector-web to a new session for bisforboman/shotdetector-web. Open: ManagedCodecs has no license yet, the owner's call)

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

- Option B: a FrameReader deferred take so FastYuv's row yadif converts off the decode thread
  - Why: The conversion thread (C, on by default for sequential HD since 2026-10-22) can't serve row-wise yadif: taking a frame deinterlaces every row, so those runs still decode, deinterlace and convert on one thread. A deferred take that keeps yadif's previous, current and next frames referenced would let the conversion thread compute only the rows it reads, deinterlacing included (then K workers): expected 1080i MXF sequential with row yadif from ~16.6 s to about 12-13 s. Only for the library configuration with FastYuv's `Yadif` (the CLI deinterlaces with FFmpeg's yadif, which C already covers); parallel runs gain little (the chunks keep the cores busy). A FrameReader change (docs/decisions/2026-10-22-decode-split.md, docs/frame-references.md)
  - Status: Maybe (the owner, 2026-10-22: parked)

- An opt-in fast mode that doesn't promise scenedetect's results: measure first
  - Why: The 2026-10-21 profile put 45-87% of CPU in decoding and up to 40% in deinterlacing (1080i), with scenedetect's own maths at a few percent, so exactness costs most where every full frame must be decoded and deinterlaced. Candidates, each changing some output: FFmpeg's `lowres` decoding at 1/2 or 1/4 size (MPEG-1/2, MPEG-4 Part 2, MJPEG: broadcast MPEG-2 above all), skipping h264's deblocking (`skip_loop_filter`), one field instead of deinterlacing on 1080i, scoring on YUV or luma without BGR/HSV. Measure on real broadcast files (MXF/TS MPEG-2, h264): the speed-up and how many cuts move against the exact run, before deciding whether a mode is worth having. The default stays exact (the owner, 2026-10-22: "Maybe row, measure first"); any mode would be opt-in with its deviations documented
  - Status: Maybe

- Collect frame timestamps during decoding instead of a packet-pts pre-scan
  - Why: Detection on a file reads it twice: `MediaProbe.Packets` over every packet (VideoReader's probe, `packets: !headersOnly && !Streaming`), then decoding. The pre-scan feeds the frame labels (`PacketOf`), the frame count (progress, chunk boundaries) and MPEG-PS timestamps; doing without it means getting those during the pass or later. Worth it for files larger than RAM on slow shares (a read costs size / throughput: 20 GB at 100 MB/s is 200 s); it would also make #452's shared audio-and-detection pass worth revisiting (closed 2026-10-21: starting both at the same time already shares the read today)
  - Status: Maybe

- FastYuv: 10-bit 4:2:0 of odd width
  - Why: FastYuv returns null there (swscale's scaled path forces full chroma interpolation, done in C; docs/fastyuv-10bit-422.md, "Odd sizes"), so on ARM such videos keep ARM's conversion rounding instead of x86's results; the one ARM gap left in the formats FastYuv covers. Rare (odd widths are unusual in 10-bit sources)
  - Status: Maybe

- FastYuv and the executable path: convert with each frame's chroma location and colour tags, not the first frame's
  - Why: The default path converts each frame from its own chroma siting and colour tags (as OpenCV's sws_scale_frame does), and in-process so does FastYuv since 2026-10-24 (B2 below, docs/decisions/2026-10-24-per-frame-tags-fastyuv.md); since 2026-10-19 the executable path's scale filter and its FastYuv take the first frame's (`FrameDecoder.ChromaLocation`/`ColorSpace`/`ColorRange`, ffprobe's frame entries; docs/decisions/2026-10-19-chroma-and-colour-tags-from-the-frame.md) for the whole video, so a video whose tags change midway still differs there. Measured on built clips (samples/informational/sintel_matrix_change.mkv, sintel_10bit_siting_change.mkv): the executable's FastYuv on a matrix or range change, and the executable's default path on a 10-bit siting change, differ in stats after the change only, with every cut exact and 0 CSV cells on every path since #432; not seen in a real file (docs/decisions/2026-10-19-per-frame-tags.md)
  - Options: B1, frames whose tags differ take the in-process BGR fallback (about 5 lines, no speed cost on x64, but ARM's rounding on those frames); B2, a FastYuv converter per tag set chosen per frame (30-60 lines, x86's results on ARM too); C, the executable path's FastYuv from showinfo's per-frame colour line (needs pairing two stderr lines, then B2's choice); D, the executable's default path on a siting change: ffmpeg's scale never follows a frame's siting, so only by restarting ffmpeg at the change (files only, a restart and a GOP per change)
  - Status: Maybe (the owner, 2026-10-19: parked). B2 done for in-process FastYuv (2026-10-24, the owner's choice over B1, B2 + C and keeping it parked): 0 stats cells on the built clips there, required in CI; C and D remain

- corrupt.ts: two decoded frames with the same pts — OpenCV numbers both, we keep one
  - Why: Found 2026-10-22 measuring damaged files (docs/decisions/2026-10-22-damaged-files-best-effort.md): on a stand-in clip's corrupt.ts (a 640x360 29.97 fps Sintel trailer through tools/make-broken.py; the real trailer couldn't be downloaded), the decoder outputs two frames with the same pts (291). OpenCV numbers both (the first as frame 237 at 7.875 s), ShotDetector keeps one, so our cuts come one frame later (09.643 against 09.610) and ~236 stats rows differ; deterministic, at any thread count and on both decoder paths. It is the "frame 236 at 8.24 s, expected 7.87 s" of a chunk fallback message. Check first whether it happens on CI's real samples (corrupt.ts is informational there)
  - Status: Maybe (the owner, 2026-10-22: Maybe row). Not on CI's real samples (checked 2026-10-23, #482, with a throwaway debug workflow on ubuntu-26.04, as the proxy here blocks download.blender.org: the 854x480 trailer, SHA-256 b670602f..., FFmpeg 9.0, opencv-python-headless 5.0.0.93): corrupt.ts has 718 packets and 718 decoded frames with no repeated pts, at one decoder thread and at FFmpeg's default (ffprobe's frames and `-f framemd5`), and OpenCV reads 718 frames with no repeated time at one thread (`OPENCV_FFMPEG_CAPTURE_OPTIONS=threads;1`) and by default. compare.py there: 9 cuts, all exact, scene list CSV identical; stats differ in 288 rows from frame 104 on, ours at one thread or at the default (informational in CI; whether scenedetect's run there was really at one decoder thread wasn't confirmed, as its values didn't change with the setting). So only the stand-in clip shows the duplicate; left Maybe for a real file that does

- Frame-source detection in chunks: score a frame range of the caller's frames and merge, so a host can split a long video across several single-threaded runtimes (Web Workers)
  - Why: Measured 2026-10-24 (wasm AOT under Node, Sintel trailer): decoding is ~80 % of a browser run and detection ~5 %, so the levers are about decoding. Besides WebCodecs (shotdetector-web#15), splitting a long video across Web Workers, each with its own runtime and no wasm threads, would give about 2.5-3.5x on 4 cores. ShotDetector chunks in-process files already (docs/parallel-detection.md), but the frame-source path can't: it needs an API that scores a range (from a keyframe, with the detectors' warm-up the chunked path uses) and one that merges the ranges' results into exactly the sequential result. Pays only on long videos (chunks start at keyframes); ask shotdetector-web whether it wants it first
  - Status: Maybe (the owner, 2026-10-24)

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
