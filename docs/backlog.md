# Backlog

What ShotDetector aims to do next, in order (the user's priorities, 2026-10-06: robustness on real videos first,
then performance, then the road to 1.0). Update a status when work starts or lands; add an item when a divergence
or a gap turns up, with why it matters. Design questions go to [decisions/](decisions/).

Status: **Released** (on nuget.org), **Done** (on `main`, in the next release), **In progress**, **Open**, **Maybe**.

## 1. Robustness on real videos

- Real-world check: whole Blender films and codec clips vs scenedetect (`tools/realworld.py`, nightly)
  - Why: Synthetic clips and trailers can't show divergences on hours of footage, fades, dark scenes, or in HEVC/VP9/AV1 decoding
  - Status: Done (2026-10-06)

- Fix every divergence the real-world check finds
  - Why: The promise is identical results
  - Status: Open (2026-10-17: ARM's default results differ from x86's by FFmpeg's conversion rounding, documented, docs/decisions/2026-10-17-arm-results.md; on x86 none known)

- CI: x64 and ARM give the same results end to end: every detector's scene lists and stats on the sample clips, both decoders, written on each and diffed
  - Why: Each piece is tested on ARM (the HSV conversion on all 2^24 colours, the content score, the microbenchmark hash, the in-process tests with the linux-arm64 build), but nothing compares a whole run: decoding goes through a different native build per platform, and scenedetect on ARM isn't the reference (opencv-python's ARM wheels use KleidiCV), so the parity matrix runs on x64 only
  - Status: Done (2026-10-17): ci.yml's arch job (x64 and ARM; our native build in-process and BtbN's ffmpeg 9.0 through the executable; tools/arch-outputs.sh) and arch-diff (tools/arch-summary.py). Its first run found ARM differing (FFmpeg's yuv420p to BGR assembly rounds differently there); the owner chose to document it, with FastYuv as the way to x86's results on ARM (docs/decisions/2026-10-17-arm-results.md): arch-diff requires ARM with FastYuv to equal x64 on the 8-bit 4:2:0 clips and reports the rest

- ARM's results with FastYuv for 10-bit, 4:2:2, MPEG-2 and MJPEG video (MPEG-2 and MJPEG done)
  - Why: FastYuv gives x86's results on ARM for 8-bit 4:2:0 only (arch-diff's table, 2026-10-17): 10-bit and 4:2:2 take other swscale paths, and MPEG-2's and MJPEG's decoded frames already differ (their IDCTs round per platform), so those still differ on ARM
  - Status: Maybe for 10-bit and 4:2:2 (a FastYuv port of those swscale paths); MPEG-1/2 and MJPEG done (2026-10-17): ShotDetector decodes them with FFmpeg's C IDCT (`-idct simple`; in-process FrameReader's new `FrameDecoderOptions.CodecOptions`, so FrameReader itself still gives ffmpeg's default bytes), which x64's default already gives (checked with FFmpeg 8.1 and 9.0) and ARM's NEON IDCT doesn't (#384's checksums); arch-diff requires them too

- VideoWriter's rare CI mismatch: about 1 CI test run in 10-20, one file differs from `ffmpeg -c:v libx264`'s by a few hundred bytes (never locally, never in ffmpeg's own output). Ruled out (2026-10-10): x264 threads (1, both sides; the SEI settings match), filter-graph threads, frame padding (zeroed), the input frames (hashes identical), uninitialised heap (MALLOC_PERTURB_). Left: something in x264/FFmpeg inside a long-lived .NET process
  - Why: The exact-output promise; the check stays a hard failure (docs/decisions/), so every failure is a data point (the test prints both files' x264 settings and CI keeps both files)
  - Status: Done (2026-10-12): stress runs on Linux (200 runs per runner, CPU logged): only AVX-512 CPUs differ (about 0.4%; 0 in 1,800 on EPYC 7763s). Not padding or the stack, not .NET's own AVX-512 code, not the GC. Tiered JIT off: 0 in 3,200; x264 capped at AVX2 on both sides: 0 in 2,000. So x264's AVX-512 code with the JIT thread in one process (likely an over-read into memory that thread writes). CI's step turns tiered compilation off; FrameReader's README and VideoWriter's docs give the workarounds

- More sources: interlaced, 10-bit (h264/hevc), odd sizes (1366x768, 427x241), 4:2:2, long GOPs, MPEG-2 in PS/TS
  - Why: Each is a different decode/convert path
  - Status: Done (2026-10-06): all exact after the MPEG-PS fix; `.mpg` and 10-bit samples added to the parity set

- Telecined footage, VP9/AV1 in webm/mkv, mpeg4 in avi, fragmented mp4, image sequences (`%04d.png`)
  - Why: More decode paths
  - Status: Done (2026-10-06): all exact

- 10-bit video on Linux: ±1 in red/green on some pixels vs OpenCV's bundled FFmpeg (cuts identical)
  - Why: No ffmpeg CLI option reproduces it; an exact match needs our own 10-bit→BGR conversion in C# (like FastYuv for 8-bit). Compared informationally in CI
  - Status: Done (2026-10-11): not the conversion but where the chroma samples sit; OpenCV takes the frame's siting (h264: left), ffmpeg's command line drops it. Both paths now pass it; 10-bit h264 compared exactly

- AV1 on Linux: OpenCV's wheel can't decode it (0 frames), so nothing to compare against there
  - Why: Matches on Windows; excluded from the Linux real-world matrix
  - Status: Done (2026-10-06)

- Damaged files (`tools/make-broken.py`): corrupted mp4/TS, truncated mp4/TS, duplicate timestamps, a resolution change
  - Why: Found and fixed: frames after a decoder-dropped frame were mislabeled; a piped TS not starting at 0 was shifted. All identical to scenedetect now, except the resolution change, where OpenCV freezes on the last frame before it (documented, not reproduced)
  - Status: Done (2026-10-06)

- Damaged data: concealed pixels can differ from OpenCV's (decoder threading), so stats differ a little on corrupt files
  - Why: Matching would need OpenCV's exact decoder threading setup; cuts matched on every damaged file tested
  - Status: Done (2026-10-11): it was the thread count; the default is now OpenCV's (CPUs, at most 16). On Linux with 4 CPUs: corrupt.ts and truncated.ts exact at 4 threads only, corrupt.mp4 down from ~300 differing stats cells to 6

- Resolution change mid-stream (samples/broken/res_change.ts): scenedetect 0.7.1 finds 7 cuts, ShotDetector 2 (found 2026-10-11 on Linux). README says OpenCV repeats the last frame before the change, so scenedetect should find fewer; either opencv-python 5.0.0.93 handles the change now, or our side regressed
  - Why: A divergence on a broken-file case the README documents the other way round
  - Status: Done (2026-10-11): not the resolution but timestamps starting over: scenedetect labels frames by time (numbers go back, stats overwritten, cuts sorted) and OpenCV scales new-size frames to the first size in one conversion; both paths now do the same; stats where a time repeats merge per metric and the threshold detector keeps the stored average, as StatsManager does. Exact in CI on the core path

- scenedetect 0.7.2 (issue #177): its changes as `PySceneDetectVersion.V0_7_2`, the new default; 0.7.1 kept
  - Why: README's versions policy: the default tracks the newest scenedetect
  - Status: Done (2026-10-12): hist/hash defaults, min_scene_len as a frame count (#577), adaptive's from the target (#408), detect-threshold's min_out_length (CLI 0.1 s), save-keyframes; exact against 0.7.2 and, in 0.7.1 mode, against 0.7.1 (CI's parity matrix runs both)

- Widevine pssh data from the caller (issue #185): a DRM provider's WidevinePsshData in the pssh box unchanged
  - Why: Their provider (Conax) supplies it, with fields its license server may expect
  - Status: Done (2026-10-12): `EncryptionOptions.WidevinePsshData` (the user chose the data over the whole box or a general list of boxes); the box `pssh-box.py --pssh-data`'s, compared in CI

- `FFmpegLibraries.Missing(Capability...)` (issue #195): what the libraries lack for FrameReader's features
  - Why: Their startup check copied FrameReader's internals by hand
  - Status: Done (2026-10-12): a flags enum per feature (README's table); a test checks our build and full builds have everything but x264

- DASH segment names per content type (issue #191): Shaka's `video/` and `audio/` layout, directories created
  - Why: They rename folders and rewrite the MPD after the dash muxer, which has no such placeholder and creates no folders
  - Status: Done (2026-10-12): `$ContentType$` in `init_seg_name`/`media_seg_name`, folders created; written as a token per representation and renamed after the muxer, the MPD's templates rewritten; files byte for byte ffmpeg's with `$RepresentationID$` names

- Audio converted like the ffmpeg command line (issue #192): `ffmpeg -i IN -vn [-af G] OUT` in one call, with the CLI's automatic conversion to the encoder's formats (and its downmix normalization for integer samples)
  - Why: They reproduce the CLI's negotiation by hand to get its bytes
  - Status: Done (2026-10-12): `Audio.Convert`, byte for byte ffmpeg's on 5.1 PCM and AAC to MP3 (normalized and not), a graph into AAC, 96 kHz to MP3, metadata carried; `AudioWriter` takes the encoder's channel layouts too

- ffprobe's JSON from MediaProbe (issue #193): `-show_format -show_streams` keys and values, with the fields MediaInfo lacks (profile, level, aspect ratios, disposition, tags, ...)
  - Why: Their service stores ffprobe's JSON; they build it by hand and can't fill every field
  - Status: Done (2026-10-12): `MediaProbe.ProbeJson(path)`, a port of ffprobe's show_format/show_streams writer (not new MediaInfo properties: ffprobe opens every stream's decoder, which Probe shouldn't pay for); the same JSON as ffprobe's, parsed, on H.264 mp4 (and rotated), interlaced MPEG-2 MXF, 5.1 AAC MKV and MP3

- ShotDetector's `StreamInfo`, `StreamType`, `FieldOrder` (issue #194): FrameReader's in 2.0, `[Obsolete]` until then
  - Why: Both packages define them, so using both is ambiguous (CS0104); type forwarding needs the same namespace, so only a major version removes them
  - Status: Done on v2 (2026-10-13): ShotDetector uses FrameReader's three types; FrameReader.StreamInfo got a public constructor so the executable path fills it from ffprobe (duration_ts, time_base); Duration is now FrameReader's exact value, not ffprobe's six-decimal print

- FastYuv and frames of a new size (res_change.ts): the second half's stats differ and content finds 2 of 7 cuts at other frames
  - Why: OpenCV scales a new size's frames to the stream's size inside its one bicubic YUV-to-BGR conversion; FastYuv converts at the stream's size after ffmpeg scaled the YUV. Exact would need swscale's scaled conversion in FastYuv. Informational in CI
  - Status: Done (2026-10-12), in-process: frames of another size than the first skip FastYuv for the core path's exact conversion (user's choice over porting swscale's scaled conversion); identical to the core path on res_change.ts for all five detectors. The executable path (ffmpeg scales first) stays informational

- corrupt.ts: scenedetect's own stats vary between runs on Linux (frame 531's adaptive_ratio 1.02608 in one job, 1.02831 in another)
  - Why: Its error concealment races with the decoder threads, so exact can't be checked; informational in CI. Cuts always match
  - Status: Done (2026-10-11): ours varies too, see the next row

- Corrupt mp4 (samples/broken/corrupt.mp4): 6 stats cells still differ from scenedetect with OpenCV's thread count (from ~300); the TS and truncated files are exact
  - Why: Probably one more decoder setting (thread type, error concealment flags); probe like the thread counts
  - Status: Done (2026-10-11): not a setting but timing. A Linux probe (4 CPUs) ran both tools five times: on corrupt.ts scenedetect gave 5 different stats files and ShotDetector 4, on corrupt.mp4 5 and 2, and on each file one of ours was byte for byte one of scenedetect's. Frame-threaded error concealment depends on thread timing, in OpenCV's decoder and ours alike; locally (16 CPUs) both are stable and equal. Corrupt files stay informational in CI

- Parity on Windows and macOS in CI: the scenedetect comparison on windows-latest and macos (Linux only today), informational at first
  - Why: Catches platform differences like the Windows wheel's FFmpeg 7.1 before users do
  - Status: Done (2026-10-13): job `parity-os` (windows-latest, macos-latest), core path, the main comparison, informational in the summary

- scenedetect on macOS differs (parity-os, #207): per-frame stats differ on nearly every frame of 5 clips (bbb ogg/mov/m4v, sintel 1080p and divx), some cuts by 1-2 frames; in places only saturation. Find why (the macOS wheel's FFmpeg converting with Apple Silicon's own rounding?), e.g. against Linux on ARM, then decide whether Mac users should get it
  - Why: Windows matched all 84 comparisons; a macOS user comparing with scenedetect there would see these differences
  - Status: Done (2026-10-13): not macOS but ARM: opencv-python's ARM wheels (macOS and Linux) use KleidiCV 26.03 and Carotene for cv2.resize (downscales under 3x) and BGR2HSV, which round differently (HSV: saturation 1 lower on 0.04% of colours; resize: 1-2 on many pixels); macOS Intel's wheel has no FFmpeg. x86 stays the reference (docs/decisions/); README documents it

- Ubuntu 26 runners: check CI there (audiowaveform's PPA, BtbN's builds, scenedetect's wheels, the native build's glibc), then unpin ubuntu-24.04
  - Why: ubuntu-latest moves to Ubuntu 26 from 2026-10-19; pinned so it can't break CI unannounced
  - Status: Done (2026-10-11): all 36 checks passed on ubuntu-26.04 (a do-not-merge PR); pinned to ubuntu-26.04 rather than unpinned (the user's choice), so the next move is announced too

- Audio on the real films: realworld.yml also compares AudioReader's samples and SpectralStats with ffmpeg on the films' own audio (AAC, MP3, Opus, ...)
  - Why: Exactness is tested on synthetic clips only; real encoders' streams (priming, edit lists, odd frame sizes) are where a difference would show
  - Status: Done (2026-10-10): identical on all three films (AAC 44.1/48 kHz, MP3; samples as read, resampled, after a seek; SpectralStats); and Opus (Commons' Elephants Dream webm, 5.1; a 1909 recording), which found AudioReader's seek on Matroska 24 samples off (fixed)

- Container edge cases on streamed input: fragmented mp4, HLS/DASH segments, RTSP
  - Why: The streaming mode relies on ffmpeg's demuxer on a pipe; verify timestamps come out right. Done (2026-10-08) for fragmented mp4 and MPEG-TS piped and HLS over HTTP: same stats as the file (tests); a local .m3u8 also decodes in-process with our build (its hls demuxer). Not checked: DASH (needs ffmpeg with libxml2; not in our build) and RTSP (needs a server)
  - Status: Done (2026-10-08)

- Unit test for scenedetect 0.6.4's histogram re-initialisation at frame 1
  - Why: A guard reproduced without a test; add it and a mutation
  - Status: Done (2026-10-06)

- Generated cases against scenedetect (`PropertyTests`; `tools/oracle.py` writes 5,000 cases with scenedetect 0.7.1's and Python's answers to `Data/oracle.txt`): FrameTime's frame numbers, seconds, timecodes, `+ frames`, `-` and cut comparisons, Stats.PyFloat against repr(float); Stats.Csv's merge and the cut list's sort as FsCheck invariants
  - Why: Hand-picked cases can miss edge values (half-way rounding, NTSC rates, float formatting switches); mutation testing checks the guards, not the inputs
  - Status: Done (2026-10-11): found PyFloat writing 1e16 to 1e21 out in full where repr switches to exponents (fixed)

- Windows OpenCV wheel (FFmpeg 7.1) colour difference on BT.709
  - Why: Documented; decide whether to offer a `ColorMatrix` override for users comparing on Windows
  - Status: Done (2026-10-11): the wheel always uses BT.601 coefficients (range followed); bit for bit with `in_color_matrix=bt601`. No public option (the user chose a dev flag): the CLI's hidden `--always-bt601`, passed by compare.py on Windows

- scenedetect parity in-process: the parity job's comparisons with the CLI decoding in-process (our FFmpeg build), not only through the ffmpeg executable
  - Why: In-process is what users of the CLI release and the Native packages get; today CI compares it with the executable (InProcessTests), not with scenedetect directly (found 2026-10-12, #202)
  - Status: Done (2026-10-13): a decoder axis on the parity job (user's choice over switching the 0.7.2 jobs or one in-process job): the 0.7.2 jobs run through the executable and in-process, `--decoder` explicit in both, res_change.ts exact with FastYuv in-process

- FastYuv and frames of a new size on the executable path: flag them from showinfo's sizes and convert them exactly, as in-process does since #202
  - Why: ffmpeg scales a new size's YUV before FastYuv sees it, so res_change.ts stays informational there; rare video, and in-process is the default
  - Status: Done (2026-10-13): a frame whose showinfo size isn't the stream's stops the yuv420p read; the file is read again from the same start on a BGR pipeline, the frames already given skipped (a seek by time could land in the other encode). Identical to the core path on res_change.ts, whole and with time ranges and frame skip; res_change.ts exact in CI on every path

- Audio.Convert: 5.1 AC-3 to MP3 louder than ffmpeg's (issue #211): the CLI copies the decoder's downmix levels (AV_FRAME_DATA_DOWNMIX_INFO) to abuffer, and aresample uses them; now FrameReader's audio graphs do too
  - Why: Byte for byte like ffmpeg on AC-3 (MP3, and WAV through a downmixing filter); AAC and PCM carry no levels and were already equal
  - Status: Done (2026-10-13)

- VideoFrameReader with Deinterlace and FrameRate gives one frame more than `-vf yadif,fps=R` (51 vs 50 on a 2 s interlaced MPEG-2 clip; either alone is exact): the end-of-stream time fps gets after yadif?
  - Why: Its docs promise ffmpeg's frames; found with Video.Transcode (#240), which now converts only variable-rate input
  - Status: Done (2026-10-13, #253): yadif's frames carried their duration in its doubled time base; the fps feed now gets it in the stream's

- parity-os off the PR path: run the Windows/macOS comparison nightly or weekly (as realworld.yml), not on every PR
  - Why: It is informational yet takes 2 runners per PR, one of the 5 macOS slots; runners are the limit (concurrent jobs count per account)
  - Status: Done (2026-10-14): parity-os.yml, nightly (and on PRs that change it or the ffmpeg action); out of ci.yml and its gate

- parity-os: a pinned ffmpeg download with retries on Windows and macOS (as the btbn-ffmpeg action on Linux) instead of Chocolatey and Homebrew
  - Why: Chocolatey's ffmpeg install failed once (#234, exit 127); the versions also drift
  - Status: Done (2026-10-14): Windows takes BtbN's FFmpeg 9.0 through the btbn-ffmpeg action (now win64 .zip too); macOS stays on Homebrew (BtbN has no macOS build)

- VideoTests.UrlInputIsStreamed under load: failed in all 3 cases in one local full run, passed alone and in the next full run
  - Why: Its local HTTP server's timing under load, presumably; flaky tests erode trust in red
  - Status: Done (2026-10-14): its HTTP server answered one request at a time, so a reader that stopped reading without hanging up (the probe) held back ffmpeg's request: a hang whose odds depend on timing and file sizes (TestMedia's clips made it happen every run). Each request now gets its own task

- A test that the Auto decoder is the one the environment implies (in-process when the libraries load, the executable otherwise)
  - Why: The Clips fixture once loaded FFmpeg's libraries in the test process and silently switched every test to in-process decoding (2026-10-14); only the mutation job noticed, through three surviving guards
  - Status: Dropped (2026-10-14): Auto uses libraries anything in the process loaded, and InProcessTests load them, so in a full run the decoder already depends on test order; the mutation job (one test class at a time, no libraries loaded) and the parity matrix (process and inprocess legs) cover both paths explicitly

## 2. Performance

Measured 2026-10-06 (dotnet-trace thread sampling plus interleaved best-of-3 timings, Tears of Steel 1280x534):
our resize and scoring are ~4% of sampled thread time, and a full run takes 3.8 s against 2.8 s for ffmpeg
decoding and converting the same frames into nothing. On large frames both tools are bound by decoding, so
faster C# can't help there; what is left is the pipe (~0.5 s per 2 min), startup with ffprobe, and decode threads.

- Benchmark script and CI job (`tools/bench.py`, `benchmark.yml`, weekly and on demand): fps and peak memory vs scenedetect on the films
  - Why: Desktop timings vary by ±50% between identical runs; claims need numbers from a quiet runner
  - Status: Done (2026-10-06)

- README performance section from the CI numbers, including memory
  - Why: Users choosing between the tools want numbers. From benchmark.yml: in-process and executable, CPU time, process-tree memory, 2 CPUs and all
  - Status: Done (2026-10-07)

- Decode threads: 8 by default on the yuv420p fast path (decode-bound: 13% faster on HD, +70 MB at 1080p, user's choice); the core path stays at 4 (pipe-bound: more threads don't help)
  - Why: Biggest lever that keeps exact results
  - Status: Done (2026-10-06)

- Pipe bytes on the core path: ffmpeg's remap filter sends only the pixels the exact resize reads (0.44 vs 6.2 MB per 1080p frame); same results. 2-CPU CPU time 30 s -> 26 s (scenedetect 30 s), kernel time 6.8 s -> 2.0 s (issue #7)
  - Why: The core path's bottleneck on few CPUs
  - Status: Done (2026-10-06)

- Memory at 1080p: ~300 MB vs scenedetect's ~235 MB, now mostly ffmpeg's frame queues and decoder threads (our process ~50 MB). Slice threading cut ffmpeg alone to 76 MB but cost 18-40% speed in the full pipeline; --threads 1 helps on many cores
  - Why: Small containers (issue #7)
  - Status: Done (2026-10-07): in-process uses less memory than scenedetect on every film (107 vs 140 MB at 1080p, 2 CPUs)

- In-process gaps: rotated/flipped video, image sequences, AV1 (zlib and dav1d in our build)
  - Why: Files needed the executable for these; now only streams/URLs and exports do. Still the executable: 4:2:2 at 90/270 degrees, odd angles, rotated video deinterlaced (ffmpeg rotates before yadif)
  - Status: Done (2026-10-08)

- All CPUs: small films slower than scenedetect in-process (ARM and x64)
  - Why: Decoding, conversion and scoring ran serially on one thread; now decoding and conversion run on their own thread, eight buffers ahead (three made the handoffs as costly as decoding). Faster than scenedetect on all four films, both architectures
  - Status: Done (2026-10-08)

- Streamed input: spill an mp4 without faststart to a temporary file instead of refusing it
  - Why: Issue #7 (low priority there). Detected from the first bytes (mdat before moov); other streams still stream (the user's choice)
  - Status: Done (2026-10-08)

- Streamed input's extra time and memory on SD (3.8 s vs 1.4 s for the file, issue #7)
  - Why: Streams went through the executable; now in-process through a custom AVIOContext (probe from the prefix, ffmpeg's -start_time offset), as fast as the file path. URLs stay on the executable (no network in our build)
  - Status: Done (2026-10-08)

- Probe cost: scanning packets in the background while ffmpeg decodes
  - Why: Measured no gain (the end check needs timestamps from the first frame; short files scan fast); reverted
  - Status: Dropped (2026-10-06)

- In-process decoding (FFmpeg.AutoGen / Sdcb.FFmpeg, the C# counterparts of PyAV) instead of the ffmpeg pipe
  - Why: Removes the pipe (~15% on HD, core path) and the separate ffmpeg executable; costs native FFmpeg libraries per platform (AOT, Alpine) and their LGPL/GPL terms
  - Status: Done (opt-in, next row)

- In-process decoding as an opt-in backend (FFmpeg.AutoGen, FFmpeg 8.1 libraries): prototype exact and 40% less CPU than the ffmpeg command line on 2 CPUs (docs/decisions/)
  - Why: Issue #12: the command line is two thirds of the gap to scenedetect on Linux
  - Status: Done

- Big Buck Bunny (720p h264) in-process: ~1.7x scenedetect's wall time and 1.4x its CPU on 2 CPUs, where the other films are within 4-13%; find out why (decoder settings, audio packets, the conversion)
  - Why: Issue #12; the outlier in the 0.6.0 measurements. Cause: our exact resize cost more than decoding at that size (0.38 ms/frame); vectorised, now 1.2x scenedetect
  - Status: Done (2026-10-14): no longer an outlier; in-process is faster than scenedetect on it in every configuration of the [weekly benchmark](https://github.com/bisforboman/shotdetector/actions/workflows/benchmark.yml) (there `bbb_640x360_h264.mp4`: the archive's "720p" file is 640x360)

- SIMD in the resize/HSV/score loops
  - Why: ~4% of the time; not worth it until the above are done
  - Status: Done (2026-10-16): the resize's horizontal pass with SSE2 (3.4.0, #333; ARM: its own item); HSV and the scores were vectorised already

- In-process: resize straight from the converter's rows, no copy of the sampled pixels (the decoding thread resizes, with its own CvResize)
  - Why: The copy was 5-13% of the CPU (profiled with dotnet-trace); pinned to 2 CPUs, CPU -16% on 480p and -5% on 1080p; byte-identical on all samples
  - Status: Done (2026-10-13)

- Resize: the horizontal pass (CvResize.HResize) off the critical path, and source rows resized once
  - Why: A performance review (2026-10-16) found in-process detection bound by the decoding thread's own work, not decoding: the consumer waits 80-90% of the time, and the horizontal resize pass is 116 of 129 us per frame at 1280x534 (measured). Row reuse (OpenCV keeps the row it resized as the next row's first) is done; an AVX2 gather version measured no faster on Zen 3 (this desktop, and CI's EPYC 7763), so the rest is moving the resize to the idle consumer thread
  - Status: Done (2026-10-16): rows reused (3.3.0), then the horizontal pass with SSE2 (pmaddwd: a pixel's three channels at once; 145 -> 80 us per frame at 426x240, 129 -> 80 at 1280x534) and the resize on the caller's thread; in-process wall time 0.67x (Elephants Dream 426x240), 0.76x (BBB 640x360), 0.80x (Tears of Steel 1280x534) on this desktop, stats identical. ARM keeps the scalar pass

- Edges (--stats, edge weights): Sobel, dilate and non-maximum suppression in row bands across threads, then SIMD (integer math)
  - Why: Stats runs measured 2.2-3.5x slower than plain detection (EdgeDetector about 440-620 us per frame, scalar); the scoring thread is mostly idle (performance review 2026-10-16)
  - Status: Done (2026-10-16): SIMD on one thread was enough (Sobel 8 pixels at a time in 16-bit words, the dilation's window maximum 16 at a time, the suppression without bounds checks): 548 -> 145 us per frame at 256x144, edge maps hashed identical; --stats runs 0.51-0.60x wall on this desktop, stats identical. No threads (they'd add CPU)

- Conversion (BgrConverter) off the decoding thread: av_frame_ref'd frames to converter threads, order restored
  - Why: 148 us per frame at 1280x534, serial with decoding (measured); after the resize move, large frames would be decode-bound; costs a decoded frame of memory per worker
  - Status: Dropped (2026-10-17): FrameReader's DecodedFrame is in (a feature of its own), but conversion workers built on it measured no gain: plain detection 1.00-1.01x on Tears of Steel, 1.04-1.07x slower on BBB with 1-3 workers, stats identical. The decoding threads (about 9 cores busy on Tears) set the pace, not the conversion

- Frame handoff in batches (or a single-producer ring) instead of BlockingCollection per frame
  - Why: Take/Add cost 30-40 us per frame on the producer in a trace, about 20% of its time on SD (estimated gain 5-10% once the resize moves)
  - Status: Dropped (2026-10-16): System.Threading.Channels (single reader and writer) instead of BlockingCollection measured 1.01-1.07x slower, stats identical; the review's 30-40 us per frame in Take/Add was mostly the decoding thread waiting for free buffers, not overhead

- ARM: the resize's horizontal pass with AdvSimd
  - Why: 3.4.0's SSE2 pass (pmaddwd) is x86 only: ARM (Apple Silicon, the ARM runners) still runs the scalar loop, the measured cost on the critical path (145 -> 80 us per frame at 426x240 on x64)
  - Status: Dropped (2026-10-17): a portable Vector128 path (each pixel's bytes widened to 32-bit lanes, a * wa + b * wb) gave identical output but measured about twice as slow as the scalar loop on CI's ARM runners (Linux ARM 182 -> 332 us, Apple Silicon 127 -> 279 us per frame at 426x240); ARM's scalar loop is already fast

- Edges: the non-maximum suppression and hysteresis
  - Why: After 3.4.0's Sobel and dilation, --stats runs still take about twice plain detection; the suppression and hysteresis are the scalar part left
  - Status: Done (2026-10-17): the suppression skips 8 pixels at a time under the low threshold and reads neighbours without bounds checks, the output 16 at a time: 145 -> 122 us per frame at 256x144 (edge maps hashed identical); --stats runs 0.87-0.98x. Hysteresis was 2% of it. Edges are now about 2 s of a 9.5 s --stats run on Elephants Dream (plain detection 2.5 s): the rest is elsewhere (next item)

- 3.4.0 on 2-vCPU runners: check the weekly benchmark
  - Why: Moving the resize to the scoring thread added CPU on Tears of Steel (+13% on an 8-core desktop); on CPU-bound 2-vCPU runners the move could cost more than it gains. If it does, keep the SSE2 pass and move the resize back for few cores
  - Status: Done (2026-10-17): it doesn't cost them. benchmark.yml on main (run 39023756460) against the last run before 3.4.0 (38809873202, 2026-10-15), in-process wall time pinned to 2 CPUs: on ARM (the same hardware both times, scenedetect within 1%) Sintel 4.1 -> 4.1 s, Tears of Steel 30.6 -> 30.3 s, Big Buck Bunny 9.8 -> 9.7 s, Elephants Dream 7.7 -> 6.9 s; on x64 the runner's CPU was much faster this time (scenedetect's Tears of Steel 43.0 -> 25.3 s at 2 CPUs), so against scenedetect: Sintel 0.90 -> 0.92x (noise on a 4 s run), Tears of Steel 0.94 -> 0.93x, Big Buck Bunny 0.91 -> 0.90x, Elephants Dream 0.85 -> 0.78x. With all the runner's CPUs no film got slower on ARM, and on x64 only Sintel moved, 0.88 -> 0.90x. The resize stays on the scoring thread

- --stats runs: find where the time goes beyond the edges
  - Why: A --stats run on Elephants Dream 426x240 takes about 9.5 s against 2.5 s for plain detection (in-process, 2026-10-17), and the edges are now about 2 s of it: something else (every detector's metrics, the stats rows, writing the CSV) takes the other 5 s
  - Status: Done (2026-10-17): it was the edges after all: on real frames (Elephants Dream's, dark, so a low threshold) EdgeDetector took 306 us per frame against 127 on the synthetic benchmark, mostly the suppression and hysteresis. The suppression now does 8 pixels at a time and the map has a zero border: 306 -> 236 us on real frames; --stats runs 0.68-0.74x of 3.4.0 with both edge PRs

- Microbenchmarks in the repository (benchmarks/micro): the resize and the edge detector, on real frames, with an output hash
  - Why: The 2026-10-16/17 performance work used scratch projects: one compiled a stale copy of the file (a no-gain result that was wrong), and the synthetic edge images hid the real cost (127 us per frame against 306 on Elephants Dream's frames). Committed ones, with real frames and a hash of the outputs, make the next round's numbers trustworthy
  - Status: Done (2026-10-17): CvResize.ResizeRows from 426x240, 640x360, 1280x534 and 1920x1080 to the processed size, and EdgeDetector.Detect on the resized frames' V plane; keyframes of the realworld.py films (cached under the build output) and a seeded synthetic set; best-of-N us per frame and a SHA-256 of the outputs, `--hash-only` for a quick check. In ShotDetector.slnx so CI builds it

- --stats rows in the weekly benchmark (benchmark.yml)
  - Why: It measures plain detection only: the edge detector's work (--stats runs 0.68-0.74x in 3.5.0) has no CI tracking, so a regression there would go unseen
  - Status: Done (2026-10-17): bench.py --stats (scenedetect -s against shotdetect --stats in-process, content), all CPUs, the same four films; [first run](https://github.com/bisforboman/shotdetector/actions/runs/39027227136): shotdetect's wall time 0.59-0.78x scenedetect's on x64 (Tears of Steel 23.6 against 33.1 s) and 0.72-0.84x on ARM (18.4 against 25.5 s), with less CPU time on every film

- Edges on ARM: the non-maximum suppression with Vector128
  - Why: 3.5.0's suppression (SuppressRow) needs Vector256, so on ARM (128-bit vectors) it runs the scalar loop; Sobel, the dilation and the 0/255 output already use Vector128 there. Measure on CI's ARM runners first: the resize's portable vector path measured twice as slow there
  - Status: Done (2026-10-17): SuppressRow4, the same lanes at 4 ints where Vector256 isn't accelerated; edges on film frames (benchmarks/micro, interleaved against main, best of 15) 0.81-0.88x the time on CI's Linux ARM runner (Elephants Dream 356 -> 289 us per frame) and 0.72-0.82x on Apple Silicon (395 -> 286 us); outputs and the recorded synthetic hash unchanged, also on x64 with AVX2 off

- CI checks benchmarks/micro's output hashes (--hash-only --set synthetic) against recorded ones, on x64 and ARM
  - Why: The resize's and the edge detector's SIMD paths differ by CPU (SSE2 on x64, scalar on ARM, Vector256 or not): the pixel tests cover set sizes against cv2, but a recorded hash over the synthetic set fails a PR on any change in their output, on every runner
  - Status: Done (2026-10-17): benchmarks/micro/check-hash.sh in ci.yml's build (x64) and arm jobs against expected-hash.txt; the first runs gave the same pair on both

- CI: x64 also runs benchmarks/micro/check-hash.sh and the PixelTests with DOTNET_EnableAVX2=0 and with DOTNET_EnableHWIntrinsic=0
  - Why: Since #370 ARM takes the Vector128 suppression, so no runner exercises the scalar suppression any more, and x64 never runs the Vector128 one (it has Vector256); the scalar resize and HSV run only where SIMD is off. Seconds per run, and every SIMD and scalar path stays checked against cv2 on every PR
  - Status: Done (2026-10-17): a step in ci.yml's build job runs PixelTests, HsvTests, ContentScorerTests and check-hash.sh under each setting (about 30 s); a broken comparison in the Vector128 suppression fails both the pixel test and the hash

- benchmarks/micro on the films in the weekly benchmark (benchmark.yml, x64 and ARM)
  - Why: The per-frame resize and edge timings are measured only by hand (the 3.4.0/3.5.0 rounds, #370 on a throwaway workflow); a weekly table beside the whole-pipeline one shows a regression in them, and the CPU behind it, week to week
  - Status: Done (2026-10-17): a step after tools/bench.py's tables (built first, so the table holds only timings); [first run](https://github.com/bisforboman/shotdetector/actions/runs/39048657694), dispatched on the branch: both runners wrote the table

- benchmarks/micro's 1920x1080 case takes 9 frames from the Sintel trailer, against 60 for the other films
  - Why: The trailer has few keyframes, so its rows (resize and edges) are the noisiest in the weekly table (#376) and in compare.yml's; take frames other than keyframes from it, or another 1080p film from realworld.py's pinned list
  - Status: Done (2026-10-17): the trailer takes every 18th frame from 5 s instead of keyframes (60 frames, as the other films); the other films keep keyframes

- The resize on ARM with AdvSimd intrinsics (widening multiply-adds), not the portable vectors
  - Why: The portable Vector128 resize measured twice as slow as the scalar loop on ARM (2026-10-17), but NEON's own widening multiply-accumulate (vmlal) fits the fixed-point weights; measure on CI's ARM runners as #370 did (main against the branch, interleaved, film frames) and drop it if it doesn't win
  - Status: Dropped (2026-10-17): an AdvSimd horizontal pass (per pixel: the two pixels' bytes widened to words, smull by wa, smlal by wb, the SSE2 branch's loads and overlapping stores) gave identical output (the recorded hash on Linux ARM and Apple Silicon, on Linux ARM the same real-frame hashes as main) but measured 1.5-1.6x the time of the scalar loop on CI's Linux ARM runner (Elephants Dream 184 -> 288 us, Sintel trailer 259 -> 408 us per frame, four interleaved rounds). One pixel per vector leaves too little work for the loads and lane moves: ARM's scalar loop stays

- compare.yml: a branch against a base on chosen runners (workflow_dispatch), instead of throwaway debug/** branches
  - Why: The two ARM measurements of 2026-10-17 each needed a push-triggered workflow on a debug/** branch, which this repository's sessions can't delete (the owner did by hand), and the second copy's test filter picked up a clip test that failed on macOS
  - Status: Done (2026-10-17): inputs base, filter, runners, rounds; the branch's pixel tests and recorded hash first, then benchmarks/micro on the films interleaved, the last round's tables in the summary, and a failure when the outputs differ

## 3. Toward 1.0

- API freeze review: detectors/ContentScorer/EdgeDetector internal, VideoReader trimmed, FrameTime raw fields hidden, DetectAsync (docs/decisions/)
  - Why: Minor versions may still change the API until 1.0
  - Status: Done (2026-10-06)

- Docs pass: README install section and current CLI/export docs; XML docs on every public member, now enforced (CS1591 is an error)
  - Why: First impression for NuGet users
  - Status: Done (2026-10-06)

- A small example app
  - Why: The README's snippets cover the API; add one if users ask
  - Status: Done (2026-10-11): examples/ShotDetector.Example (probe, shots, tone, waveform), built with the solution

- Frame-rate override (`-f/--frame-rate`, incl. image sequences): `DetectionOptions.FrameRate`, CLI `--frame-rate`, exact on video at 30/29.97/23.976/12.5, image sequences and 0.6.4
  - Why: Image sequences and broken headers need it
  - Status: Done (2026-10-06)

- split-video options (`--copy`, `--high-quality`, `--rate-factor`, `--preset`, `--args`, `--expand`): `SplitOptions`, CLI `--split-*`; 60 of 60 clips byte-identical to scenedetect's. Also fixed: `-sn` (subtitles dropped) in 0.7.1 mode too
  - Why: Parity on the command surface
  - Status: Done (2026-10-06)

- save-html / export-html: `Shots.Html`, CLI `--save-html`; byte-identical pages (with and without thumbnails)
  - Why: Parity on the command surface
  - Status: Done (2026-10-06)

- list-scenes `--skip-cuts`, `-q`; the CSV's `Timecode List:` row
  - Why: Our CSV lacked scenedetect's first row
  - Status: Done (2026-10-06)

- Timeline exports: save-edl, save-fcp (fcpx/fcp7), save-otio, save-qp (`Timeline`, CLI `--save-*`); byte-identical, checked in CI (`tools/compare-timeline.py`)
  - Why: Editors and encoders import these
  - Status: Done (2026-10-06)

- Detection options: several detectors in one run (per-detector settings, user's choice), --drop-short-scenes, --merge-last-scene, detect-content --filter-mode suppress, -d/--downscale; checked in CI (`tools/compare-options.py`)
  - Why: Parity on the command surface
  - Status: Done (2026-10-06)

- File name templates (save-images/split-video -f, every variable), -o, $VIDEO_NAME in output paths
  - Why: Parity on the command surface
  - Status: Done (2026-10-06)

- load-scenes: `LoadScenes`, CLI `--load-scenes`; FrameTimecode's seconds kind added to `FrameTime` (typed times print their nearest frame); checked in CI (`tools/compare-load-scenes.py`)
  - Why: Re-exporting edited lists
  - Status: Done (2026-10-06)

- Config file (`--config`, scenedetect.cfg): global, detector and output sections; add-last-scene can be turned off; checked in CI (`tools/compare-options.py`)
  - Why: Parity on the command surface
  - Status: Done (2026-10-06)

- scenedetect features still missing: save-images `--quality`/`--compression` (our images come from ffmpeg, so only approximately), split-video `--mkvmerge` (external tool), filename templates (`$VIDEO_NAME`...)
  - Why: Parity on the command surface, not only on results
  - Status: Partly done (2026-10-14): `--quality`/`--compression` (PNG and WebP as OpenCV's, JPEG mapped); the filename templates were already done (2026-10-06, the row above). Left: `--mkvmerge` (Maybe)

- Issue #17: `ShotDetectionException` with a reason (ffmpeg not found, invalid input, decode failed), derived from InvalidOperationException so existing catches still work
  - Why: Job workers retry a deployment problem and fail bad input
  - Status: Done (2026-10-06)

- Issue #19: `ActivitySource` "ShotDetector", one span per detection (frames, fps, size, pipeline, decoder)
  - Why: Shows in OpenTelemetry traces without wrapping each call
  - Status: Done (2026-10-06)

- Issue #18: SourceLink and symbol packages, CI-deterministic builds, GitHub build attestations for packages and binaries (docs/decisions/)
  - Why: Approval of a production dependency
  - Status: Done (2026-10-06)

- Issue #16: `Deinterlace` option (yadif before the resize); results match scenedetect on a losslessly deinterlaced copy
  - Why: 1080i broadcast files without a re-encode
  - Status: Done (2026-10-06)

- In-process decoding by default when FFmpeg 8.1's libraries load, the ffmpeg executable otherwise (docs/decisions/)
  - Why: Faster than scenedetect and ~half the memory for users who pass no flag
  - Status: Done (2026-10-07)

- `ShotDetector.Native` (LGPL): FFmpeg 8.1 shared libraries for win-x64, linux-x64, linux-musl-x64, osx-arm64
  - Why: In-process with no FFmpeg libraries installed (built by native.yml, packed and bundled by release.yml)
  - Status: Done (2026-10-07)

- Probing in-process (stream properties, packet timestamps, rotation) when the libraries load, instead of ffprobe
  - Why: With ShotDetector.Native, the last reason ffmpeg must be installed. Same text as ffprobe prints, compared in tests (7 clips, 7 colour formats)
  - Status: Done (2026-10-07)

- 10-bit exactness via the in-process converter (OpenCV converts with the legacy swscale interface, which the sliced path uses)
  - Why: Closes the known 10-bit divergence on Linux if it matches. Tried on Linux (10-bit 4:2:0, 4:2:2, BT.709 h264 against scenedetect 0.7.1): in-process gives exactly the executable's values, both differ from OpenCV's as before; the difference isn't the swscale interface
  - Status: Dropped (2026-10-07)

- Issue #35: `DeinterlaceMode.Auto` (field order) and `ShotDetection.Probe` (headers only)
  - Why: A worker can drop its own ffprobe call
  - Status: Done (2026-10-07)

- Issue #42: decoder in progress reports, `FfmpegLibrariesNotFound`, `CanDecodeInProcess`, `VideoInfo.Container`/`HasAudio`
  - Why: API for 0.9.0
  - Status: Done (2026-10-07)

- Issue #42: deinterlacing in-process (libavfilter with only yadif in our build)
  - Why: 1080i broadcast is most of a user's input. Done, identical, but not faster: yadif on whole frames is the cost (80 s 1080i MXF, 2 CPUs: 13.3 s vs 13.4 s with the executable; 5.7 s without deinterlacing)
  - Status: Done (2026-10-08)

- Deinterlacing only the rows and columns the resize reads: a C# port of yadif's arithmetic (LGPL, so in ShotDetector.FastYuv), checked against FFmpeg's yadif
  - Why: The real speed-up for 1080i: yadif costs ~7.6 s of 13.3 s there and the resize reads ~7% of the pixels. Done as `DetectionOptions.Yadif` / FastYuv `Yadif`, rows only (columns come in pairs ~7 px apart, not worth it): 80 s 1080i MXF 7.5 s vs 11.6 s on 2 CPUs, 5.0 s vs 5.5 s on all
  - Status: Done (2026-10-08)

- Issue #42: interlaced broadcast samples in CI (XDCAM HD MPEG-2 4:2:2 MXF, AVC-Intra, ProRes), parity with Off and against a lossless yadif copy with Auto
  - Why: Confidence for broadcast input
  - Status: Done (2026-10-08)

- 1.0 release plan: what must hold (parity matrix green for N releases of scenedetect/ffmpeg), version policy afterwards
  - Why: So 1.0 means something. Plan (revised 2026-10-08): 1.0 when stable, from fixing and testing; no quiet period. Policy in README "Versions" (docs/decisions/)
  - Status: Done (2026-10-08)

## 4. After 1.0 (ideas, 2026-10-08)

- Track scenedetect releases: a weekly CI job that opens an issue when PyPI has a newer scenedetect
  - Why: Matching scenedetect is the point; a new release means a new compatibility mode (the default tracks the newest, README "Versions")
  - Status: Done (2026-10-09)

- FFmpeg 9's libraries (libavcodec.so.63): load them in-process, or at least test what Auto does
  - Why: Distros will move; today Auto falls back to the (slower) executable there, and our Native packages stay on 8.1
  - Status: Done (2026-10-09): tested and documented, not loaded (docs/decisions/). With only FFmpeg 9.0.2 there, Auto uses its executable and the results equal in-process 8.1's on 14 samples x 5 detectors with stats; CI's parity job runs this way (FFmpeg 9.0 executable, no libraries)

- Move to FFmpeg 9's libraries (FFmpeg.AutoGen 9, Native packages on 9.0.x) when opencv-python's Linux wheels bundle FFmpeg 9
  - Why: scenedetect's frames come from OpenCV's FFmpeg; moving earlier risks bytes OpenCV doesn't give, and drops system FFmpeg 8 in-process
  - Status: Maybe (2026-10-14: FFmpeg.AutoGen 9.0.1.1 is out; opencv-python 5.0.0.93 and 4.14.0.94 still bundle libavcodec 62, so the trigger hasn't fired; the user: wait for OpenCV)

- The test clips with FFmpeg 9 on PATH: the interlaced clip's `-top 1`
  - Why: FFmpeg 9 dropped the encoder option ("Codec AVOption top ... is not a encoding option"), so the Clips fixture failed and with it every VideoTests test, for anyone with FFmpeg 9 on PATH (Homebrew has 9.0.2); found on a macOS run 2026-10-17. A step toward the row above
  - Status: Done (2026-10-17): `-vf setfield=tff` instead (the encoder takes the field order from the frames, as ClipMaker's TestMedia already did): the same bytes as before with BtbN's 8.1.3, and the whole ShotDetector.Tests suite passes with BtbN's 9.0.2 on PATH (391 of 391) as with 8.1.3

- A parity leg through the FFmpeg 9 executable, informational
  - Why: The question the move to FFmpeg 9 hinges on is whether 9's decoders and swscale give OpenCV's bytes; with the test suite passing on 9 (#374) and BtbN's 9.0 already pinned in the action, one compare.py leg with `--decoder process` and FFmpeg 9 against scenedetect answers it for the executable path before the trigger (opencv-python's wheel) fires. Not required: the reference is still FFmpeg 8
  - Status: Dropped (2026-10-17): already covered. ci.yml's parity matrix runs on BtbN's FFmpeg 9.0 (`FFMPEG: '9.0'`), so its required `process` legs already compare the FFmpeg 9 executable with scenedetect, and pass. What the move still needs answered is in-process decoding with FFmpeg 9's libraries, which needs FFmpeg.AutoGen 9: the move itself

- README performance: 1.0's x64 and ARM benchmark, 2-CPU figures and memory
  - Why: Issue #7 asked for 2-CPU numbers; the README table predates in-process decoding
  - Status: Done (2026-10-09): x64 and ARM, 2 and 4 CPUs, wall/CPU/memory from a benchmark.yml run on main

- DASH and RTSP input checked against the file
  - Why: fMP4, TS and HLS are (tests); DASH needs ffmpeg with libxml2, RTSP a test server in CI
  - Status: Done (2026-10-11): DASH over HTTP gives the file's stats (VideoTests); RTSP, a live source the reader joins late, is checked in CI against mediamtx serving a looping clip: cuts exactly one shot (50 frames) apart

- Live RTSP read 18 frames, then stopped, when mediamtx's publisher looped the clip (`-stream_loop -1`) and/or ShotDetector got `--duration` (2026-10-11, Linux CI); a plain publisher without `--duration` reads it all. Which of the two, and whether it's ours
  - Why: Could be a real bug with live input (e.g. how `--duration` ends a stream), or the publisher's
  - Status: Done (2026-10-11): neither. A Linux probe with a fresh server per run: looped or not, `--duration 8s` gives 199 stats rows (8 s) on both decoder settings; without it a looped source runs until stopped, as a live one should. The short and hung reads came from joining an on-demand stream an earlier reader had used up (mediamtx). CI now also checks the looped source with `--duration`

- HLS segments over https with the certificate checked: in-process, wrap the format context's io_open to add tls_verify=1 to every nested open
  - Why: FFmpeg's HLS opens segments, keys and variant playlists with a fixed option list (ffio_copy_url_options) that leaves tls_verify out, so they're read unchecked even when the playlist was checked (2026-10-16, source and a local run); the executable path has no such hook
  - Status: Done (2026-10-16): Demuxer wraps io_open in-process (unless InputOptions set tls_verify=0 or a ca_file); the executable path still has no hook

- URLs in-process: network protocols (and TLS for https) in our FFmpeg build
  - Why: URLs are the last input on the executable; TLS means another linked library (licence, updates)
  - Status: Done (2026-10-16): http and https everywhere in our build, with the certificate checked: Schannel (Windows), Secure Transport (macOS), a static OpenSSL 3.5 (Linux, ca_file from the system's bundle); the build script checks a trusted and a self-signed server on each platform (docs/decisions/2026-10-16-https-certificates-and-tls.md)

- Rotation gaps: 4:2:2 at 90/270 degrees (transpose converts the format first), rotated video deinterlaced (ffmpeg rotates before yadif)
  - Why: Rare, and the executable handled them exactly (it turned out it didn't: see the status)
  - Status: Done (2026-10-11), and more than a gap: OpenCV turns the converted BGR image by the display matrix's angle (flips ignored), ffmpeg's autorotate turns the planes first. Both paths now turn like OpenCV (rotated 10-bit and 4:2:2 at 90 degrees were off on both), every format in-process, deinterlaced too

- Hardware decoding
  - Why: Only if bit-exact with software decoding, proven per codec; the conversion and analysis would still be ours. Built as FrameReader's opt-in HardwareDecoding (Windows, D3D11VA): identical pictures on the user's GPU, half the CPU, 3.6x the wall time
  - Status: Done (2026-10-09)

- Investigate: a FrameReader `Remux` (a file's streams into separate files without re-encoding, e.g. video-only mp4 and an .m4a, as `ffmpeg -map 0:v -c copy`)
  - Why: The user asked whether ShotDetector or FrameReader can split a video into audio and video files; both only read today (our build has no muxers or encoders). Needs a few muxers in the native build (mp4, matroska/webm, adts, wav; LGPL, no encoders). Investigation only: whether it's wanted is open
  - Status: Done (2026-10-10): `Remux.Copy`, with the MP3/AAC/muxers row

- Investigate: an encoding package (issue #81: H.264 with pixel format, -g/-keyint_min and forced keyframes; MP3), opt-in beside the decode-only Native packages: LGPL (LAME, OpenH264, mp4/matroska/adts muxers) vs a separate GPL x264 package; H.264 patents (OpenH264's cover only applies to Cisco's own binaries); OS/GPU encoders as an option; the API (writers mirroring VideoFrameReader/AudioReader); the native size each adds; overlap with Remux's muxers
  - Why: Declined on #81 as out of scope, reopened as a question by the user (2026-10-09): a sub-package keeps the core's licences as they are. Investigation first, then a decision with numbers
  - Status: Done (2026-10-09): docs/encoding-package.md; decided: MP3, AAC and muxers in ShotDetector.Native, H.264 not in-process (docs/decisions/)

- MP3, AAC and muxers in ShotDetector.Native: LAME (static, LGPL), FFmpeg's AAC encoder, mp4/mov/matroska/webm/adts/mp3/wav muxers; FrameReader writers (audio to MP3/AAC/WAV, and Remux's stream copy), byte for byte ffmpeg's command line
  - Why: Issue #81's MP3 encoding and the Remux investigation's muxers, +~0.5 MB zipped per platform, licences unchanged. H.264 stays on the ffmpeg executable (SplitVideo's Args) until the AVC patents' US expiry (~late 2027)
  - Status: Done (2026-10-10): `AudioWriter` and `Remux.Copy`, byte for byte ffmpeg's output; LAME, AAC, PCM and 16 muxers in the build

- H.264 encoding package, opt-in beside ShotDetector.Native, using an encoder directly (not a second FFmpeg build): Cisco's OpenH264 binary downloaded at runtime to the user's machine (the only setup Cisco's patent cover applies to: separate download, user can switch it off, attribution; non-commercial use, commercial reading uncertain; no musl build; baseline-class quality, VMAF 90 vs x264's 94), or x264 under GPL; frames to an mp4 with the muxers Native already has
  - Why: A separate package settles the licence (GPL stays opt-in) but not the AVC patents, which follow whoever distributes an encoder. Revisit when the US patents expire (~late 2027) or a user asks (docs/encoding-package.md)
  - Status: Done (2026-10-10), revisited for #81's user: x264 in a GPL build of our libraries (ShotDetector.Native.Gpl.<rid>) with FrameReader's VideoWriter (docs/decisions/); that package dropped 2026-10-11, VideoWriter needs libraries with libx264 of the user's own

- A general frame-reader library extracted from the in-process decoder ([frame-reader-library.md](frame-reader-library.md)). Done: loading, probe, decoder, VideoFrameReader (exact frames at a size and format, seeking, fps, reading forward with GOP jumps, KeyframesOnly, ReadAt in parallel), AudioReader, opt-in hardware decoding on Windows, benchmarks (1.5-1.6x faster than the ffmpeg pipe for whole frames, 2.5-3x than FFMpegCore). Left: the rows below
  - Why: Fast in-process frame access for other .NET analysis tools; process-based wrappers like FFMpegCore spawn ffmpeg per call. Same repo, one-way dependency, no change to ShotDetector's API or results
  - Status: Done (2026-10-09)

- ShotDetector on FrameReader's public API: remove FrameReader's InternalsVisibleTo for ShotDetector by giving the public API what ShotDetector's conversions use (the decoded frame or its sampled rows, crop, a Stream with a prefix already read)
  - Why: The plan's end point before FrameReader can stand alone; until then ShotDetector reaches into its internals
  - Status: Done (2026-10-09): `BgrConverter` (public) does the conversions, ShotDetector wraps its own prefixed Stream; InternalsVisibleTo ShotDetector removed

- Track FFmpeg point releases: a weekly check that opens an issue when FFmpeg 8.1 has a newer release than the native build's (8.1.3)
  - Why: Security and decoder fixes reach ShotDetector.Native and the CLI only when the build script's version moves
  - Status: Done (2026-10-09)

- FrameReader as its own NuGet package (prerelease first)
  - Why: Useful without ShotDetector now that it reads exact frames, seeks, reads audio and has the speed options; decided "internal until the API settles" (docs/decisions/, 2026-10-08)
  - Status: Done (2026-10-10): the `FrameReader` package, a preview released with ShotDetector (ShotDetector's version plus -preview)

- FrameReader stable (1.5.0) and a ShotDetector dependency; ReferenceTone `SearchDuration`; `FFmpegLibraries.HasEncoder`/`HasFilter`/`HasMuxer` with a README list of what each part needs
  - Why: Issue #81's user: what they need to move their tone job (find tone, notch it with equalizer, write MP3) in-process on Alpine's FFmpeg
  - Status: Done (2026-10-10)

- Hardware decoding on Linux and macOS (VAAPI, VideoToolbox) behind the same option
  - Why: Windows only today; worth it only where CPU is the limit, and needs real hardware to test (CI runners have no GPUs)
  - Status: Maybe

- Fuller probe (issue #81): every stream (video, audio, subtitle, data) with its type, index, codec and codec tag (hvc1/hev1...), audio details (sample rate, channels, layout), in FrameReader's MediaProbe and ShotDetection.Probe
  - Why: Lets a pipeline drop its separate ffprobe call; both frame rates and the full container name are there already. No ffprobe-JSON emulation (a partial copy would mislead code written for the real one)
  - Status: Done (2026-10-09)

- Spectral statistics per window (issue #81): FFmpeg's own aspectralstats filter (spectral centroid and the rest) over AudioReader's samples, exact with ffmpeg; its filter added to our build
  - Why: Audio analysis such as finding a sustained tone without ffmpeg; AudioReader itself is built but unreleased (see "FrameReader as its own package")
  - Status: Done (2026-10-09): `SpectralStats` over an AudioReader, equal to ffmpeg's printed values window for window; aspectralstats, abuffer and abuffersink in our build

- Waveform peaks (issue #81): BBC audiowaveform-compatible .dat (8/16-bit, points per second) from AudioReader
  - Why: Display data without another tool; equal to audiowaveform's where both decode the same samples (WAV, FLAC), not always for lossy codecs
  - Status: Done (2026-10-09): `WaveformData` (Read, Min/Max, Save as .dat), compared byte for byte with audiowaveform in CI

- Reference tone (1 kHz line-up tone, "bars and tone"): detect it and optionally trim it ([reference-tone.md](reference-tone.md))
  - Why: Source files can start with 30-60 s of tone that counts as programme content and skews audio analysis; opt-in, outside scenedetect parity
  - Status: Done (2026-10-09): FrameReader `ReferenceTone.Find`, ShotDetector `FindReferenceTone` and `TrimReferenceTone`, CLI `--find-tone`/`--trim-tone`; tone only (bars go with it)

- Audio filter graphs (issue #81): run an ffmpeg audio filter graph over AudioReader, with filters active only in time windows (e.g. equalizers)
  - Why: Needs those filters in our build, or a full FFmpeg brought by the user
  - Status: Done (2026-10-09): `AudioFilter` over an AudioReader, byte for byte ffmpeg's -af output; EQ/level, dynamics, loudness and detection filters in our build

- The CLI's 1.0.0 symbol package (never pushed: the 500 during the release)
  - Why: Goes up with the next release (release.yml now pushes symbols on their own)
  - Status: Done (2026-10-09): 1.1.0 pushed the CLI's symbol package; 1.0.0's stays missing

- Track dav1d and zlib releases in upstream.yml, as FFmpeg's point releases are
  - Why: Both are linked into the Native packages and the CLI; their security fixes reach users only when build-ffmpeg.sh's versions move
  - Status: Done (2026-10-09): upstream.yml's libraries job; zlib 1.3.2 and dav1d 1.5.4 are already out (ours 1.3.1, 1.5.1), so its first run files both

- Run upstream.yml by hand once after it merges and check it files nothing at the current versions
  - Why: Its curl/grep against PyPI and ffmpeg.org was only dry-run locally; a page format change would silently find nothing
  - Status: Done (2026-10-09): run twice by hand; the first filed #91 (zlib 1.3.2) and #92 (dav1d 1.5.4), the second found both already filed; scenedetect and FFmpeg current

- Streams over a URL: a test that `Probe` of a file served over local HTTP (the ffprobe JSON path) gives the file's `Streams`
  - Why: Files are compared in-process vs ffprobe (`StreamsAreFfprobes`); URLs always take the ffprobe path and aren't checked
  - Status: Done (2026-10-09): URLs had no Streams at all (null for streamed input); now one ffprobe call on first use, tested equal to the file's (docs/decisions/)

- CLI `shotdetect probe`: VideoInfo and `Streams` as a table or JSON
  - Why: The fuller probe (issue #81) is library-only; the CLI can't show it
  - Status: Done (2026-10-10): `--probe` / `--probe-json` (flags, like `--find-tone`)

- Automatic ApiBaselineVersion bump: after a stable release is on nuget.org, release.yml opens the PR setting `ApiBaselineVersion` to it
  - Why: Done by hand for 1.0.0 and 1.1.0 (#97) and easy to forget; until it moves, package validation checks against an older API
  - Status: Done (2026-10-09): release.yml opens an issue (not a PR: one opened with its token gets no CI runs; docs/decisions/)

- Track OpenCV's FFmpeg: upstream.yml notes when opencv-python's Linux wheel bundles a new FFmpeg major (or minor)
  - Why: scenedetect's frames come from it: the trigger for the "Move to FFmpeg 9's libraries" row, and a possible source of colour or 10-bit differences
  - Status: Done (2026-10-10): upstream.yml's opencv job; first run: 5.0.0.93 is the newest and bundles libavcodec 62.28 (FFmpeg 8.1), as ours

- FrameReader API review before its package: names, ref structs, options and errors across VideoFrameReader, AudioReader, SpectralStats, BgrConverter, FrameDecoder and MediaProbe
  - Why: Its first prerelease API should be coherent; it grew a piece at a time (thumbnails, fps, audio, spectral, the BGR converter)
  - Status: Done (2026-10-10): docs/frame-reader-api-review.md; the user's seven changes made

- CI network resilience: retries on every download (BtbN's FFmpeg, ffmpeg.org, apt, PPAs), archives checked before use (a truncated download failed as "not in xz format"), BtbN builds cached by date
  - Why: 2026-10-09 had three CI failures from the network alone (ffmpeg.org DNS, a corrupted BtbN download, Docker Hub's rate limit, which held up the 1.2.0 release)
  - Status: Done (2026-10-10): `.github/actions/btbn-ffmpeg` (download to a file with retries, `xz -t` before unpacking, cached per day, the last cached copy when the download fails) in every workflow; retries on the build script's fetches and apt

- CLI for the audio pieces: `shotdetect waveform -i X -o X.dat` (WaveformData's options) and an audio filter passthrough for the analysis filters (silencedetect, ebur128)
  - Why: WaveformData and AudioFilter need code today; the CLI is how most people would try them
  - Status: Done (2026-10-11): `--waveform` with audiowaveform's options; `--audio-filter` and `--audio-out`

- FrameReader's audio over a Stream: tests that AudioFilter, SpectralStats and WaveformData give a file's results from a headers-first Stream (and after Seek)
  - Why: Only file input is tested for them; AudioReader's Stream path feeds all three
  - Status: Done (2026-10-10): identical on m4a, mp3, mkv, flac, wav; found MP3's end padding kept and Seek throwing on a Stream (fixed)

- A line-ending guard: CI fails when a pull request flips a file's line endings wholesale (or a `.gitattributes` that pins them)
  - Why: #165 turned README.md from CRLF to LF unnoticed (a Git Bash `sed -i`), so blame showed every line changed; #167 restored it. Line endings are per file here, with no `.gitattributes`
  - Status: Done (2026-10-12): ci.yml's `line-endings` job, from `git diff --numstat` with and without `--ignore-cr-at-eol` (lines changed only in their ending; more than 20 in a file fail); it would have stopped #165 and passes #181; the `line-endings` label skips it for a conversion on purpose

- Close release.yml's "Set ApiBaselineVersion" issue automatically: the baseline PR says `Closes #N`, or release.yml closes it once Directory.Build.props has the version
  - Why: #162 stayed open after #163 set 1.7.0
  - Status: Done (2026-10-12): release.yml's `baseline-done` job closes the open issue for the props file's ApiBaselineVersion on every push to main

- Generated images against cv2: FsCheck sizes, crops and pixels for CvResize (INTER_AREA/INTER_LINEAR), Hsv and ContentScorer, compared with cv2 in one Python process per test class, as #168 does for FrameTime
  - Why: Today's pixel tests are fixed fixtures and whole videos; #168's property tests found a PyFloat divergence hand-picked cases missed
  - Status: Done (2026-10-13): PixelTests, 1,800 generated images (1x1 to 720x480; noise, the extremes' palette, gradients, flat) through CvResize, AreaResize, Hsv, gray, EdgeDetector and ContentScorer against cv2/scenedetect answers in Data/pixels.txt (tools/pixel_oracle.py, as oracle.txt: no Python at test time; CI regenerates it on Linux); all equal on the first run

- DASH output (issue #171): stream copy into fMP4 segments and a static MPD through `Remux`
  - Why: The issue's service packages with Shaka only for this; FFmpeg's dash muxer (LGPL) does it
  - Status: Done (2026-10-12): `RemuxOptions.MuxerOptions` (ffmpeg's output options) and the dash muxer in ShotDetector.Native; every file byte for byte `ffmpeg -c copy -f dash`'s (RemuxTests)

- CENC and Widevine for DASH (issue #171, items 2-3): AES-CTR with a caller's key ID and key (FFmpeg's `encryption_scheme cenc-aes-ctr`), a `pssh` box in the init segments and `ContentProtection` in the MPD, a Widevine PSSH builder
  - Why: FFmpeg encrypts but writes no pssh or ContentProtection, so this is ours on top, checked against Shaka's output only
  - Status: Done (2026-10-12): `RemuxOptions.Encryption` (the user chose an option over a PSSH builder only, or docs only); encrypted files byte for byte ffmpeg's, the Widevine pssh box `pssh-box.py`'s (CI), ffmpeg decrypts the result back to the input frame for frame

- `FFmpegLibraries.MissingFilters(graph)` (issue #236): a caller's own graph's filters, parsed without creating them, in Missing's format; `"filter graph"` when it doesn't parse
  - Why: Their startup check kept a hand list of the graph's filters that could drift from the graph
  - Status: Done (2026-10-13)

- Throttled progress (issue #238): `DetectionOptions.ProgressStep`, reports when the fraction crosses a step, plus a first and a final one
  - Why: Every consumer that logs progress writes the same throttle
  - Status: Done (2026-10-13)

- Synthetic test media without the ffmpeg executable (issue #237): lavfi sources (testsrc2, smptebars, sine, ...), concatenation for known cuts, interlaced flags, native encoders (mpeg2video, mpeg4, aac, pcm)
  - Why: Their media tests skip without ffmpeg on PATH though the code needs only the libraries; our CI could use it too. Needs the source filters in our native build
  - Status: Done (2026-10-13): `TestMedia.Write(path, options)`; ShotDetector.Native gets the sources, concat, trim, setfield/setsar/setparams, mpeg2video, mpeg4, more PCM and avi. Moving our own Clips fixture to it: later, separately (the user's choice)

- Exceptions (issue #239): document which exception each method throws now; a shared base for ShotDetectionException and FrameReaderException in a 3.0 (a base-class change is breaking)
  - Why: Two catch blocks where both packages are used; unclear which surfaces from detection
  - Status: Docs done (2026-10-13, #248); the shared base: Maybe (3.0). A non-breaking shape exists (a MediaException : InvalidOperationException in FrameReader under both; only change, catch (InvalidOperationException) would also catch FrameReader's), but the user keeps it for 3.0

- Async overloads for FrameReader's whole-file jobs (issue #243): ConvertAsync, CopyAsync, ReadAsync, FindAsync, ProbeJsonAsync
  - Why: Async workers wrapped each in Task.Run
  - Status: Done (2026-10-13)

- Progress for the whole-file jobs (issue #242): one MediaProgress through each options' Progress
  - Why: Long films left their logs quiet for minutes
  - Status: Done (2026-10-13)

- Remux.CanCopy (issue #241): whether streams can be stream-copied into a container (DASH) and why not, from FFmpeg (avformat_query_codec)
  - Why: They keep ~80 lines of rules from Shaka Packager that drift from FFmpeg
  - Status: Done (2026-10-13, #250)

- ShotDetector.Native meta-package (issue #244): depends on every ShotDetector.Native.<rid>
  - Why: One reference instead of one per platform; a forgotten one only shows at runtime
  - Status: Done (2026-10-13, #251)

- Video.Transcode (issue #240): a one-call transcode with deinterlacing, with whatever H.264 encoder the caller's libraries have; no OpenH264 in our package (Cisco's patent licence covers its binary only as users download it from Cisco)
  - Why: Their last use of the ffmpeg executable: making inputs DASH-ready
  - Status: Done (2026-10-13, #252)

- ShotDetector's Clips fixture from TestMedia instead of the ffmpeg executable
  - Why: Fewer tests needing ffmpeg on PATH; careful: CI's comparisons and the stats tests depend on those clips' bytes, so compare before switching (the user: later, separately)
  - Status: Done (2026-10-14): with SHOTDETECTOR_FFMPEG_LIBS, tests/ClipMaker makes every clip with FrameReader in a process of its own (loaded in the test process, the libraries made the Auto decoder use them in every test, which the mutation job caught), else ffmpeg as before. Needed TestMedia's VideoQuality, Remux's DisplayRotation, MediaProbe's PacketPositions, png/mpeg/image2 in our build, and an encoder fix (frames' own picture types made all-intra video)

- Line endings in the pre-commit hook: the line-endings job's check on the staged files
  - Why: Twice on 2026-10-14 a scripted edit turned a mixed-ending file (the CLI's Program.cs) all CRLF, caught only by CI after a push
  - Status: Done (2026-10-15): the hook runs the same check on the staged files

- Typed time options: `TimeSpan` beside the string ones (`MinSceneLength`, `StartTime`, `EndTime`, `MinOutLength`)
  - Why: From 3.0's API review: scenedetect's strings ("15", "0.6s") are its CLI's; a typed overload reads better in C# and needs no major
  - Status: Done (2026-10-15): `Start`, `End`, `Duration`, `MinSceneDuration`, `MinOutDuration` (and the last two on `DetectorSettings`), the user's names

- Crop corners on `DetectedVideo` beside `CropRegion`
  - Why: From 3.0's API review: `DetectionOptions.Crop` is corners (scenedetect's), `CropRegion` X, Y, width, height; a matching property needs no major
  - Status: Done (2026-10-15): `DetectedVideo.Crop`

- Alpine container pulls that survive registry limits: the musl jobs run Alpine with `docker run` in a step (retries, then Docker Hub or ghcr.io) instead of as the job's `container:`, which the runner pulls once and can't retry
  - Why: `public.ecr.aws` answered "Data limit exceeded" three times in a row and stopped two releases (3.1.0 on 2026-10-15, and one before); the job container is pulled before any step can react
  - Status: Done (2026-10-15): native.yml pulls alpine:3.20 with retries from ECR, mirror.gcr.io or Docker Hub and builds with docker run; the cache key now includes native.yml

- Hardened native build on Linux: FFmpeg's `--toolchain=hardened` (stack protector, FORTIFY_SOURCE, full RELRO, PIE)
  - Why: Makes a bug in an FFmpeg decoder harder to exploit when untrusted media is decoded in-process; about 1-3% speed, to be measured with the weekly benchmark
  - Status: Done (2026-10-15): --toolchain=hardened, the build checks BIND_NOW; speed measured on this branch's benchmark run

- Plain file paths read only files: `protocol_whitelist=file,crypto` for a path that isn't a URL
  - Why: FFmpeg reads prefixes like `concat:` or `subfile:` in an input name; a small behaviour change (scenedetect, through OpenCV, would take them)
  - Status: Done (2026-10-15): paths, and pseudo-protocol strings like concat:, get protocol_whitelist=file,crypto; network URLs (scheme://host) keep theirs

- A frame size cap: FFmpeg's `max_pixels` on the decoder (about 8K x 8K)
  - Why: A crafted file claiming a huge size can't exhaust memory; no real video comes near it
  - Status: Done (2026-10-16): FrameDecoderOptions.MaxPixels (8192 x 8192 by default), and -max_pixels on ShotDetector's ffmpeg decodes

- CodeQL code scanning for the C# (every pull request, weekly)
  - Why: Common vulnerability patterns caught automatically
  - Status: Done (2026-10-15): codeql.yml (build-free C#); its first scan's two alerts (the URL tests' server) fixed in #308

- Parallel detection of long files: split at keyframes, detect the pieces on separate cores with a warm-up overlap (the previous frame, adaptive's window), stitch, prove identical in CI
  - Why: The biggest speed-up left (several times on 8 cores for a film); the hard part is matching every frame's result
  - Status: Done (2026-10-16), opt-in: `DetectionOptions.Parallel` / `--parallel` (docs/parallel-detection.md), 1.5-2.3x on 16 threads, 1.1-1.6x on 4 CPUs; CI compares it with sequential runs on the samples and three films. Next: the default, once CI has compared it for a while. The default: In progress (2026-10-18, docs/decisions/2026-10-18-parallel-default.md). Step 1, CI on every platform: the comparison on ARM (arch job), Windows and macOS (parity-os, in-process with our build), and `--parallel` in the weekly benchmark. Step 2, once ARM and a parity-os nightly have passed: `DetectionOptions.Mode` (Auto/Sequential/Parallel; Auto parallel for Detect, sequential for DetectStreamAsync), `Parallel` obsolete, the CLI's `--sequential`

- An SBOM in the packages (FFmpeg, zlib, dav1d, LAME versions), generated by the release workflow
  - Why: What's inside each package, as security teams ask for it
  - Status: Done (2026-10-16): build-ffmpeg.sh writes sbom.spdx.json (SPDX 2.3, with CPEs), packed at the root of
    each FrameReader.Native package and in the shotdetect archives

- Stream input (Detect(Stream), CLI -i -): a protocol whitelist
  - Why: It passes no protocol_whitelist at all (VideoReader's InputOptions gives a Stream none); FFmpeg's HLS demuxer probably won't take pipe:0, so likely not exploitable (security review 2026-10-16, unverified), but a whitelist like a file's is cheap defence in depth
  - Status: Done (2026-10-17): protocol_whitelist pipe for a Stream, in-process and through the executable (defence in depth: no piped input was found that reaches another protocol)

- A cap on the probe's packet-timestamp lists
  - Why: An MP4 whose sample tables repeat chunk offsets could make the packet-pts lists (VideoReader, MediaProbe) grow to hundreds of millions of entries from a small file (security review, unverified: needs a crafted file to confirm)
  - Status: Maybe (2026-10-17): unverified; such an input would grow FFmpeg's own packet index first (several times larger per packet than our lists), so a cap of ours alone likely wouldn't help. Revisit if a real file shows it

- CI: BtbN's ffmpeg pinned to a dated release and its SHA-256
  - Why: ci.yml and release.yml's test job fetch BtbN's latest build, checked only as a valid archive; the publishing job no longer runs it (#328), so the risk is low, but a pin makes CI reproducible and a tampered build visible
  - Status: Done (2026-10-17): autobuild-2026-09-30-13-08 (month-end releases are kept; daily ones are pruned), FFmpeg 8.1.3 and 9.0.2, each archive's SHA-256 in the action

- Workflows: read-only tokens by default, actions pinned by commit SHA
  - Why: Eight workflows (benchmark, ci, frame-reader-bench, mutation, native, parity-os, realworld, and release's version and native jobs) ran with the repository's default token; and an action referenced by a tag (`@v7`) runs whatever that tag points to when the job starts, so a compromised or moved tag would run in CI and in the release workflow (security review with the advisor, 2026-10-17)
  - Status: Done (2026-10-17): `permissions: contents: read` at the top of those workflows (jobs that need more already say so); every external action pinned to its tag's commit with the tag in a comment, which Dependabot's github-actions updates move together. Dependabot alerts are a repository setting for the owner

- upstream.yml: an issue when BtbN has a newer month-end release with a newer FFmpeg point release
  - Why: The btbn-ffmpeg pin is moved by hand; without a reminder CI's ffmpeg falls behind the 8.1 and 9.0 point releases (their fixes) unnoticed
  - Status: Done (2026-10-17): upstream.yml's btbn job (newest month-end tag, its checksums.sha256 against the action's 8.1/9.0 pin); the issue lists the new tag and the action's table rows; up to date today (8.1.3, 9.0.2)

- Windows named pipe: PipeOptions.CurrentUserOnly
  - Why: Another local user could connect to the frame pipe first and break the run (a denial of service; the name is a GUID, so nothing leaks); CurrentUserOnly closes it (security review)
  - Status: Done (2026-10-17)

- FrameReader.Native's Linux packages: runtimes/<rid>/native/ paths without the double slash
  - Why: Packed on Linux, the libraries were listed as `runtimes/linux-x64/native//libavcodec.so.62` (2.3.0 on nuget.org too); NuGet and the loader cope, but CI's pack check had to accept it
  - Status: Done (2026-10-17): the csproj's `PackagePath="runtimes\$(Rid)\native\"` kept its backslashes on Linux (MSBuild only rewrites what looks like an existing path), so NuGet didn't see a trailing separator and joined the file name with another slash; forward slashes now, and ci.yml's pack check requires exactly one

- Warn when an https HLS playlist decodes through the ffmpeg executable
  - Why: In-process, the segments a playlist names are opened with the certificate checked (FrameReader's io_open wrapper); the executable has no such hook and opens them unchecked. A one-line warning in the result says so when an https playlist goes that way (no FFmpeg libraries, or --decoder process)
  - Status: Done (2026-10-17) in the CLI (a line on stderr) and README; Done (2026-10-18) in the library too: `DetectionResult.Warnings` (new public API, the owner's choice: docs/decisions/2026-10-18-library-warnings.md), which the CLI prints. Not covered: a local playlist whose segments are https, and DetectStreamAsync (no result)

- Check upstream.yml's BtbN job on its first scheduled run (Monday 2026-10-19)
  - Why: #357 wasn't dispatched on its branch (the workflow's other jobs could open real issues), so its first real run is the check: the log should say the pin is up to date, or open one issue
  - Status: Open

- The owner files the x264 AVX-512 report with VideoLAN (x264-avx512-report.md in the main checkout)
  - Why: CI's VideoWriter step needs DOTNET_TieredCompilation=0 because x264's AVX-512 code and .NET's tiered JIT differ about 1 run in 250 (CLAUDE.md); the report was written but not filed as of the 2026-10-15 reminder (it waited on a VideoLAN GitLab account). A public post from the owner's account: theirs to file, never a session's
  - Status: Open

## 5. 2.0 (breaking; picked 2026-10-13)

Nothing here changes detection results: types, names and visibility only. With #194 (section 1). A 2.0 moves the
package validation baseline and empties CompatibilitySuppressions.xml.

- `DetectionResult.Video` as a plain record (source and analysed sizes, rate, crop, compatibility, how it was decoded), `VideoReader` internal; stream data as FrameReader's `MediaInfo`
  - Why: `VideoReader` is a result view with a decoder's name; `VideoInfo` and `MediaInfo` are parallel probes; `VideoReader.Streams`/`HasAudio` run ffprobe from a property getter
  - Status: Done on v2 (2026-10-13): `DetectedVideo` (immutable, wraps the now-internal VideoReader): source Width/Height, ProcessedWidth/Height, FrameRate, FrameCount (OpenCV's), Codec, PixelFormat, FieldOrder, Rotation, Container, CropRegion, Compatibility, Deinterlaced, DecodedInProcess, Streamed, Position(frame); no streams (ShotDetection.Probe or MediaProbe give them)

- One type per concept with FrameReader: `DetectionOptions.Yadif` takes `IRowDeinterlacer` (IYadif goes; FastYuv's `Yadif` implements it, so FastYuv 2.0 too), `ReferenceToneSegment` -> `ToneSegment` (and the reference-tone options as `ReferenceToneOptions`)
  - Why: Identical shapes copied by adapters; FastYuv's yadif would then work with FrameDecoder directly
  - Status: Done on v2 (2026-10-13): `DetectionOptions.Yadif` is an `IRowDeinterlacer` (IYadif and its adapter gone; FastYuv's Yadif implements it); `ReferenceToneSegment` -> FrameReader's `ToneSegment`; `DetectionOptions.ReferenceTone` (FrameReader's `ReferenceToneOptions`, Frequency and MaxGap now reachable) replaces ReferenceToneMinDuration/SearchDuration, same defaults

- `FramePipeline` internal; `DetectionProgress` without `Pipeline`/`DecodesInProcess` (the ActivitySource tags keep the diagnostics)
  - Why: Plumbing of the ffmpeg-pipe design in the public API and in progress reports
  - Status: Done on v2 (2026-10-13): FramePipeline internal; DetectionProgress drops Pipeline but keeps DecodesInProcess (issue #42 asked for the decoder in progress reports, which streaming callers see before any frame; user's choice); the ActivitySource tag keeps the pipeline

- Renames where one name means different things: analysed `Width`/`Height` vs source/displayed ones; OpenCV's `FrameCount` vs the container's; `FfmpegLibrariesNotFound` -> `LibrariesNotFound`; `DetectionOptions.Duration` (a string) vs `VideoInfo.Duration` (a TimeSpan)
  - Why: Mistakes waiting to happen when using both packages (API review, 2026-10-13)
  - Status: Done on v2 (2026-10-13): analysed size ProcessedWidth/Height (with DetectedVideo, #222); OpenCvFrameCount on VideoInfo and DetectedVideo; LibrariesNotFound; DetectionOptions.RangeDuration (the user's names)

## 6. FrameReader as its own product (after 2.1.0)

The plan, with why and what stays: docs/frame-reader-product.md. One repository; its own versions, releases, native packages and CI.

- Versions: FrameReader's own Version and ApiBaselineVersion; ShotDetector depends on it (only a minimum until 3.0, which bounds it below the next major)
  - Why: Lockstep moved each package's number for the other's changes
  - Status: Done (2026-10-14, with the next two rows in one PR): ShotDetectorVersion/FrameReaderVersion and their baselines; ShotDetector's package depends on FrameReader's current version (>=, no upper bound: NuGet's)

- Changelog per package: changes/framereader/ and changes/shotdetector/, src/FrameReader/CHANGELOG.md, changelog.py --package
  - Why: Each product's release notes say only what changed in it
  - Status: Done (2026-10-14)

- Releases per product: framereader-vX.Y.Z tags and GitHub releases next to vX.Y.Z; a baseline issue each
  - Why: Release FrameReader without ShotDetector and the other way round
  - Status: Done (2026-10-14): framereader-v2.1.0 tagged on 2.1.0's release commit, so the new flow starts from there

- FrameReader.Native.<rid> (and the meta-package) as the real native packages; ShotDetector.Native.<rid> as aliases
  - Why: FrameReader users shouldn't need a ShotDetector-named package
  - Status: Done (2026-10-14): src/FrameReader.Native packs both (-p:Alias=true for the aliases, which depend on FrameReader.Native at FrameReader's version or newer); FrameReader.Native first appears with FrameReader's next release, which has to come with or before ShotDetector's

- CI by what a change touches: FrameReader-only parts run FrameReader's tests; shared code still runs the scenedetect comparison
  - Why: FrameReader-only PRs ran the whole parity matrix
  - Status: Done (2026-10-14): tools/reach reads which FrameReader files ShotDetector's detection runs from the built assemblies (14 of 27); tools/ci-changes.sh classifies for all three `changes` jobs. It found the hand-kept list wrong: AudioReader runs for ShotDetector's reference tone

- Docs: FrameReader's README as its package's front page, the root README introducing both; a framereader issue label
  - Why: FrameReader read as a part of ShotDetector
  - Status: Done (2026-10-14): FrameReader's README opens with what it does, install, its own changelog and licence; the root README introduces it; label `framereader`

- ShotDetector 2.2.1: the ShotDetector.Native aliases' minimum to FrameReader.Native 2.2.1, the CLI binaries rebuilt
  - Why: The musl fix (#278) is in FrameReader.Native 2.2.1, but the aliases resolve to 2.2.0 (NuGet takes the lowest allowed) and the CLI's Alpine binary bundles 2.2.0's libraries (it falls back to the executable there)
  - Status: Done (2026-10-14): released as 2.2.1

- release.yml: the tests in a job of their own (read-only permissions), so the publishing job never runs a third-party binary
  - Why: The release job runs `dotnet test` with BtbN's ffmpeg (latest, unpinned) while it holds id-token: write (NuGet trusted publishing) and contents: write; a compromised build could tamper with the packages or take the token (security review, 2026-10-16, docs/decisions/2026-10-16-security-review-fixes.md)
  - Status: Done (2026-10-16): a test job (contents: read) before the release job, which no longer installs BtbN

- Deprecate the ShotDetector.Native 2.x packages on nuget.org, FrameReader.Native as the alternative
  - Why: 3.0 stopped publishing them; deprecation makes NuGet warn whoever still references them. Done on nuget.org by the user: steps in [deprecate-shotdetector-native.md](deprecate-shotdetector-native.md)
  - Status: Done (2026-10-17): all 128 versions of the six packages deprecated (legacy) on nuget.org, each with its FrameReader.Native counterpart as the alternate; checked on the website, version by version

- FrameReader 2.6.1: the Linux packages' library paths without the double slash (#358)
  - Why: FrameReader's only change since 2.6.0; a patch release ships it. Count the commits touching both products at that release (docs/frame-reader-product.md): 0 of 6 at 2.6.0 was the first under the 1-in-20 trigger, and a second in a row would raise the repository split
  - Status: Done (2026-10-17): released by this PR (FrameReader 2.6.1). Split count since framereader-v2.6.0: 0 of 2 code commits touched both products (#358 FrameReader only, #360 ShotDetector only): the second release in a row under 1 in 20, on a small sample; splitting is the owner's call

## 7. 3.0 (picked 2026-10-14)

- #239: `MediaException : InvalidOperationException` in FrameReader, the base of FrameReaderException and ShotDetectionException
  - Why: One catch for both packages
  - Status: Done (2026-10-14, #290)

- Upper bounds: ShotDetector on FrameReader and FastYuv on ShotDetector up to the next major (Directory.Build.targets), FFmpeg.AutoGen below 9
  - Why: NuGet writes only minimums, so a later major could replace the version a package was built against
  - Status: Done (2026-10-14, #290)

- ShotDetector.Native aliases no longer published
  - Why: They only point at FrameReader.Native; the 2.x ones stay on nuget.org
  - Status: Done (2026-10-14, #290)

- 3.0.0-preview.1 (FrameReader 2.3.0-preview.1), then 3.0.0
  - Why: A preview first (the user)
  - Status: Done (2026-10-14): preview.1, then 3.0.0 and FrameReader 2.3.0; an API review found nothing else worth breaking for

## Done

- Streamed input: `Detect(Stream)`, URLs, CLI `-i -`
  - Landed: 0.4.0

- Time range, frame skip, crop; image sizing options
  - Landed: 0.4.0

- Histogram and hash detectors; API review; repo polish
  - Landed: 0.3.0

- DetectStreamAsync, cancellation, progress, FfmpegDirectory, CLI tool + AOT binaries, Alpine, rotation
  - Landed: 0.3.0

- 0.6.4 compatibility mode, VFR, edges (`delta_edges`), exports, FastYuv package, release pipeline
  - Landed: 0.1.0–0.2.0
