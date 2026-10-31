# Backlog: done

Done, Released and Dropped items, moved from [backlog.md](backlog.md) on 2026-10-18; grouped by the section they
were in. Rows finished after that go at the end, under "Since 2026-10-18".

## 1. Robustness on real videos

- Real-world check: whole Blender films and codec clips vs scenedetect (`tools/realworld.py`, nightly)
  - Why: Synthetic clips and trailers can't show divergences on hours of footage, fades, dark scenes, or in HEVC/VP9/AV1 decoding
  - Status: Done (2026-10-06)

- CI: x64 and ARM give the same results end to end: every detector's scene lists and stats on the sample clips, both decoders, written on each and diffed
  - Why: Each piece is tested on ARM (the HSV conversion on all 2^24 colours, the content score, the microbenchmark hash, the in-process tests with the linux-arm64 build), but nothing compares a whole run: decoding goes through a different native build per platform, and scenedetect on ARM isn't the reference (opencv-python's ARM wheels use KleidiCV), so the parity matrix runs on x64 only
  - Status: Done (2026-10-17): ci.yml's arch job (x64 and ARM; our native build in-process and BtbN's ffmpeg 9.0 through the executable; tools/arch-outputs.sh) and arch-diff (tools/arch-summary.py). Its first run found ARM differing (FFmpeg's yuv420p to BGR assembly rounds differently there); the owner chose to document it, with FastYuv as the way to x86's results on ARM (docs/decisions/2026-10-17-arm-results.md): arch-diff requires ARM with FastYuv to equal x64 on the 8-bit 4:2:0 clips and reports the rest

- ARM against x64 on MPEG-4 Part 2 (Xvid/DivX) and MPEG-1: a sample of each in the arch comparison
  - Why: No sample is MPEG-4 Part 2 or MPEG-1 (tools/make-samples.ps1), so whether ARM decodes them as x64 is unknown; MPEG-4 Part 2's default IDCT on x64 equals `-idct simple` but not the C one (#384), so the MPEG-2 fix may not carry over. A new sample changes the samples cache key, so CI regenerates them once
  - Status: Done (2026-10-18): MPEG-1 required since its sample came in. MPEG-4 Part 2: ARM differed on FFmpeg's encoder's clip (765/1253 frames) and matched on Xvid's; the owner chose the C IDCT unless FFmpeg's decoder takes the Xvid IDCT, by its own rule (`Mpeg4Idct`: the user data's signature in the extradata and first packet, else the FourCC), on both paths (docs/decisions/2026-10-18-mpeg4-xvid-idct.md); both samples moved to samples/, required in parity, compare-parallel and arch-diff

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
  - Status: Partly done (2026-10-14): `--quality`/`--compression` (PNG and WebP as OpenCV's, JPEG mapped); the filename templates were already done (2026-10-06, the row above). Left: `--mkvmerge`: Dropped (2026-10-18, the owner)

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
  - Why: The real speed-up for 1080i: yadif costs ~7.6 s of 13.3 s there and the resize reads ~7% of the pixels. Done as `DetectionOptions.Yadif` / FastYuv `Yadif`, rows only (columns come in pairs ~7 px apart, not worth it): 80 s 1080i MXF 7.5 s vs 11.6 s on 2 CPUs, 5.0 s vs 5.5 s on all (2026-10-21: ~8% of the pixels at 1080, but rows only means ~29% of the rows, 308 of 1080, and with FastYuv's converter every row was deinterlaced until #461)
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
  - Status: Dropped (2026-10-18, the owner's backlog review)

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
  - Status: Docs done (2026-10-13, #248); the shared base: Done in 3.0 (2026-10-14, #290; the row under section 7). A non-breaking shape exists (a MediaException : InvalidOperationException in FrameReader under both; only change, catch (InvalidOperationException) would also catch FrameReader's), but the user keeps it for 3.0

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
  - Status: Done (2026-10-18): the default for `Detect` and the CLI (`DetectionOptions.Mode`, `--sequential`), from 8 CPUs on and for frames up to 3840x2160 (docs/decisions/2026-10-18-parallel-auto-threshold.md: 2 chunks were ~15% slower on 4 CPUs, and memory grows per chunk). History: built opt-in (2026-10-16): `DetectionOptions.Parallel` / `--parallel` (docs/parallel-detection.md); CI compares it with sequential runs on the samples and three films. The default (docs/decisions/2026-10-18-parallel-default.md): step 1, CI on every platform: the comparison on ARM (arch job), Windows and macOS (parity-os, in-process with our build), and `--parallel` in the weekly benchmark: done. Step 2, once ARM and a parity-os nightly have passed: `DetectionOptions.Mode` (Auto/Sequential/Parallel; Auto parallel for Detect, sequential for DetectStreamAsync), `Parallel` obsolete, the CLI's `--sequential`: Done (2026-10-18), after the parallel step passed on Windows and macOS (parity-os on main, dispatched) and on ARM

- FastYuv in-process: prefer the default path where it is cheaper on x64
  - Why: In-process on x64, FastYuv isn't needed for exactness (the default path converts only the rows the resize reads with FFmpeg's own swscale), and 4:2:2 through FastYuv costs ~9-12% more CPU than that (4:2:0 ~4%; the release-candidate performance review, 2026-10-18), since `WriteYuv` copies the whole frame's planes first. Its point in-process is ARM, where it gives x86's results. README says so; choosing the path by platform is a design question for the owner
  - Status: Won't do (2026-10-18, the owner: an explicit converter is used as set; the README says FastYuv isn't needed for exactness in-process on x64; docs/decisions/2026-10-18-fastyuv-x64-in-process.md)

- An SBOM in the packages (FFmpeg, zlib, dav1d, LAME versions), generated by the release workflow
  - Why: What's inside each package, as security teams ask for it
  - Status: Done (2026-10-16): build-ffmpeg.sh writes sbom.spdx.json (SPDX 2.3, with CPEs), packed at the root of
    each FrameReader.Native package and in the shotdetect archives

- Stream input (Detect(Stream), CLI -i -): a protocol whitelist
  - Why: It passes no protocol_whitelist at all (VideoReader's InputOptions gives a Stream none); FFmpeg's HLS demuxer probably won't take pipe:0, so likely not exploitable (security review 2026-10-16, unverified), but a whitelist like a file's is cheap defence in depth
  - Status: Done (2026-10-17): protocol_whitelist pipe for a Stream, in-process and through the executable (defence in depth: no piped input was found that reaches another protocol)

- A cap on the probe's packet-timestamp lists
  - Why: An MP4 whose sample tables repeat chunk offsets could make the packet-pts lists (VideoReader, MediaProbe) grow to hundreds of millions of entries from a small file (security review, unverified: needs a crafted file to confirm)
  - Status: Dropped (2026-10-18, the owner's backlog review)

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

- Dependabot: one grouped PR for the GitHub Actions updates
  - Why: Since actions are pinned by SHA (2026-10-17) every action update is its own PR and its own full CI run (six on 2026-10-18, #388-#394); a `groups` entry for the github-actions ecosystem in .github/dependabot.yml makes it one PR a week
  - Status: Done (2026-10-18): two groups, minor and patch together (auto-merged once green, as before) and majors together (wait for a look)

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

## Since 2026-10-18

- In-process default path vs the executable on 10-bit h264 with top-left chroma: different stats
  - Why: Found 2026-10-18 measuring FastYuv's 10-bit speed-up: a 854x480 yuv420p10le h264 clip in mkv encoded with `-x264-params chromaloc=2` gave other stats in-process with the default path than through the ffmpeg executable. Checked against scenedetect 0.7.2 (2026-10-19): the in-process default path was right. The h264 VUI (each decoded frame) says top-left while Matroska's header says left; OpenCV converts with the frame's siting, BgrConverter too, but the executable path's `in_chroma_loc` and FastYuv (both decoders) took the probe's stream entry, which is the container's
  - Status: Done (2026-10-19, #427): VideoReader reads the first frame's chroma location (in-process `FrameDecoder.ChromaLocation`, new in FrameReader; ffprobe's `frame=chroma_location` for one frame on the executable path) and prefers it to the stream's, where it's used (FastYuv, and the executable path for formats other than 8-bit 4:2:0). The sample sintel_10bit_topleft.mkv (tools/make-samples.ps1) puts it in CI's parity runs; all four paths equal scenedetect on it (docs/decisions/2026-10-19-chroma-and-colour-tags-from-the-frame.md)

- FastYuv vs the default path where the container's colour matrix or range contradicts the video stream's
  - Why: Found with the row above (2026-10-19): FastYuv took the colour matrix (`color_space`) and range from the stream entry too, so an 8-bit h264 mkv whose header says bt709 while its VUI says smpte170m differed from scenedetect in every frame's stats with FastYuv through both decoders (191 cells per content column), the default paths not; the same with a header saying full range and a VUI saying limited (`h264_metadata=video_full_range_flag=0`)
  - Status: Done (2026-10-19, #427): the first frame's colour space and range win over the stream's as its chroma location does (`FrameDecoder.ColorSpace`/`ColorRange`); all four paths equal scenedetect on both clips; FastYuvTests' `420-vui-matrix` and `420-vui-range`

- Parallel detection: fewer frame buffers per chunk (8 / chunks instead of 8 each)
  - Why: Each chunk's in-process reader keeps 8 full-frame buffers (VideoReader.InProcess.cs), which is most of the memory growth per chunk (the release-candidate security review, 2026-10-18: a 4096x4096 clip with FastYuv peaked at 525 MB sequential, 836 MB in 2 chunks, 1197 MB in 4). Sharing the 8 between the chunks would keep a parallel run near a sequential one's memory; the 3840x2160 cap on Auto (docs/decisions/2026-10-18-parallel-auto-threshold.md) limits it meanwhile. Since 3.8.0 Auto chunks from 4 CPUs (docs/decisions/2026-10-18-parallel-auto-4cpus.md), so this is the default's cost: +10 to 54 MB peak per 2 chunks on GitHub's 4-CPU runners up to 1080p
  - Status: Done (2026-10-18/19, #423): `ChunkBuffers` = max(2, 8 / chunks), 4 each in 2 chunks and 2 in 3 or 4, so a chunked run holds a sequential run's 8 in all. Measured first on a 4-CPU container (240p to 2160p, content, with and without stats, 8/4/3/2 buffers in 2 and 4 chunks, 3 interleaved rounds): CPU time unchanged, wall time within the machine's noise for every count (even 2 in 2 chunks). Peak RSS in 2 / 4 chunks: 1080p 187 to 175 / 259 to 218 MB, with FastYuv 202 to 178 / 292 to 221 MB, 2160p FastYuv 553 to 458 / 868 to 583 MB (sequential 366); on the default path the buffers hold only the sampled rows, so the decoders are most of what remains (docs/parallel-detection.md, "As built")

- ARM's results with FastYuv for 10-bit, 4:2:2, MPEG-2 and MJPEG video
  - Why: FastYuv gives x86's results on ARM for 8-bit 4:2:0 only (arch-diff's table, 2026-10-17): 10-bit and 4:2:2 take other swscale paths, and MPEG-2's and MJPEG's decoded frames already differ (their IDCTs round per platform), so those still differ on ARM
  - Status: Done (2026-10-18, #412): 10-bit 4:2:0: FastYuv converts yuv420p10le with x86's bytes (`IYuv420P10Converter`, a port of swscale's bicubic filters, MMXEXT vertical scaler and output and the C output for the last two rows; docs/fastyuv-10bit-422.md "As built (10-bit)"), arch-diff requires ARM to equal x64 on both 10-bit samples; on x64 slower than the default path on small video, merged anyway (docs/decisions/2026-10-18-fastyuv-10bit-x64.md; the speed-up is in Next). 4:2:2 done (2026-10-18, #404): yuv422p/yuvj422p (`IYuv422Converter`; rotated frames converted before turning), arch-diff requires them (docs/decisions/2026-10-18-fastyuv-422.md). MPEG-1/2 and MJPEG done (2026-10-17): ShotDetector decodes them with FFmpeg's C IDCT (`-idct simple`; in-process FrameReader's `FrameDecoderOptions.CodecOptions`), which x64's default already gives and ARM's NEON IDCT doesn't (#384's checksums); arch-diff requires them too

- The owner files a StyleBro issue: `--verify-no-changes` on Linux flags untouched files
  - Why: In a Linux cloud container, `dotnet tool run stylebro-migrate format ShotDetector.slnx --verify-no-changes` reports WHITESPACE errors on main as it is (2026-10-18): src/ShotDetector/Detectors.cs(298,115 and 300,68), FrameTime.cs(69-71), VideoReader.cs(946,112 and 948,119), VideoReader.InProcess.cs(17,31 and 19,95), tests/ShotDetector.Tests/Tests.cs(213-214), each "Replace N characters with '\n...'" around comments in expression-bodied members of these CRLF files, while Windows CI's check passes. Every cloud session and the daily triage see them; CLAUDE.md sends StyleBro feedback to its issues, a repository outside this one's sessions
  - Status: Done (2026-10-18, #421): already filed as stylebro#133 and fixed in 0.6.0-alpha.1; ShotDetector moved to 0.7.0-alpha.1

- FastYuv 10-bit: filter each chroma row once per frame
  - Why: On x64, FastYuv for 10-bit 4:2:0 is about 2.5-3.5x slower than the default path at 854x480 (1080p about even; docs/decisions/2026-10-18-fastyuv-10bit-x64.md), because each chroma row is filtered horizontally again for every output row that reads it. A per-frame chroma pass would remove that: a batch method (the needed chroma rows filtered once, then the rows converted), additive as a new interface beside `IYuv420P10Converter` that the pipeline type-tests for, and CvResize's YUV path reworked to call it per frame
  - Status: Done (2026-10-18): `IYuv420P10FrameConverter` (ShotDetector, additive; FastYuv's 10-bit converter implements it, the pipelines prefer it): each needed chroma row filtered once per frame (four samples at a time with pmaddwd on x86), the vertical filter four pairs at a time, saturation without branches; CvResize converts once per frame, turned and cropped too. On 4 shared x64 vCPUs 854x480 10-bit with FastYuv: 3.9 s instead of 5.8 s (executable), 3.8 s instead of 5.1 s (in-process), the default path 3.5 s; 1080p about the default path's time (in-process 10.3 s against 12.1 s wall, 20.9 s against 21.6 s CPU). The same bytes (FastYuvTests against ffmpeg and row by row; the CLI's CSV and stats before and after on four 10-bit clips, both decoders)

- FrameReader: unknown `InputOptions` names fail, as `CodecOptions` and the encoders' do
  - Why: `Demuxer.Open` passes `FrameDecoderOptions.InputOptions` to avformat_open_input and drops what's left, so a misspelt demuxer option is ignored without a word (the release-candidate security review, 2026-10-18), though FrameReader's README says unknown option names are an `ArgumentException`. `CodecOptions` checks its leftovers since 2026-10-18. Changing it could break callers passing options some demuxers lack (ShotDetector passes the image sequences' `framerate`), so it needs a look per caller
  - Status: Done (2026-10-19, #428): after avformat_open_input, the first name left over is an `ArgumentException` naming it and the input's format (FrameReader's own defaults, `scan_all_pmts`, `tls_verify`, `ca_file` and the TLS aliases, never count; docs/decisions/2026-10-19-unknown-input-options-throw.md). ShotDetector's options were checked in-process (file, image sequence's `framerate`, Stream, http, https, HLS) and are all taken: no ShotDetector change. Correction (2026-10-19): a file path with a `%` that isn't an image sequence (`My%20Video.mp4`) also got `framerate`, which mp4 doesn't take, so 3.10.0 refused it; fixed in 3.10.1 (only image2's patterns get it)

- Warnings for streaming callers (DetectStreamAsync)
  - Why: DetectStreamAsync yields shots and has no result object, so it can't carry DetectionResult.Warnings; a streaming caller decoding an https playlist through the executable gets no warning
  - Status: Done (2026-10-19, #429): `DetectionOptions.Warnings`, an `IProgress<string>` told each warning as soon as it is known (the HLS notice and why Parallel read sequentially before any shot, a chunk fallback before the sequential read), for Detect and DetectStreamAsync alike; DetectionResult.Warnings holds the same sentences, from the same place (docs/decisions/2026-10-19-warnings-as-they-happen.md)

- Warn for a local playlist whose segments are https
  - Why: DetectionResult.Warnings (2026-10-18) covers an https playlist decoded by the ffmpeg executable; a local .m3u8 naming https segments takes the same unchecked path, but the decision left it undecided (docs/decisions/2026-10-18-library-warnings.md)
  - Status: Done (2026-10-19, #430): a local playlist can't fetch https through the executable (it opens with `-protocol_whitelist file,crypto`; ffprobe 8.1: "Protocol 'https' not on whitelist 'file,crypto'!"); the real gap was an http playlist, whose https segments, keys and variants the executable fetches unchecked: it now warns too (docs/decisions/2026-10-19-hls-warning-http-playlists.md)

- Check upstream.yml's BtbN job on its first scheduled run (Monday 2026-10-19)
  - Why: #357 wasn't dispatched on its branch (the workflow's other jobs could open real issues), so its first real run is the check: the log should say the pin is up to date, or open one issue
  - Status: Done (2026-10-19: the scheduled run started at 11:00 UTC, about 5.5 h after its 05:27 cron; its btbn job logged "The action pins autobuild-2026-09-30-13-08 (FFmpeg 8.1.3 and 9.0.2); BtbN's newest month-end release, autobuild-2026-09-30-13-08, has 8.1.3 and 9.0.2. Up to date.", run https://github.com/bisforboman/shotdetector/actions/runs/39241460086)

- Parallel detection: hand the outer reader's packet list to the chunks' readers
  - Why: Each chunk's `new VideoReader` (ShotDetection.ScoreChunked) probes the file and reads every packet's pts again, N times at once: about 0.1 s on mp4, growing with the file on MPEG-TS (the release-candidate performance review, 2026-10-18). The outer reader already has the list
  - Status: Done (2026-10-19, #431): a chunk's reader takes the outer reader's probe (`VideoReader.ProbeResult`: the stream entries with the first frame's tags, every frame's pts, the Xvid IDCT) instead of probing again; on a 4-minute, 280 MB MPEG-TS on this 4-CPU container 6.7 s to 5.8 s wall and 18-21 s to 16-17 s CPU in 2 and 4 chunks, an mp4 within noise

- The executable path's raw-YUV pipe (FastYuv) converted the samples after a colour tag change
  - Why: Found investigating the per-frame-tags Maybe row: ffmpeg sets the pipe's output up with the first frame's colour matrix and range and, when a later frame's differ (two h264 streams joined), converts its samples to them through RGB; FastYuv then read other samples than the decoder's, and cuts moved (a cut missing on a 10-bit matrix change, one 2 frames off on an 8-bit one)
  - Status: Done (2026-10-19, #432): the pipe labels every frame with the first frame's tags (`setparams` after showinfo), so the samples pass through untouched; process + FastYuv now gives in-process FastYuv's results on every built clip (docs/decisions/2026-10-19-per-frame-tags.md)

- Review what FrameReader 2.8.0 and ShotDetector 3.10.0 brought (#422-#432): security, performance and docs, as the release-candidate review before 3.7.0 did
  - Why: Six code PRs across both products landed in one day (the frame's colour tags in FrameDecoder, the InputOptions check, streaming warnings, the http-playlist warning, shared chunk probes, the raw-YUV labels); the last such review found what #407 fixed. Its findings become rows here
  - Status: Done (2026-10-19, #439, ShotDetector 3.10.1): a regression, a file name with a `%` that isn't an image sequence's number (`My%20Video.mp4`) got `framerate`, which #428 then refused (now only image2's patterns, `VideoReader.IsImageSequencePattern`); Auto fell back to the executable for an http(s) URL the libraries refused, which fetches https segments unchecked (now refused, owner's choice: docs/decisions/2026-10-19-auto-no-fallback-for-https.md; DASH over http(s) with FrameReader.Native now needs `--decoder process`); the HLS warning checked again after the frames (Auto's no-frames fallback can't reach a streamed playlist today: its frame count is unknown) and worded "any https segments, keys and variants"; the raw-YUV labels tested and mutation-checked; a comment saying 10-bit where every format but 8-bit 4:2:0 is meant. Two Maybe rows: the first frame's tags read lazily, the CLI printing warnings live

- FrameReader: an rtsp input through the InputOptions check, tested
  - Why: Since 2.8.0 (#428) an `InputOptions` name nothing takes is an `ArgumentException`. ShotDetector passes rtsp URLs only `protocol_whitelist` (VideoReader.InputOptions), and a caller may pass rtsp's own options (`rtsp_transport`, `stimeout`); #428 tested file, image sequence, Stream, http, https and HLS but no rtsp input, as no test serves one. A local rtsp server (ffmpeg `-rtsp_flags listen`, or a minimal one in the tests) would show the whitelist and an rtsp option still open, and a misspelt one throw
  - Status: Done (2026-10-20, #443): FrameDecoderTests.RtspInputOptionsAreChecked: the reader listens (`rtsp_flags=listen`, `listen_timeout`) while the Libs folder's ffmpeg publishes a clip to it over tcp; ShotDetector's whitelist with `rtsp_transport=tcp` reads all 50 frames, a misspelt `rtsp_transprot` is an `ArgumentException` naming it. No bug in the check. Found on the way: FFmpeg 8.1's rtsp listen mode fails without `listen_timeout` (its default -1 s reaches tcp as -1000 ms, out of range), on the command line too

- The CLI prints warnings as they happen
  - Why: The review of 3.10.0 (2026-10-19): `shotdetect` prints `DetectionResult.Warnings` on stderr after detection; `DetectionOptions.Warnings` (since 3.10.0) reports each as soon as it is known, so a long run or a live input would tell why it reads sequentially (or that the executable fetches https segments unchecked) at the start instead of the end
  - Status: Done (2026-10-20, #444): the CLI sets `DetectionOptions.Warnings` and prints each one on stderr when the library reports it (`Warning: ` as before, a terminal's progress line cleared first), no longer the result's list after the summary line; every report is on the detection thread (the chunk fallback comes back after the chunks), so one Write per warning is enough. CliTests runs the built `shotdetect` (`--parallel` on a short clip: the warning once, before the summary line)

- README: an install section for the release binaries (download, unpack, run, per OS), and what still needs ffmpeg
  - Why: The README's Install shows only `dotnet tool install`, which needs the .NET 10 SDK; the release binaries need nothing installed (checked 2026-10-20: 3.10.1's linux-x64 binary detected a clip with no ffmpeg and no .NET on PATH, linking only libc and libm) but the README only says they exist. Also say what still needs ffmpeg/ffprobe: the image and clip exports, URLs whose protocol the libraries lack, DASH (`--decoder process`)
  - Status: Done (2026-10-20, #446): README's Install has "The release binaries": the latest release's archives per platform, unpacking and running on Linux, Alpine, macOS (`xattr -dr com.apple.quarantine` for a browser download) and Windows, nothing to install, and what still needs ffmpeg/ffprobe (image and clip exports, URLs whose protocol the libraries lack, DASH with `--decoder process`); the tool line says it needs the .NET 10 SDK

- CLI: `--version`
  - Why: `shotdetect --version` prints "Unknown option"; users and bug reports need the version
  - Status: Done (2026-10-20, #446): `--version` prints the informational version without the "+commit" (release.yml's `-p:ShotDetectorVersion`); CliTests and ci.yml's aot job check it

- Release binaries for linux-arm64
  - Why: release.yml's `binaries` matrix builds linux-x64, linux-musl-x64, win-x64 and osx-arm64, though FrameReader.Native already ships linux-arm64 and CI builds an ARM AOT binary; ARM Linux users (Graviton, Raspberry Pi 5) need the .NET tool today
  - Status: Done (2026-10-20, #446): release.yml's `binaries` matrix has linux-arm64 on ubuntu-26.04-arm (native.yml already built native-linux-arm64); ci.yml's aot matrix builds it the same way on every full PR, with our linux-arm64 FFmpeg build next to the binary, and checks that it detects cuts decoding in-process (file, https, standard input). release.yml's entry first runs at the next release

- CI: the AOT binaries decode a clip with their bundled FFmpeg on Alpine, Windows and macOS too
  - Why: ci.yml's `aot` job runs the published binary on a clip (file, https URL, standard input) with our FFmpeg next to it on linux-x64 and linux-arm64 only; on linux-musl-x64 it runs without the musl libraries, and on win-x64 and osx-arm64 it only checks that the binary starts. The README says every release binary needs nothing installed (#446; checked by hand on linux-x64 only, and 3.11.0's musl archive does carry the libraries), so the other three should prove it the same way
  - Status: Done (2026-10-20, #451): every entry of ci.yml's `aot` job now publishes as the release does (with a version), builds the release's archive (tools/archive-binary.sh, shared with release.yml) and runs tools/check-binary.sh on it: unpacked into an empty folder, `--version` and a clip from the new `clip` job (a file, `--probe-json`, standard input) decoded in-process with no ffmpeg or ffprobe on PATH; the `native-aot` job gives it our FFmpeg build for linux-musl-x64, win-x64 and osx-arm64. The https check stays on linux-x64 and linux-arm64

- Release: run each binary from its archive before uploading it
  - Why: release.yml's `binaries` job publishes, archives, attests and uploads without running the binary; unpacking the archive into an empty folder and running `--version` and one small clip (with no ffmpeg on PATH) would catch a broken archive, a missing library or a wrong rpath before users download it. The linux-arm64 entry first ran at 3.11.0
  - Status: Done (2026-10-20, #451): release.yml's `binaries` job runs tools/check-binary.sh on each archive after Archive and before Attest and Upload, on the clips of its own `clip` job (BtbN's ffmpeg on Linux); ci.yml's `aot` job runs the same script on the same archive on every full PR. First runs in release.yml at the next release

- FrameReader: the audio reader discards the streams it doesn't use
  - Why: Measured for #452: `ffmpeg -vn` reads 13% of a 52 Mb/s MXF to convert its audio, `Audio.ConvertAsync` all of it (AudioReader sets no `AVDISCARD_ALL` on the video stream). Discard shouldn't change the decoded audio; check the output byte for byte against before and against ffmpeg's. Nothing to gain on MPEG-TS, where every packet is parsed
  - Status: Done (2026-10-21, #454): AudioReader sets `AVDISCARD_ALL` on every stream but its audio stream, and lifts it around its seek (ffmpeg seeks before it discards; the seek takes its reference stream, video, for its index or cues, only among streams not discarded: without that, seeks in Matroska, MP4 and MPEG-TS landed elsewhere). Same samples, chunk times and positions with and without, and ffmpeg's, in mp4, mkv, MPEG-TS (one wrapping at 2^33), MXF with PCM, AVI and two-audio-stream files, from the start and after seeks (AudioDiscardTests), and the same Audio.Convert files before and after on the files below. Reads (rchar) of a generated 60 s 1080i MXF (mpeg2video 4:2:2 50 Mb/s + PCM s24le, 394 MB): 394 MB before, 55 MB after (ffmpeg `-vn`: 54 MB); its MOV remux 393 to 51 MB; its Matroska remux and a 139 MB MPEG-TS unchanged (every byte parsed). Wall time from the page cache didn't change measurably (0.3 s); the gain is in the bytes, i.e. on slow disks and network inputs

- Parallel detection: find why 2 chunks read the file about 4 times, not about 2
  - Why: Measured for #452 (2026-10-21, 4 CPUs, synthetic 1080i MXF 2.4 GB and MPEG-TS 1.6 GB, `rchar`): sequential in-process detection read a file 2.0 times (the packet-pts pre-scan, then decoding), parallel with 2 chunks 4.0 times, and parallel was no faster on the TS
  - Status: Done (2026-10-21, no code change): #452's measurements ran a build of fcc279f (#429), from before #431, where each chunk's reader still scanned the file's packets itself: those were the two extra reads. On files made the same way (old build and main side by side) main reads them 2.0 times in 2 chunks as well (strace: each chunk reads its half and a little more; a 6-minute 2.36 GB MXF 9.49 to 4.75 GB, 45 s either way against 59 s sequential; a 12-minute 1.57 GB TS 6.32 to 3.18 GB, 61 to 60 s against 80 s sequential), and `ParallelTests.ChunksTakeTheOuterReadersProbe` with its mutation guards it. The TS "no faster" was a fallback in that run (`chunk 1: frame 9000 at 359.968 s, expected 359.928 s`: the chunk boundary at the join of a looped 18000-frame file), which then read the file again from the start; TS files looped with `-stream_loop`, copied or re-encoded, didn't fall back on either build, so that file's cause is unknown. Parallel and sequential gave identical scene lists and stats on every file tried

- FrameReader: rtsp listen mode needs `listen_timeout` with FFmpeg 8.1
  - Why: Found by #443's test: with `rtsp_flags=listen` and no `listen_timeout`, FFmpeg 8.1's default of -1 s reaches tcp as -1000 ms, out of range ("Numerical result out of range"), on the plain command line too. Say so in FrameReader's README (InputOptions), or pass a default when `rtsp_flags` asks to listen; check whether a later FFmpeg 8.x fixes it first
  - Status: Done (2026-10-21, #456): not fixed upstream (FFmpeg 8.1.3, release/8.1 and master): rtspdec.c still builds tcp's URL with `listen_timeout=<seconds * 1000>`, so -1 s becomes -1000 ms, and since FFmpeg 8.1 tcp.c reads its URL options through `ff_parse_opts_from_query_string`, which checks them against the option's range (-1 to INT_MAX); 8.0 and earlier read it with `strtol`, which took any value, and a negative or zero timeout waits without end in `ff_poll_interrupt`. FrameReader's `Demuxer.Open` passes `listen_timeout=0` (the same wait without end, which tcp accepts; checked with the command line and FrameDecoder) when `rtsp_flags` ends up with listen and the caller set none; a caller's own value wins. That case only ever failed, so nothing working changes. README's "What it handles" says so

- macOS: sign and notarize the osx-arm64 binary, then perhaps a Homebrew tap
  - Why: release.yml doesn't codesign or notarize it, so a copy downloaded in a browser is quarantined and Gatekeeper blocks it until the user clears it (`xattr -d com.apple.quarantine`); notarizing needs the owner's Apple Developer account (secrets in the `release` environment). A Homebrew tap (formula pointing at the release asset) would make `brew install` the macOS route
  - Status: Won't do (2026-10-21): the owner has no Apple Developer account and doesn't plan one (docs/decisions/2026-10-21-no-macos-notarization.md); README's "The release binaries" explains the quarantine and clearing it with `xattr -dr com.apple.quarantine`

- FastYuv's row yadif with FastYuv's converter: compute and copy only the rows that are read
  - Why: Profiled 2026-10-21 on main 77469b9 (4 vCPU, 150 s 1080i25 MPEG-2): `Yadif = new Yadif()` with `Yuv420Converter = new SwscaleYuv420()`, the pairing the FastYuv package offers, is slower than FFmpeg's whole-frame yadif (MXF 23.5 s against 24.0 s, and against 16.6 s for row yadif with the default converter; TS 16.0 against 13.1 s): `FrameWriter.WriteYuv` takes whole planes (`GetPlane` -> `EnsureRows(0, height)`), so every row is deinterlaced in managed code (26 s CPU) and copied. Compute and copy only the rows the resize reads plus the chroma rows the converter's filter taps read (from `SwscaleYuv420`'s taps, not a guessed margin), same buffer layout so `ResizeYuv` doesn't change; also drops the full-plane copy on 10-bit FastYuv (2.9 s CPU). Needs a public FrameReader way to compute selected rows, so a FrameReader release too. Results must stay byte-identical (the profile's configurations all matched)
  - Status: Done (2026-10-21, #461): FrameReader's `FrameDecoder.CopyRows` copies only the rows asked for, and with a row deinterlacer computes only those; ShotDetector's in-process YUV pipelines pass the luma rows the resize converts (turned: the rows under its pixels) and the chroma rows the converter says those read (`IYuvChromaRows`: 8-bit its own row's or y / 2, 10-bit the vertical filter's taps), whole planes for a converter that doesn't say. Unit test: noise in every other row changes nothing (each layout, turn, crop, size). 150 s, 4 vCPUs, min of 3 interleaved, adaptive with stats: MXF 4:2:2 24.1 to 17.2 s sequential (CPU 61.9 to 43.7 s), 20.3 to 13.1 s parallel; TS 4:2:0 16.0 to 11.8 s, 12.6 to 9.3 s; 10-bit h264 progressive 21.2 to 20.4 s, 20.8 to 19.0 s; scene and stats CSVs identical in every configuration. Row yadif with FastYuv is now about as fast as with the default converter (MXF 16.4 s)

- More decoder threads for frame-threaded decoders (h264, HEVC)
  - Why: Profiled 2026-10-21: with h264 the four frame threads are 62-68% busy and frames in flight limit throughput; 8 decoder threads on 4 vCPUs gave 1.19-1.22x sequential and 1.10-1.21x parallel on 1080p h264 8- and 10-bit, same CPU time, +30-50 MB, byte-identical output. MPEG-2 (slice threads only) gains nothing. Rule in `DefaultDecodeThreads`/`ChunkThreads`: about 2x the CPUs when the codec has frame threading, which ShotDetector can't see today (a FrameReader property, e.g. from `AV_CODEC_CAP_FRAME_THREADS`, so a FrameReader release); check the memory at 4K against the parallel cap
  - Status: Done (2026-10-21, #461) for chunked runs: `FrameDecoder.HasFrameThreads(codec)` (the decoder's AV_CODEC_CAP_FRAME_THREADS, known before opening; h264/HEVC yes, MPEG-2 and dav1d no); `ChunkedDecodeThreads` shares out twice the CPUs (at most 16) for such decoders with frames up to 1920 x 1080, so each chunk gets up to OpenCV's count for one decoder. 1080p h264 parallel: 8-bit 16.8 to 15.1 s, 10-bit 23.8 to 19.5 s, same CPU, +30 MB, identical. At 4K it was 1.09x for +125 MB (398 to 523 MB), so frames over 1080p keep the CPUs' count. A sequential run keeps OpenCV's count, per docs/decisions/2026-10-11-decoder-threads-like-opencv.md (damaged files): measured, 4 and 8 threads gave the same results on samples/broken's base.mp4, truncated.mp4, dup_pts.mkv and res_change.ts, while truncated.ts and corrupt.mp4/ts differed from run to run at either count; doubling sequential threads (1.19-1.22x on 1080p h264): the owner kept OpenCV's count (the next row, docs/decisions/2026-10-21-sequential-decoder-threads.md)

- More decoder threads in a sequential run for frame-threaded decoders
  - Why: 8 threads on 4 vCPUs gave 1.19-1.22x on 1080p h264 sequential (2026-10-21 profile), same CPU, identical output on clean files; chunked runs got it in #461. Against docs/decisions/2026-10-11-decoder-threads-like-opencv.md (OpenCV's count, for damaged files' concealment): measured 4 vs 8 threads on samples/broken, the clean and truncated mp4, dup_pts and res_change gave identical results, truncated.ts and corrupt.* varied run to run at either count. The owner's call
  - Status: Won't do (2026-10-21): the owner kept OpenCV's count (docs/decisions/2026-10-21-sequential-decoder-threads.md): damaged files keep concealing like scenedetect (docs/decisions/2026-10-11-decoder-threads-like-opencv.md), and parallel runs already get the gain from #461

- Parity CI for FastYuv's row yadif: compare `Yadif` + FastYuv's converter against scenedetect on the interlaced samples
  - Why: Neither the CLI's `--fast-yuv` (it sets only `Yuv420Converter`) nor tools/compare.py nor ci.yml sets `DetectionOptions.Yadif`, so the parity matrix's fast-yuv rows deinterlace interlaced samples with FFmpeg's whole-frame yadif. The pairing #461 rewired (row yadif with FastYuv's converter, taking only the rows read through `CopyRows`) is checked against scenedetect only through unit tests (`RowWiseYadifEqualsFfmpegs`, the poison test). Have the dev-only `WithFastYuv` CLI's `--fast-yuv` also set `Yadif` (or a hidden flag), and the fast-yuv parity rows on samples/broadcast run it with `--deinterlace auto` against the lossless yadif copy, as the default path's rows do. Small
  - Status: Done (2026-10-22, #468): the dev-only CLI's `--fast-yuv` now sets FastYuv's `Yadif` with its converter (the pairing the package's README shows; unused without in-process deinterlacing), so ci.yml's fast-yuv in-process row on samples/broadcast compares row yadif + FastYuv's converter, `--deinterlace auto`, against scenedetect on the lossless yadif copy. Exact locally on an XDCAM HD 1080i25 MXF and 20 s of a 1080i25 MPEG-2 4:2:2 MXF, every detector with stats, in-process and through the executable; a Yadif that throws made the in-process run fail, so the rows are its

- Parallel detection on a long 1080i MPEG-TS fell back to reading sequentially: reproduce and fix if ours
  - Why: In #452's measurement (2026-10-21) a 12-minute 1080i25 TFF MPEG-TS (mpeg2video 4:2:0 18 Mb/s + MP2, six lavfi scenes with temporal noise, woven from 1080p50) made the parallel run fall back: `chunk 1: frame 9000 at 359.968 s, expected 359.928 s`. A fallback reads the file about 3 times and was no faster than sequential. #455's agent rebuilt TS files by `-stream_loop` copy and re-encode and didn't hit it, so the original construction matters: write the generator as a script under tools/ (the scratchpad one is gone with the container), reproduce, find whether the chunk's seek or the expected time is off (TS timestamps, the 2-frame early seek, field order), and fix it if it is ours; if the file's own timestamps are at fault, say so and keep the fallback
  - Status: Done (2026-10-22): the original was six copies of a 2-minute TS joined by ffmpeg's concat demuxer with `-c copy`, each copy 0.024 s short of the video (the audio's length, 119.976 s): each copy's first frame, a keyframe, came 0.016 s after the previous copy's last frame. `tools/make-interlaced-ts.sh` rebuilds it (default 12 min; scene length, copies and join adjustable) and gave the exact message on main. Cause, ours: the second chunk's seek for frame 8999 landed on frame 9000's keyframe, and `SeekLandedLate` allowed half a frame (0.02 s), so 9000 passed for 8999 and every later label was one off; it now also counts a landing nearer the next frame than the wanted one as late (read again from further back). Joins 0.05 s apart (the concat demuxer's own choice with FFmpeg 8.1), `-stream_loop` and a single encode have no such join, hence #455's misses. Neither interlacing nor audio matters (a 2 s 320x240 progressive copy joined 0.024 s short does the same). A second cause turned up with the script at 30 s copies: deinterlacing in-process, a seek that lands on the wanted frame itself (a keyframe) left yadif without its previous frame, so the frame differed from the sequential read's (an overlap-score fallback; with a time range, other pixels); such a landing reads again from further back too. The script's default 12 minutes, 4 vCPUs, `--deinterlace auto`: parallel 2:08 (fallback, the exact message) to 1:30, sequential 2:05, scene and stats CSVs identical in all three. Tests: RangeTests' join and interlaced-keyframe seeks, ParallelTests' chunks meeting at a join

- Repeated runs on damaged files (samples/broken: truncated.ts, corrupt.mp4, corrupt.ts) give different scene or stats results at the same thread count
  - Why: Find whether the decoder's frame or slice threads make concealment non-deterministic, whether scenedetect/OpenCV varies the same way across runs, and what "identical" should mean for damaged input. Measured 2026-10-21 (#461, 4 vCPUs, in-process, content with stats, 3 sequential runs each): truncated.ts gave 3 different results at 4 threads (on main too) and 2 at 8; corrupt.mp4 and corrupt.ts 3 different results at both 4 and 8; base.mp4, truncated.mp4, dup_pts.mkv and res_change.ts the same in every run at 4 and 8. CI's exact set includes truncated.ts
  - Status: Done (2026-10-22, #470): best effort, documented (the owner, docs/decisions/2026-10-22-damaged-files-best-effort.md). Measured on a stand-in clip (640x360 29.97 fps Sintel trailer; the real one couldn't be downloaded), FFmpeg 8.1, 4 vCPUs: at one decoder thread our results are deterministic and identical in-process and through the executable; at 4/8 threads corrupt.* give a different stats CSV almost every run (87-174 rows) and about 1 run in 10 adds or moves a cut, truncated.ts varies in its last frame or two, truncated.mp4/base.mp4 never. The cause is libavcodec's threaded concealment (`ffmpeg -threads N -f framemd5` varies the same way, never at 1); scenedetect varies as much (15-20 distinct stats files in 20 runs on corrupt.*; median rows differing 110 against itself, 111 ours against ours, 110 between the two), and OpenCV numbers frames without gaps at one thread, so one thread on both sides wouldn't match. README, the 2026-10-11 decision (a note) and ci.yml's comment corrected; CI unchanged (if truncated.ts ever flakes there, make it informational). The duplicate-pts frame on corrupt.ts is a Maybe row

- Decoding apart from conversion and deinterlacing (another thread)
  - Why: Profiled 2026-10-21: one producer thread decodes, deinterlaces and converts in series; on MPEG-2 it is ~80% busy with ~35% of its time serial work while the slice threads idle, and the scoring thread is 85-90% idle. Handing reference-counted frames (`TakeFrame`) to a conversion thread, with row yadif's previous/current/next frames kept alive, should take sequential 1080i MXF from 16.6 s to about 11-12 s (decode-only floor ~10.5 s) and help 10-bit h264, where swscale is 88% of the producer. docs/frame-references.md describes K converter workers, which ShotDetector doesn't use yet, and names the obstacle (row-wise deinterlacing stays on the decoding thread). Medium; a decision record on the design first (docs/decisions/2026-10-22-decode-split.md)
  - Status: Done (2026-10-22, #471 and #472): C built (`ConvertThreads`, hidden `--convert-threads N`): one conversion thread taking refcounted frames where the decoder isn't in row-yadif mode, byte-identical. Measured 2026-10-22 on 4 vCPUs (min of 3, CLI with stats, FFmpeg's yadif for interlaced): sequential 1080i MXF 4:2:2 36.2 -> 33.1 s (1.09x), 1080i TS 4:2:0 27.4 -> 23.6 s (1.16x), 1080p h264 20.3 -> 18.5 s (1.10x), 1080p 10-bit h264 30.3 -> 25.2 s (1.20x); parallel 1.00-1.07x; 360p 1.00-1.03x; 2 CPUs (taskset) 1.06-1.13x on HD; CPU time the same, RSS 1-23 MB more. The owner (2026-10-22): on by default for sequential in-process runs of HD (at least 1280 x 720's pixels; chunks, smaller video, row yadif and the executable path convert on the decoding thread), and B parked (Maybe row "Option B"; docs/decisions/2026-10-22-decode-split.md)

- The ffmpeg executable's seek with deinterlacing: land before the wanted frame
  - Why: Found 2026-10-22 (the TS chunk fallback): on interlaced MPEG-TS with `--deinterlace`, a seek for a keyframe's frame (a time range's start) lands on that frame itself, so yadif has no previous frame for it and its pixels differ from a full read's (RangeTests' interlaced-keyframe test fails with `VideoDecoder.FfmpegProcess`). The in-process path reads again from further back since then; the executable's select filter drops the frames before the wanted one unseen, so it would need another sign (a showinfo before the select, or always seeking a GOP further back with yadif). Only the executable decoder with deinterlacing and a start time or seek
  - Status: Done (2026-10-23, #477): the executable's select keeps the frame before the wanted one when deinterlacing after a seek (`select=gte(t,...)` from halfway between the two frames before, after yadif, one frame more for `-frames:v`); that frame is dropped, and when it doesn't come first the seek landed on the wanted frame (or after it) and the read starts again from further back, as in-process. A showinfo before the select would have broken the one timestamp line per frame, and a fixed further seek isn't exact (a TS seek lands on the next keyframe after the time). Tests: RangeTests' interlaced-keyframe seek for MPEG-TS and mp4, both decoders. Parallel chunks run in-process only, so they were never affected

- Interlaced video with a time range against scenedetect
  - Why: #469 (in-process) and #477 (ffmpeg executable) fixed seeks while deinterlacing, but both were checked only as our full read against our ranged read (RangeTests), never against scenedetect; CI's time-range step used progressive samples only, and the interlaced broadcast step compared whole files
  - Status: Done (2026-10-23, #480): ci.yml's interlaced broadcast step also compares `--deinterlace auto -s 12 -e 120` (a keyframe, -g 12) and `-s 30 -e 150` (mid-GOP) with scenedetect's `time -s/-e` on the lossless yadif copy, all four detectors with stats, exact, on every path and decoder: the XDCAM MXF (an index: the seek lands before the wanted frame) and an MPEG-TS remux of it (the seek lands on the wanted keyframe). The TS starts at 1.44 s and its yadif copy at 0, but both tools number frames from the first decoded one, so the copy needs no -copyts (checked: a -copyts copy gives the same result). Locally (FFmpeg 8.1, scenedetect 0.7.2) all identical. Not a guard for those two fixes by itself: the code before #469 also matched on stats and cuts (its first frame's pixels differed, measured at frames 12, 24, 36 and 60 of the TS, but too little to move a frame's means, even with downscale 1); RangeTests' interlaced-keyframe test stays the byte-level guard

- The first frame's colour tags read only when they'll be used
  - Why: The review of 3.10.0 (2026-10-19): ShotDetector reads the first frame's chroma location and colour tags once per detection (`FrameTags`: in-process FrameDecoder, else a one-frame ffprobe) also where only a fallback would use them (in-process without FastYuv, where BgrConverter converts each frame from its own tags), about 0.14 s on 4K 10-bit, and on a live rtsp URL it waits for the first frame before detection starts. Reading them lazily, when the executable path or FastYuv first needs them, would save both
  - Status: Done (2026-10-23, #482): read on first use (`VideoReader`'s `Colours`, the tags' read shared with a detection's chunks through the probe they take), by the executable's scale filter (`in_chroma_loc`) or FastYuv's converter and raw-YUV labels; the in-process default path never reads them. The condition for whose tags apply (the first frame's or the stream's) is unchanged, so every argument and conversion is the same (the per-format tests on both decoders and FastYuv's); `InProcessTests.ReadsTheFirstFramesTagsOnlyWhereTheyreUsed` and two mutation entries guard the laziness and the sharing. An http(s) URL decoded in-process no longer opens a second connection for the first frame. An rtsp URL goes to the executable (the libraries lack rtsp), which still reads them before ffmpeg starts where it needs them (formats other than 8-bit 4:2:0, or FastYuv): that wait stays

- An end-to-end test of FastYuv's row yadif on a rotated interlaced clip
  - Why: #461 left it unproven beyond the poison test's geometry (`FastYuvTests.RowsOutsideTheRowsReadChangeNothing` covers each turn with crops): a rotated (90/270) interlaced clip, row yadif with FastYuv's converter and with the default one, against FFmpeg's yadif + transpose, scene and stats byte for byte
  - Status: Done (2026-10-23, #482): `InProcessTests.RowWiseYadifEqualsFfmpegs` runs clips with a display matrix of 90 and -90 degrees (8-bit 4:2:0 and 4:2:2, 10-bit 4:2:0, 1080i 4:2:0 cropped), row yadif with the default converter and with FastYuv's (its YUV pipeline asserted), against FFmpeg's yadif in-process and the executable's yadif and transpose (above 8 bits, FFmpeg's C yadif as a lossless copy turned the same way): per-frame stats and scenes byte for byte. No difference found

- Comparisons that differ in CI as check-run annotations
  - Why: Job logs and step summaries can't be read from cloud sessions (the proxy blocks GitHub's log storage), and tools/compare.py and tools/realworld.py wrote their results only there, so a failed parity or real-world job said no more than "exit code 1". #483 did the same for failed tests
  - Status: Done (2026-10-24): under GitHub Actions, compare.py writes one `::error` per sample that differs (title `compare.py: <sample> differs`; the detectors with their counts, the first difference: a cut only one side has, else the first stats cell, else the first scene list cell, with both values; the run's options such as `--compat`, `--fast-yuv`, the extra args), at most 9 and one more naming the rest (GitHub keeps 10 errors per step), a `::notice` with how many comparisons matched, and an error when a tool fails or scenedetect writes no scene list. realworld.py passes `--label realworld.py` and annotates a failed download. `--informational` (ci.yml's `|| true` steps: detect-hash, interlaced without deinterlacing, corrupt.ts, interlaced 10-bit; parity-os.yml) writes only the notice, naming the samples that differ, so expected differences use none of the step's errors. tools/annotate.py has the escaping (`%`, CR, LF; also `:` and `,` in properties) and a `--test` the build job runs. Proved on the PR with a deliberately broken leg, read back with `gh api .../check-runs/<id>/annotations`

- Shots as they're found on the frame-source Detect (issue #493, from shotdetector-web)
  - Why: shotdetector-web's page waits for the whole run before showing a shot, and a cancelled run leaves nothing. `Run` already reports shots through `onShot` (DetectStreamAsync uses it), and the frame-source overload passes null. Additive: an option reported synchronously on the detection's thread, like `DetectionOptions.Warnings`. The shots must be exactly those Detect returns, in order: incrementally with one detector and no DropShortScenes/MergeLastScene, at the end otherwise
  - Status: **Done (2026-10-24)**: `DetectionOptions.OnShot` (`IProgress<Shot>?`, the owner's choice: docs/decisions/2026-10-24-shot-callback.md), told synchronously on the detection's thread by every Detect overload (file, Stream, frames) under Run's incremental rule; it only listens (Auto still chunks a file: chunked shots come in order once scored), and DetectStreamAsync tells it too (`OnShotTests`).

- Cancel a frame-source run on single-threaded wasm (issue #492, from shotdetector-web)
  - Why: On browser-wasm the run blocks the worker's event loop, so a cancel message never arrives and the page must terminate the worker. The run's loop pulls frames synchronously: either an awaitable path through the reader, or a push-style API (begin, push a frame, finish) whose loop the caller owns (which would cover the previous row too). The design goes to the owner as a decision when the work starts. Results must stay byte-identical to the synchronous Detect, which must never become sync-over-async; additive only (ApiBaselineVersion 3.14.0). After the previous row (same entry point, never in parallel)
  - Status: Done (2026-10-24): `ShotDetection.DetectAsync(YuvVideo, IAsyncEnumerable<YuvFrame>, ...)`, the owner's choice over a public push API (docs/decisions/2026-10-24-frame-source-async.md). The run is an iterator that yields before each frame it takes from the caller; both frame-source overloads fill a one-frame feed before each step (the asynchronous one awaiting its source, on no other thread), so they pull the same frames and give the same result; files and streams run straight through, unchanged. Cancellation is checked before each pull. `FrameSourceAsyncTests`, `FrameInputTests` (decoded clips through both), `tests/FramesWithoutFfmpeg`

- FrameReader: reference tone and waveform from decoded samples (issue #494, from shotdetector-web)
  - Why: shotdetector-web decodes audio with ManagedCodecs' AacDecoder, without FFmpeg, and wants the same ReferenceTone and WaveformData definitions FindReferenceTone uses, rather than a copy. Sample-fed entries that stream (a chunk at a time) and whose types never reach FFmpeg.AutoGen (proved like tests/FramesWithoutFfmpeg: not loaded by the run). A FrameReader release
  - Status: **Done (2026-10-24)**: `ReferenceToneFinder` (`Add` returns false once `SearchDuration` is read, `IsComplete`, `Finish`) and `WaveformBuilder` (`integerSamples` for integer-decoded sources), both with an optional duration for progress; `ReferenceTone.Find` and `WaveformData.Read` are loops over them, results unchanged (docs/decisions/2026-10-24-audio-from-samples.md). `AudioFromSamplesTests` (equal to Find/Read on real files however the chunks are cut), `tests/AudioWithoutFfmpeg`

- Frame-source detection in chunks: score a frame range of the caller's frames and merge, so a host can split a long video across several single-threaded runtimes (Web Workers)
  - Why: Measured 2026-10-24 (wasm AOT under Node, Sintel trailer): decoding is ~80 % of a browser run and detection ~5 %, so the levers are about decoding. Besides WebCodecs (shotdetector-web#15), splitting a long video across Web Workers, each with its own runtime and no wasm threads, would give about 2.5-3.5x on 4 cores. ShotDetector chunks in-process files already (docs/parallel-detection.md), but the frame-source path can't: it needs an API that scores a range (from a keyframe, with the detectors' warm-up the chunked path uses) and one that merges the ranges' results into exactly the sequential result. Pays only on long videos (chunks start at keyframes); ask shotdetector-web whether it wants it first
  - Status: **Done (2026-10-25)**: `ShotDetection.ScoreRange` (and `ScoreRangeAsync`) scores a range of the caller's frames (from the frame before it to one past its end) into `RangeScores`, which travel as versioned, checksummed bytes (`ToBytes`, `FromBytes`), and `ShotDetection.Merge` runs the unchanged decisions over every range's scores in order, as for a chunked file: exactly the sequential frame-source Detect's result. Merge refuses ranges of another video or options (a fingerprint), not from frame 0, out of order, with a gap or no overlap frame, an overlap frame whose score or pts differs, or frames off the constant-rate grid; a time range, frame skip and `DetectionMode.Parallel` are refused for ranges. The owner's choice of shape over a `PlanRanges` helper and JSON: docs/decisions/2026-10-25-frame-source-ranges.md; docs/parallel-detection.md, "Frame-source ranges (for hosts)". shotdetector-web picks the range starts at keyframes and runs the workers

- Benchmark history: each benchmark.yml run's numbers as JSON on a `bench-data` branch, so the trend can be charted
  - Why: The weekly results lived only in the run summaries (blob storage, readable in a browser only), so there was no history to compare or chart (the owner, 2026-10-25)
  - Status: **Done (2026-10-25)**: `tools/bench.py --json`, `tools/bench_record.py` (bench.py's files, benchmarks/micro's output and the run's context in one file per runner), and a `publish` job that commits them on main to the orphan `bench-data` branch under `runs/`; schema in benchmarks/bench-data.md (the branch's README). A page to chart it is not part of this

- Shot detection in the browser: a showcase app on browser-wasm decoding with ManagedCodecs (github.com/bisforboman/ManagedCodecs) and converting with ShotDetector.FastYuv, fed to ShotDetector through a public frame-source API
  - Why: ManagedCodecs' H.264 and MJPEG decoders are bit-exact with FFmpeg and already run on browser-wasm, and FastYuv converts in managed code, so detection can run with no native binaries at all and give a file's results (docs/decisions/2026-10-24-managedcodecs-showcase.md). The owner's shape (2026-10-24): ShotDetector takes decoded YUV frames from any decoder (not a ManagedCodecs backend inside FrameReader); the showcase lives in a new repository (e.g. bisforboman/shotdetector-web, own CI and GitHub Pages, ManagedCodecs as a submodule until it is on NuGet), plain HTML + JSExport with decoding in a web worker, MIT with FastYuv a separate, replaceable DLL in the bundle. First version: 8-bit 4:2:0 H.264 in MP4 at a constant frame rate, trailers rather than films (ManagedCodecs decodes at 2.3-3.6x FFmpeg's time on one thread). Exactness first: before any UI, ManagedCodecs + FastYuv must give the same scene and stats CSVs as the FFmpeg path, natively and under WASM
  - Status: **Done (2026-10-26)**: everything ShotDetector owed the showcase has shipped: the frame-source API (3.14.0), `DetectionOptions.OnShot` and `DetectAsync` over frames (3.15.0), ranges for several workers (`ScoreRange`/`Merge`, 3.16.0), the stats memory fix (3.16.1); ManagedCodecs is MIT (bisforboman/managedcodecs#114). The showcase itself continues in bisforboman/shotdetector-web (live on GitHub Pages), with its own backlog; its asks to this repository come as issues

- A WebAssembly benchmark in benchmark.yml: ShotDetector's frame-source path on browser-wasm AOT under Node
  - Why: The browser's speed was asked about twice and measured only with one-off scratch harnesses (2026-10-24/25). The frame-source `Detect` needs no decoder: pre-decoded YUV frames from a sample (ffmpeg rawvideo at build time) through `Detect` with FastYuv under Node, timing conversion, resize and detection, recorded to bench-data with the other numbers, so a regression in the wasm path shows week to week (FastYuv's SIMD, #503/#508, took 7.8 to 3.4 ns a pixel there). Needs the wasm-tools workload on the runner (its NETSDK1147 re-install gotcha)
  - Status: **Done (2026-10-26)**: benchmarks/wasm (Bench.cs on browser-wasm AOT under Node and natively, raw yuv420p frames of bbb and the Sintel trailer read from a file), a step in benchmark.yml's x64 job, the run's `wasm` section on bench-data (benchmarks/bench-data.md); the step fails when the WebAssembly CSVs differ from the native run's

- Charts in the benchmark's run summary: Mermaid xychart-beta blocks GitHub renders, of the numbers each run records
  - Why: The weekly tables are long and the bench-data history had no view at all; the owner chose charts in the summary itself (2026-10-26), drawn by GitHub with nothing to host
  - Status: **Done (2026-10-26)**: `tools/bench_chart.py`: in each benchmark job, shotdetect's wall time as a ratio of scenedetect's per film (content, content --stats) above those tables, with a line at 1; in the publish job (main only), the trend over the last 12 runs on bench-data (the median ratio per runner, and benchmarks/wasm's ns per pixel per film). A missing section leaves its chart out; `tools/bench*.py` count as light in tools/ci-changes.sh, like benchmarks/

- Measure where the ffmpeg-executable path's time goes, then decide what to fix
  - Why: The first recorded benchmark run (bench-data, 2026-10-25, run 40233248479) has `shotdetect --decoder process` at 1.04-1.30x scenedetect 0.7.2's time on every film, on x64 and ARM and on 2 CPUs, while the in-process default is 0.41-0.92x. It is the path library users get without FrameReader.Native, and both tools read ffmpeg's decoded frames, so the gap is ours: the rawvideo pipe, ffmpeg's BGR conversion and remap, the reader thread, or no chunking (sequential by design). Profile it on one film (CPU in ffmpeg vs in .NET, the pipe's throughput, what `remap` costs), then propose fixes with their expected gain
  - Status: **Done (2026-10-27)**: profiled on the Sintel trailer, Tears of Steel and a 640x360 stand-in (ffmpeg sets the pace; remap's looping map inputs were a quarter of its CPU); the findings, the ranked fixes and the owner's choice (the maps through the loop filter, landed with this row; chunking the executable path; tracking process + FastYuv in the benchmark) are in docs/decisions/2026-10-27-executable-path-fixes.md

- Chunk the ffmpeg-executable path: several ffmpeg processes, each with the exact `-copyts` + `select` seek and `-frames:v`, through `ScoreChunked` and its overlap check
  - Why: The profile (docs/decisions/2026-10-27-executable-path-fixes.md) found ffmpeg setting the pace on about 2.4 of 4 cores; two concurrent halves took ffmpeg alone about a fifth off on long films at the same CPU, and it is the only lever found for the 640x360 / whole-frame class, which doesn't use remap. Today `NotChunkable` refuses "decoded by the ffmpeg executable". Needs a CI job comparing chunked with sequential runs on the process path, in its gate's `needs`; the overlap check falls back to sequential on any mismatch
  - Status: **Done (2026-10-27)**: `NotChunkable` no longer refuses the executable; each chunk is its own ffmpeg process with the exact seek, its own maps folder and pipe, killed on cancel or failure; the first frame size comes from showinfo there too, and frame-threaded decoders are told by codec name (`VideoReader.FrameThreadedCodecs`). ParallelTests run on both decoders; CI's parity step compares chunked with sequential runs on every sample through the executable too (core and FastYuv), Real world parallel the films. On a 4-vCPU container Tears of Steel 1280x534 gained clearly, 640x360 a little (where ffmpeg already kept the cores busy), identical CSVs and stats

- FastYuv and the executable path: convert with each frame's chroma location and colour tags, not the first frame's
  - Why: The default path converts each frame from its own chroma siting and colour tags (as OpenCV's sws_scale_frame does), and in-process so does FastYuv since 2026-10-24 (B2 below, docs/decisions/2026-10-24-per-frame-tags-fastyuv.md); since 2026-10-19 the executable path's scale filter and its FastYuv take the first frame's (`FrameDecoder.ChromaLocation`/`ColorSpace`/`ColorRange`, ffprobe's frame entries; docs/decisions/2026-10-19-chroma-and-colour-tags-from-the-frame.md) for the whole video, so a video whose tags change midway still differs there. Measured on built clips (samples/informational/sintel_matrix_change.mkv, sintel_10bit_siting_change.mkv): the executable's FastYuv on a matrix or range change, and the executable's default path on a 10-bit siting change, differ in stats after the change only, with every cut exact and 0 CSV cells on every path since #432; not seen in a real file (docs/decisions/2026-10-19-per-frame-tags.md)
  - Options: B1, frames whose tags differ take the in-process BGR fallback (about 5 lines, no speed cost on x64, but ARM's rounding on those frames); B2, a FastYuv converter per tag set chosen per frame (30-60 lines, x86's results on ARM too); C, the executable path's FastYuv from showinfo's per-frame colour line (needs pairing two stderr lines, then B2's choice); D, the executable's default path on a siting change: ffmpeg's scale never follows a frame's siting, so only by restarting ffmpeg at the change (files only, a restart and a GOP per change)
  - Status: **Done (2026-10-27)**: B2 for in-process FastYuv (2026-10-24, #496), C and D through the executable (the owner's choice, docs/decisions/2026-10-27-per-frame-tags-executable.md): FastYuv takes each frame's tags from showinfo; the default path reads a file again from each chroma siting change with the new `in_chroma_loc` (a stream keeps the first siting and warns). 0 stats cells on the built clips on every path, required in CI

- MPEG-PS: frame timestamps from the frames as detection decodes them, no decoding pass first
  - Why: Packets without a pts (MPEG-PS) cost a whole decoding pass for the frames' timestamps before detection decodes the file again: 25-35% of such a run. In-process each frame says the pass's timestamp itself; `PacketOf` keeps its rule over the timestamps so far, the pass only where the answer depends on frames to come or something needs them all first (start time, executable, deinterlacing). Choice 1 of docs/decisions/2026-10-27-pre-scan.md
  - Status: **Done (2026-10-27)**: in-process MPEG-PS takes each frame's timestamp from the frame (`TimestampsArrive`, `PacketOfArrived`); the pass only where the timestamps so far don't settle a frame's packet or something needs them all first; frame labels aren't the identity (the flushed last frame, joined files), so `PacketOf` keeps its rule. About a quarter off a PS run in-process, byte-identical (#546)

- mp4/mov: a timing-only packet scan that skips `mdat`'s payload
  - Why: libavformat computes an mp4's packet pts from the sample tables, so the scan needs kilobytes, not the file (a prototype gave identical pts on faststart h264 with and without B-frames, reading 0.0-0.1 MB). Needs the zero-filling hook between the header probe and the packet loop (before it, pix_fmt is lost), a seekable custom AVIO for files (moov at the end), fragmented mp4 left out, and a FrameReader test that the pts equal the full scan's on every sample mp4/mov. A FrameReader release. Choice 2 of docs/decisions/2026-10-27-pre-scan.md
  - Status: **Done (2026-10-27)**: automatic in `MediaProbe` for local, unfragmented MP4/MOV with h264, HEVC, MPEG-2, MPEG-4 Part 2, MJPEG or ProRes video (MPEG-1 shown to differ: its parser splits by the data; other codecs and containers reopen through the file protocol); `FileInput`'s mdat payload reads as zeros after the headers; a 290 MB h264 file's scan read 0.5 MB

- FrameReader's `ImageEncoder` to WebP
  - Why: Issue #542's encoder writes JPEG and PNG; WebP is refused ("isn't available yet") because FrameReader.Native has no WebP encoder. It needs libwebp built and linked into every native variant (linux glibc, musl and arm64, win-x64 cross-compiled, osx-arm64), with its licence (BSD-3-Clause) in the SBOM, then the `webp` format with libwebp's own options and `Capability.WebP` (docs/decisions/2026-10-27-image-encoder.md: JPEG and PNG first, WebP later)
  - Status: **Done (2026-10-27)**: libwebp 1.6.0 linked statically into every FrameReader.Native variant with FFmpeg's `libwebp` encoder (not `libwebp_anim`; COPYING.libwebp, the SBOM, THIRD-PARTY-NOTICES); `ImageFormat.WebP` writes `ffmpeg -c:v libwebp out.webp`'s file byte for byte (the encoder's packet is the whole file, no muxer), with `WebPQuality` (`-quality`), `WebPLossless` (`-lossless`) and `WebPCompressionLevel` (`-compression_level`), and `Capability.WebP`. A FrameReader release

- Re-tune `SampledPays`' crossover after the loop filter
  - Why: Before the maps went through the loop filter, whole frames were 9% faster than remap on Tears of Steel (1280x534, 16% of the pixels read) and 7% slower on Sintel 1080p; the loop filter took about a seventh of ffmpeg's CPU off remap's side, so the two now look about equal at 1280x534 (docs/decisions/2026-10-27-executable-path-fixes.md). 0-9%, on 720p-class video only; measure on CI's x64 and ARM runners before moving the 0.25. Same results either way (both pipelines are CI-compared). The weekly benchmark's first `--decoder process --fast-yuv` row (run 40462238268, 2026-10-27) was 27-28% slower by wall than plain `--decoder process` at 640x360 (BBB) with equal CPU, i.e. waiting rather than work, so the crossover look covers FastYuv over the pipe too (the owner's choice, 2026-10-27)
  - Status: **Won't do (2026-10-27)**: measured on CI's 4-CPU runners, x64 and ARM (debug run 40583013074: `--decoder process --sequential`, content, FFmpeg 8.1, 5 interleaved rounds; the Sintel trailer and Tears of Steel re-encoded from 640x360 to 3840x1600; output identical). remap's rows against whole frames, by wall (x64 / ARM): 64% of the pixels read (640x360) 1.30 / 1.27; 36% (854x480) 1.11 / 1.11; 25% (1024x576, whole frames today) 1.02 / 1.02 with 10% less CPU; 20% (1152x480, 1152x648) 0.99-0.95 / 1.02-0.93; 16% (1280x534, 1280x720) 0.94-0.89 / 0.97-0.86 with 16-20% less CPU; 1080p 0.73 / 0.66; 3840x1600 0.66 / 0.58. The loop filter moved Tears of Steel from whole frames 9% ahead to remap 3-6% ahead; the crossover sits between 20% and 25% on both, where `< 0.25` already puts it, so nothing moves. FastYuv over the pipe at 640x360 (BBB): 1.27x the wall of plain `--decoder process` with equal CPU on ARM in one pass (`--sequential`), 1.01x with the default 2 chunks; on x64 0.96x and 0.90x. So it's ARM's one pass: likely FastYuv's conversion on the reader's side setting the pace there while ffmpeg waits (not profiled)

- Damaged MPEG-2 decodes differently from run to run with slice threads
  - Why: A PS with bytes cut out of its middle gave another frame (and other content scores) about 1 run in 15 with 8 decoding threads, on `main` too: FFmpeg's error concealment across slices depends on which thread finishes first. Undamaged files are unaffected, and OpenCV's decoder threads the same way, so scenedetect's own results on such a file aren't stable either. Find out whether one slice thread for MPEG-1/2 (or a concealment option) makes it repeatable and what it costs (found 2026-10-27 by ArrivingTimestampsTests, which now decodes with one thread)
  - Status: **Won't do (2026-10-27)**: measured; two causes (slices raced between threads, and leftover frame buffers that depend on when frames are released), scenedetect varies as much at its default threads; making MPEG-1/2 repeat costs undamaged MPEG-2 +28% chunked (1080i) to 2.1x single-pass. The owner chose to document it and stay best effort: README corrected, docs/decisions/2026-10-22-damaged-files-best-effort.md has the note

- Shots told before timestamps start over are not told again
  - Why: `DetectionOptions.OnShot` and `DetectStreamAsync` tell shots in order as cuts are confirmed; when timestamps start over (two streams joined) the cuts come out of order, the result's shots are sorted, and the shots told before that point aren't told again, so a listener's list can differ from `DetectionResult.Shots`. Documented in OnShot's XML doc (#497) but not fixed: e.g. tell a correction, or hold shots back once an out-of-order cut is seen
  - Status: **Done (2026-10-28)**: a file's probe shows the reset before the first frame (its decode-ordered packet pts, or MPEG-PS's decoding pass: a pts below one at least 16 earlier), and MPEG-PS decoded in-process (timestamps arriving with the frames) may start over: those tell every shot at the end, exactly the result's (the owner's choice, docs/decisions/2026-10-28-retell-shots-after-reset.md). A Stream decoded in-process still can't know it ahead (OnShot's XML doc); its difference from the executable is its own Next row

- Parallel detection above 8 CPUs: more than 4 chunks and 16 decoder threads in total
  - Why: `AutoChunks` = min(4, CPUs / 2, whole minutes) and `DefaultDecodeThreads` = min(CPUs, 16): on 16 CPUs 4 chunks of 4 threads, on 32 still 4 x 4. A chunk keeps about 1.5-1.75 cores busy (profiled on 4 vCPUs), so by that estimate half of 32 cores sit idle. Not measured: the weekly benchmark only runs on 4-vCPU runners, so measure first on a larger machine (the owner's 16-thread desktop, asked for, or a larger runner), then raise the caps if it pays; results stay identical (CI compares chunked with sequential)
  - Status: **Done (2026-10-28)**: measured on the owner's 16-thread Ryzen with tools/bench-cpus.ps1: more than 4 chunks was slower at every thread count and 32 threads gained nothing over 16 (24: 2-3%); the owner kept both caps, docs/decisions/2026-10-28-parallel-caps-16-threads.md. Above 16 CPUs unmeasured

- An image encoder that scales from a decoded frame, exactly as ffmpeg's one command
  - Why: `ImageEncoder` gives the file ffmpeg writes from the same raw frame. ffmpeg's one command `-vf scale=...,format=F out.jpg` differs once it scales: its format negotiation folds the conversion to the encoder's full range into the user's scale (one swscale pass, where the reader scales and the encoder converts), and from an RGB frame of a BT.709-tagged source it keeps BT.709 for the YUV (a raw RGB frame gets BT.601). Encoding a decoded frame (`FrameDecoder.TakeFrame`, or a `VideoFrameReader` that hands out its frames before converting, through its fps graph) with the encoder's own graph `buffer -> scale=...:flags=bicubic -> format -> buffersink` would be ffmpeg's graph itself, byte for byte with `ffmpeg [-ss T] -i X -vf [fps=R,]scale=... -frames:v 1 out.jpg` (docs/decisions/2026-10-27-image-encoder-input.md)
  - Status: **Done (2026-10-28)**: `ImageEncoder.Encode(DecodedFrame, ...)` with `ImageFilterOptions` (`Width`/`Height`, `MaxWidth`, or a `-vf` `Filter`, the owner's choice: docs/decisions/2026-10-28-save-images-in-process.md), ffmpeg's graph buffer -> -vf -> format -> buffersink and its encoder setup (the decoder's bits per raw sample only through a metadata-only graph), byte for byte with `ffmpeg [-ss T] -i X [-vf ...] -frames:v 1 out.jpg` on 17 sources (10-bit, interlaced, rotated, anamorphic) for JPEG, PNG and WebP. A FrameReader release; ShotDetector's save-images moves onto it next

- corrupt.ts: two decoded frames with the same pts — OpenCV numbers both, we keep one
  - Why: Found 2026-10-22 measuring damaged files (docs/decisions/2026-10-22-damaged-files-best-effort.md): on a stand-in clip's corrupt.ts (a 640x360 29.97 fps Sintel trailer through tools/make-broken.py; the real trailer couldn't be downloaded), the decoder outputs two frames with the same pts (291). OpenCV numbers both (the first as frame 237 at 7.875 s), ShotDetector keeps one, so our cuts come one frame later (09.643 against 09.610) and ~236 stats rows differ; deterministic, at any thread count and on both decoder paths. It is the "frame 236 at 8.24 s, expected 7.87 s" of a chunk fallback message. Check first whether it happens on CI's real samples (corrupt.ts is informational there)
  - Status: **Done (2026-10-28)**: reproduced on real samples with other seeds of make-broken's corruption (the Sintel 480p trailer as TS, seed 2; the Sintel DivX mkv, seeds 0-2) and 10 more of 110 damaged variants: a late frame's pts goes back and the best-effort timestamp falls back to the dts, while OpenCV labels by the frame's own pts. In-process now labels as OpenCV (`VideoReader.LabelPts`); the executable can't (ffmpeg sets each frame's pts to the best effort), documented in README (the owner, docs/decisions/2026-10-28-damaged-frame-pts.md). On the real trailer every cut exact and the scene list identical in-process (the rest is threaded concealment); make-broken.py's corrupt_copy.ts (the trailer as TS, seed 2) is in CI's informational damaged-files step

- `VideoFrameReader.TryReadForwardTo`'s keyframe jump lands on the wrong frame in MPEG-PS
  - Why: Found 2026-10-28 while fixing the timestamp-less last frame: a 2 s `-c:v mpeg2video -bf 2 -g 12` .mpg (start time 0.54 s), read from the start: `TryReadForwardTo(0.9 s)` gives the frame at 1.44 s and `TryReadForwardTo(1.85 s)` none, where the same clip in mkv gives 0.92 s and 1.88 s (main 6266830 too, so not that fix's). The jump seeks with `avformat_seek_file` to the target (no `SeekPoint` margin, while MPEG-PS seeks by dts), and the first call compares the target with 0 rather than the start time, so it jumps from 0.46 s on. ReadAt and the TryTake methods share it. Find where MPEG-PS's seek lands, then give the frames a full decode gives (as for mkv)
  - Status: **Done (2026-10-28)**: root cause: MPEG-PS (libavformat/mpeg.c) indexes every PES with a dts as a keyframe ("FIXME keyframe?") and seeks by dts to any of them, so a jump landed mid-GOP and decoding began at the next I-frame, past the target; and AVI with B-frames, which has only dts, times its I- and P-frames by libavcodec's guess from the frames decoded before, so after a jump an I-frame came out three frames late (a test found it). `FrameDecoder.KeyframeAtOrBefore` gives no keyframe to jump by in either, so reading forward decodes through, as ffmpeg's output-side `-ss` does. Before the first read the jump is now measured from the start time, not 0. Tests: SeekDemuxerTests.ForwardIsTheFullDecodesFrame (.mpg, .vob, mpeg4 and Xvid .avi with B-frames, ReadAhead 0/2, against the full decode and, same build, ffmpeg's frames)

- `VideoFrameReader.Seek` (and `TryReadAt`) near the start of an AVI with B-frames throws "FFmpeg (seek): Operation not permitted"
  - Why: Found 2026-10-28 with the unreferenced-frames tests: `-c:v mpeg4 -bf 2` (or libxvid) in .avi, `TryReadAt(0)` and `TryReadAt(0.1 s)` throw while 0.7 s works (main 6266830 too). `SeekPoint` takes ffmpeg's 3/23 s margin for formats that seek by dts with B-frames, which puts the timestamp before the start, and `avformat_seek_file` refuses it; ffmpeg's command line only warns ("could not seek to position") and reads from where it is. Do as it does
  - Status: **Done (2026-10-28)**: root cause as written: `avformat_seek_file` refuses the margin's timestamp before the first index entry, where ffmpeg's command line warns and reads on. `SeekLikeFfmpeg` does the same: a fresh reader reads on (libavformat has dropped what it read to probe, so its frames are ffmpeg's `-ss` frames, byte for byte), and one that has read already seeks to the start. Tests: SeekDemuxerTests.SeekNearTheStartReadsFromTheStartAsFfmpeg

- `VideoFrameReader` on raw elementary streams (.m2v, .m4v, .h264) and FLV: some reads forward and seeks aren't ffmpeg's frames
  - Why: Found 2026-10-28 extending SeekDemuxerTests beyond the .mpg/.avi fix (8 s 320x240 testsrc2, FFmpeg 8.1, main after it): raw.m4v (`-c:v mpeg4 -bf 2`) `TryReadForwardTo(3.312 s)` gives the frame at 3.36 s where the full decode has 3.32 s (a keyframe jump); raw.m2v's forward frame at 0.912 s isn't ffmpeg's output-side `-ss` frame although it is the full decode's (ffmpeg's command line times raw streams itself); `TryReadAt(0)` on raw.h264 finds no frame; on raw.m2v and FLV (x264) a reused reader's `TryReadAt(0)` isn't ffmpeg's `-ss 0` frame. Root-cause each (raw demuxers have no timestamps: AVFMT_NOTIMESTAMPS, a generic index) before deciding which to fix
  - Status: **Done (2026-10-29)**: raw demuxers have AVFMT_NOTIMESTAMPS, so ffmpeg's command line drops their timestamps (frames timed from 0 by the frame rate) and its -ss can't seek them (raw H.264/HEVC: no frame at any time); the reader times them as ffmpeg, makes no jumps there and seeks by decoding from the start. The "raw.m4v" was an MP4 (ffmpeg's .m4v muxer): its index by dts landed a jump on the keyframe after the time; now from the keyframe before when a jump lands past it. FLV's refused seek left ffmpeg past the first keyframe (frame 5.28 s in, or none): a fresh reader reads from the start too

- A Stream whose timestamps start over: in-process and the executable give different shots
  - Why: Both decoders should give the same. Two h264 .ts encodes joined, the second's timestamps starting over (TimestampResetTests' JoinedEncodes), content detector, FFmpeg 8.1: as a file, both give cuts 50, 75, 100 (frames labelled by time, as scenedetect labels them); as a Stream (`Detect(Stream)`, `DetectStreamAsync(Stream)`), in-process gives 50, 75, 100 and the executable 50, 100, 125, 175, because ffmpeg without `-copyts` makes an mpegts discontinuity's timestamps continue. Pick the reference (the file's labels, or ffmpeg's continuing times, which no scenedetect run compares) and make the other decoder match. In-process, a Stream also can't know the reset ahead, so its OnShot listener misses the shots it told before (OnShot's XML doc; docs/decisions/2026-10-28-retell-shots-after-reset.md); continuing times would end that too
  - Status: **Done (2026-10-29)**: scenedetect 0.7.2 gives 50, 75, 100 on the file and on a pipe (OpenCV reading pipe:0 or a Python stream: its times start over as libavformat gives them), so in-process was right. The cuts 50, 100, 125, 175 were the ffmpeg command line's own correction (fftools' ts_discontinuity_detect: an MPEG-TS dts going back over 0.1 s or jumping over 10 s continues), which -copyts turns off: streamed runs of the executable now take -copyts too and subtract the input's start from ffmpeg's own Duration line, as its shift did (the owner's choice, docs/decisions/2026-10-29-stream-timestamp-reset.md). A Stream still can't know the reset ahead, now with either decoder (OnShot's XML doc)

- `TryReadForwardTo`: tell the decoder the next target early, so frame skipping drops more
  - Why: Skipping unreferenced frames (#578) drops only about 75% of the droppable packets in the issue #566 loop (1701 of 2237), because frame threading and ReadAhead read packets before the next target is known. `ReadAt` already sets each reader's next time as soon as a frame is handed over; `TryReadForwardTo` doesn't know its next target, but could guess it from the last steps and fall back exactly (read those packets again from the keyframe) if a dropped packet turns out to be at or after the real next target. Exact by the same argument as #578; measure the gain on the issue's clip
  - Status: **Done (2026-10-29)**: `TryReadForwardTo`/`TryTakeForwardTo` guess the next time after steps of one length (at least 2 in a row, doubled by each wrong guess; the next time plus the shortest of them) and `ReadAt` passes the one it knows. While decoding to a target, a packet after it and before the next time is left out once a packet at or after the target and before it is in the decoder (so it isn't the wanted frame); once the frame is handed over, everything before the next time. Exact by a check, not by the guess: each read forward (and a plain read or take after a guessed one) looks for a packet left out at or after its time and before the frame found (or the end), and if there is one reads again from the index's keyframe before, as the jump fallback does. Only where that can go back (`CanReadAgain`: a file with its own header index, mp4, mov, mkv; not Streams, MPEG-TS, whose timestamp seeks landed a keyframe late in the tests, MPEG-PS, AVI with B-frames or raw streams); nothing changes where skipping is off. Measured on a 4-vCPU cloud runner (BtbN FFmpeg 8.1.3, the issue loop: MaxWidth 1280, Yuv420p, JPEG q2, files written; mean of 3 interleaved runs against main): issue clip 1080p25, 300 pictures, packets left out of 2237: 1701 -> 2136 (ReadAhead 0), ~1330 -> 2134 (ReadAhead 4); default threads, ReadAhead 0: 12.7 -> 11.4 s wall, 36.5 -> 32.8 s CPU; ReadAhead 4: 12.3 -> 10.7 s, 37.7 -> 32.9 s; 2 threads, ReadAhead 4: 18.3 -> 16.2 s, 36.2 -> 32.6 s; 2 threads, ReadAhead 0: level (18.8/19.2 s; main already left out 1967). Tears of Steel 720p, 3 min, ReadAhead 4: h264 3.69 -> 2.85 s wall, 10.3 -> 8.2 s CPU; x265 4.97 -> 4.15 s, 11.2 -> 9.0 s. JPEGs identical to main's. A wrong guess costs one read again from a keyframe: steps cycling 1, 1, 0.2 s guess wrong once per reader (the shortest-of-two rule first tried guessed wrong 38 times there, +12-17% CPU), within noise of main on the issue clip, +0.5 s CPU on Tears of Steel. Tests: SkipUnreferencedTests' irregular steps (constant, a short step after them, increasing, a jump over a second, reads and takes after a guessed read, past the end and back) on all 15 sources, ReadAhead 0/4, threads 1/default, read again on every indexed source with skipping and never elsewhere; four mutation lines

- MPEG-PS through the ffmpeg executable: drop the frame-timestamp pass, as in-process does since #546
  - Why: Through the executable, an MPEG-PS file (packets without a pts) still takes an ffprobe frames pass for each frame's time before detection: 8.5 s on a 4-minute 576p clip, 25-35% of the run (pre-scan memo, docs/decisions/2026-10-27-pre-scan.md); #546 removed the pass in-process only. The executable path already reads each frame's showinfo line. Check first whether showinfo's per-frame times reproduce the pass's rule (pts if not 0, else pkt_dts) on every PS sample; if they do, take them from there, byte-identical, else measure what's left and decide
  - Status: **Done (2026-10-29)**: showinfo's times don't reproduce the pass's (the command line extrapolates a time for frames without pts and dts, every PS file's last; libavcodec's pts correction can prefer a dts over a real pts, 3 frames of an h264 PS), so the pass now runs alongside the read instead of before it, frames labelled from its timestamps as they arrive, results identical (the owner's choice, docs/decisions/2026-10-29-mpeg-ps-pass-executable.md)

- Frame-source detection reaches FFmpeg-only code, so trimmed apps ship FFmpeg.AutoGen (issue #597)
  - Why: shotdetector-web (browser WebAssembly, frames decoded in the browser) runs `DetectAsync` on YuvFrames, `ScoreRangeAsync` and `Merge`, never FFmpeg, yet its trimmed bundle kept FFmpeg.AutoGen and the AOT code of the FFmpeg paths: `Steps` constructed the file and Stream readers and called FindReferenceTone and ScoreChunked, and VideoReader's shared members branched to the in-process and executable reads
  - Status: **Done (2026-10-30)**: the file and Stream half of the run behind `ShotDetection.IDecodedInput`, VideoReader's decoders behind `VideoReader.IDecoding`, each implemented by a type only decoded inputs create, so the trimmer drops them. benchmarks/wasm's AOT `_framework/`: 14.40 -> 10.39 MB raw, 4.26 -> 3.28 MB gzipped, FFmpeg.AutoGen gone. benchmarks/wasm also runs DetectAsync, ScoreRange(Async) and Merge against Detect's CSVs, and ci.yml's `wasm-trim` job fails when its bundle has FFmpeg.AutoGen (tools/trim_path.py prints why). #598 (OnShot for ranges) next (docs/decisions/2026-10-30-frame-sources-without-ffmpeg.md)

- ScoreRange from frame 0: honour OnShot, as Detect does (issue #598)
  - Why: shotdetector-web resumes a stopped run after a page reload from its ranges' `RangeScores`, so every run that might stop is a `ScoreRangeAsync` from frame 0, which told no shots: the page lost its live shot table until the merge
  - Status: **Done (2026-10-30)**: a range from frame 0 with `OnShot` runs Merge's decisions as it scores (`LiveShots`, `Steps` over a live `Prescored`), telling Detect's shots after the same frames, whole and stopped; `ScoreRange`/`ScoreRangeAsync(video, earlier, frames, firstFrame, ...)` continue from the earlier ranges' scores (checked as Merge checks them, overlap frames too) and tell the rest of the whole video's shots, the confirmed ones not again (the owner asked for this optional part too: docs/decisions/2026-10-30-score-range-on-shot.md). Tests: FrameRangeTests on every detector configuration, stats on and off, timed and not; eight mutation lines

- FrameReader: skip unreferenced frames on MPEG-TS too (forward reads at steps), by making its re-read from a keyframe exact
  - Why: #594's skipping (`SkipAhead`, `CanReadAgain`) left out 1701 -> 2136 of 2237 packets and about 10% CPU on the issue loop, but only where a wrong guess can read again from the index (mp4, mov, mkv); MPEG-TS is excluded because its timestamp seeks landed a keyframe late in the tests, and TS is the broadcast container. Find why the seek lands late, make the re-read exact there, then let TS in (SkipUnreferencedTests on a TS source)
  - Status: **Done (2026-10-30)**: a TS seek by time lands on the last packet with a dts before the time, which mpegts_get_dts indexes as a keyframe whether or not it is one ("FIXME keyframe?"), so decoding started at the next keyframe, a whole GOP late (even the keyframe-before retry). MPEG-TS now records the keyframe packets it reads (pts, byte position) and reads again from the one at or before the time by byte position (from the start when none was read), the one before that when the check fails. Issue clip as TS, ReadAhead 4: 1335 -> 2132 packets left out, 37.5 -> 32.7 s CPU, 12.7 -> 11.0 s wall; Tears of Steel 3 min as TS: 10.4 -> 8.2 s CPU (h264), 11.0 -> 9.2 s (x265), JPEGs identical. Tests: SkipUnreferencedTests on h264, open-GOP, intra-refresh, HEVC and MPEG-2 TS read again and match decoding every packet; four mutation lines


- Frame sources: take the display rotation and turn frames as the file path does (issue #608)
  - Why: shotdetector-web's backlog row "Rotation": a phone's rotated MP4 got other stats, another processed size at 90 degrees and other hash cuts at 180 on the frame-source path than through `Detect(path)`
  - Status: **Done (2026-10-30)**: `YuvVideo.Rotation` (any quarter turn, as `DetectedVideo.Rotation`: docs/decisions/2026-10-30-frame-source-rotation.md); frames turned after converting as the file path's `Turn`, the upright size reported, the rotation in Merge's fingerprint. Tests: FrameInputTests against `Detect(path)` on 90/180/-90 copies (content, adaptive, hash, crop, no resize) and merged ranges; benchmarks/wasm checks each rotation on WebAssembly against native

- Frame sources: variable frame rate, as the file path takes it (issue #609)
  - Why: shotdetector-web's backlog row "Variable frame rate": the frame-source path refused frames off the average rate's grid, so a variable-rate MP4 stopped at frame 2 where `Detect(path)` gave three shots
  - Status: **Done (2026-10-30)**: `YuvVideo.FrameTimestamps`, every frame's pts given up front (the owner's choice over a flag or no check: docs/decisions/2026-10-30-frame-source-vfr.md), in place of the grid: each frame at its entry, ranges identified by them in Merge, StartTime as the file path's search, a dropped frame taken as a file's run takes it. Tests: FrameInputTests against `Detect(path)` on the issue's clip (every detector kind, start times, a time range, 0.7.1, merged ranges); benchmarks/wasm checks a variable-rate variant on WebAssembly against native

- Frame sources: deinterlace as the file path does (issue #614)
  - Why: shotdetector-web's "Interlaced H.264" row: `--deinterlace auto`, the setting for broadcast material, had no frame-source equivalent, and deinterlacing in the caller would copy FrameDecoder's row-wise yadif outside this repository
  - Status: **Done (2026-10-30)**: `YuvVideo.FieldOrder`, `YuvFrame.Interlaced` and `TopFieldFirst`, `Deinterlace` with `Yadif` (docs/decisions/2026-10-30-frame-source-deinterlace.md); frames held as FrameDecoder's row-wise yadif holds them, copied. Tests: FrameInputTests against `Detect(path)` on libx264 MBAFF (top and bottom first) and MPEG-2, Auto and On, start, skip, no resize, rotated; ranges refuse it

- Frame-source parity audit: list in one pass what `Detect(path)` handles or reports that `YuvVideo`/`YuvFrame` can't express, and file what's missing
  - Why: #608 (rotation), #609 (variable frame rate) and #614 (deinterlacing) were filed on one day, one at a time, from the same caller (shotdetector-web's ManagedCodecs path). One audit of the file path's inputs and `DetectedVideo`'s fields against the frame-source path's (colour tags, chroma location, field order, rotation, timing, start time, crop and scale inputs, warnings) finds the rest at once. After #614
  - Status: **Done (2026-10-31)**: docs/frame-source-parity.md, one table of what the file path handles against the frame source, each divergence shown by running both paths on a clip (FFmpeg 8.1, Linux x64). Silent gaps: a frame size changing midway (read as the larger frame's corner: another cut), colour tags changing midway (other stats); refused: 10-bit 4:2:0 and 8-bit 4:2:2, deinterlaced ranges, odd heights, other pixel formats, reference tone. Six proposed issues there, for the owner to pick

- Timeline: YouTube chapters export (#648)
  - Why: A chapters export beside Edl, Fcpx, Otio, Qp and ShotChanges, for the CLI and shotdetector-web's page (shotdetector-web#84's chapters part); YouTube's rules (first chapter at 0:00, at least three, each at least 10 s) to verify before building, and how shots merge into chapters that meet them to decide
  - Status: **Done (2026-10-31)**: #654. `Timeline.YouTubeChapters` and `--save-youtube-chapters`/`--chapter-min-length`: YouTube's rules checked on its help page; shots merge greedily until a chapter has run the minimum, cut times rounded up to the second, "Chapter N" titles for the page to edit (docs/decisions/2026-10-31-youtube-chapters.md); the rule alone public as `Timeline.ChapterStarts` for other players' chapters (#634), the export's home and the standing rule that cut-list formats live on `Timeline` in docs/decisions/2026-10-31-youtube-chapters-home.md

- CLI: exact contact sheet next to --save-images (#649)
  - Why: A grid of each shot's frames with its number, timecode and length, as PNG or PDF, built from `--save-images`' exact frames (the page's version, shotdetector-web#93, stays there with the browser's stills); format, layout and what it shares with `--save-html` to scope
  - Status: **Done (2026-10-31)**: #659. `--contact-sheet <file.png|file.pdf>` and `--contact-sheet-width`: a row per shot (number, start timecode, length, then save-images' frames), PNG or PDF by the extension (docs/decisions/2026-10-31-contact-sheet.md); the PNG composed here with a built-in bitmap font (no drawtext in our FFmpeg build), the PDF embedding save-images' JPEGs as they are; tests compare every thumbnail with save-images' own

- Frame sources: LoadScenes for frames from the caller (#647)
  - Why: `ShotDetection.LoadScenes` takes only a video path; frames from the caller (`YuvVideo` + `YuvFrame`s) have no equivalent. shotdetector-web#90 (correcting cuts on the page) needs the exports from an edited cut list for a video read in the browser; done when they equal `LoadScenes(path)`'s byte for byte (docs/frame-source-parity.md lists the gap)
  - Status: **Done (2026-10-31)**: `ShotDetection.LoadScenes(YuvVideo, TextReader, column, options)` (the owner's choice, docs/decisions/2026-10-31-load-scenes-frame-source.md): the video's properties from `YuvVideo`, the list as text, no converter; an edited list's shots, cuts, positions, scene CSV and EDL/QP/keyframes/shot-change exports equal `LoadScenes(path)`'s byte for byte (`FrameInputTests.LoadScenesFromTextIsTheFiles`)
