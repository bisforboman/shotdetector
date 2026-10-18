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
  - Status: Open (2026-10-18: on x86 none known; on ARM the default results differ by FFmpeg's conversion rounding, documented in docs/decisions/2026-10-17-arm-results.md, while ShotDetector.FastYuv gives x86's results on 8-bit 4:2:0 and 4:2:2 and 10-bit 4:2:0 of even width, and MPEG-1/2, MJPEG and MPEG-4 Part 2 decode as on x86)

- Parallel detection: fewer frame buffers per chunk (8 / chunks instead of 8 each)
  - Why: Each chunk's in-process reader keeps 8 full-frame buffers (VideoReader.InProcess.cs), which is most of the memory growth per chunk (the release-candidate security review, 2026-10-18: a 4096x4096 clip with FastYuv peaked at 525 MB sequential, 836 MB in 2 chunks, 1197 MB in 4). Sharing the 8 between the chunks would keep a parallel run near a sequential one's memory; the 3840x2160 cap on Auto (docs/decisions/2026-10-18-parallel-auto-threshold.md) limits it meanwhile. Since 3.8.0 Auto chunks from 4 CPUs (docs/decisions/2026-10-18-parallel-auto-4cpus.md), so this is the default's cost: +10 to 54 MB peak per 2 chunks on GitHub's 4-CPU runners up to 1080p
  - Status: Open

- FrameReader: unknown `InputOptions` names fail, as `CodecOptions` and the encoders' do
  - Why: `Demuxer.Open` passes `FrameDecoderOptions.InputOptions` to avformat_open_input and drops what's left, so a misspelt demuxer option is ignored without a word (the release-candidate security review, 2026-10-18), though FrameReader's README says unknown option names are an `ArgumentException`. `CodecOptions` checks its leftovers since 2026-10-18. Changing it could break callers passing options some demuxers lack (ShotDetector passes the image sequences' `framerate`), so it needs a look per caller
  - Status: Open

- FastYuv 10-bit: filter each chroma row once per frame
  - Why: On x64, FastYuv for 10-bit 4:2:0 is about 2.5-3.5x slower than the default path at 854x480 (1080p about even; docs/decisions/2026-10-18-fastyuv-10bit-x64.md), because each chroma row is filtered horizontally again for every output row that reads it. A per-frame chroma pass would remove that: a batch method (the needed chroma rows filtered once, then the rows converted), additive as a new interface beside `IYuv420P10Converter` that the pipeline type-tests for, and CvResize's YUV path reworked to call it per frame
  - Status: Open

## Waiting

- Move to FFmpeg 9's libraries (FFmpeg.AutoGen 9, Native packages on 9.0.x) when opencv-python's Linux wheels bundle FFmpeg 9
  - Why: scenedetect's frames come from OpenCV's FFmpeg; moving earlier risks bytes OpenCV doesn't give, and drops system FFmpeg 8 in-process
  - Status: Maybe (2026-10-14: FFmpeg.AutoGen 9.0.1.1 is out; opencv-python 5.0.0.93 and 4.14.0.94 still bundle libavcodec 62, so the trigger hasn't fired; the user: wait for OpenCV)

- Check upstream.yml's BtbN job on its first scheduled run (Monday 2026-10-19)
  - Why: #357 wasn't dispatched on its branch (the workflow's other jobs could open real issues), so its first real run is the check: the log should say the pin is up to date, or open one issue
  - Status: Open

- The owner files the x264 AVX-512 report with VideoLAN (x264-avx512-report.md in the main checkout)
  - Why: CI's VideoWriter step needs DOTNET_TieredCompilation=0 because x264's AVX-512 code and .NET's tiered JIT differ about 1 run in 250 (CLAUDE.md); the report was written but not filed as of the 2026-10-15 reminder (it waited on a VideoLAN GitLab account). A public post from the owner's account: theirs to file, never a session's
  - Status: Open

- The owner files a StyleBro issue: `--verify-no-changes` on Linux flags untouched files
  - Why: In a Linux cloud container, `dotnet tool run stylebro-migrate format ShotDetector.slnx --verify-no-changes` reports WHITESPACE errors on main as it is (2026-10-18): src/ShotDetector/Detectors.cs(298,115 and 300,68), FrameTime.cs(69-71), VideoReader.cs(946,112 and 948,119), VideoReader.InProcess.cs(17,31 and 19,95), tests/ShotDetector.Tests/Tests.cs(213-214), each "Replace N characters with '\n...'" around comments in expression-bodied members of these CRLF files, while Windows CI's check passes. Every cloud session and the daily triage see them; CLAUDE.md sends StyleBro feedback to its issues, a repository outside this one's sessions
  - Status: Open

## Maybe

- Parallel detection: hand the outer reader's packet list to the chunks' readers
  - Why: Each chunk's `new VideoReader` (ShotDetection.ScoreChunked) probes the file and reads every packet's pts again, N times at once: about 0.1 s on mp4, growing with the file on MPEG-TS (the release-candidate performance review, 2026-10-18). The outer reader already has the list
  - Status: Maybe (2026-10-18: measured about 0.1 s on mp4 and 0.3 s on a 500 MB MPEG-TS per run, small next to the detection)

- FastYuv: convert with each frame's chroma location, not the stream's
  - Why: The default path converts each frame from its own chroma siting (as OpenCV's sws_scale_frame does), FastYuv from the stream's (FrameReader exposes no per-frame chroma location), so a frame tagged differently from its stream would differ in-process; rare (h264 sets it per SPS), found while building FastYuv 10-bit (#412). Needs FrameReader to expose the frame's chroma location (additive)
  - Status: Maybe

- Warn for a local playlist whose segments are https
  - Why: DetectionResult.Warnings (2026-10-18) covers an https playlist decoded by the ffmpeg executable; a local .m3u8 naming https segments takes the same unchecked path, but the decision left it undecided (docs/decisions/2026-10-18-library-warnings.md)
  - Status: Maybe

- Warnings for streaming callers (DetectStreamAsync)
  - Why: DetectStreamAsync yields shots and has no result object, so it can't carry DetectionResult.Warnings; a streaming caller decoding an https playlist through the executable gets no warning
  - Status: Maybe

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
