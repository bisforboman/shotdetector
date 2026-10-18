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
  - Status: Open (2026-10-17: ARM's default results differ from x86's by FFmpeg's conversion rounding, documented, docs/decisions/2026-10-17-arm-results.md; on x86 none known)

- Parallel detection: fewer frame buffers per chunk (8 / chunks instead of 8 each)
  - Why: Each chunk's in-process reader keeps 8 full-frame buffers (VideoReader.InProcess.cs), which is most of the memory growth per chunk (the release-candidate security review, 2026-10-18: a 4096x4096 clip with FastYuv peaked at 525 MB sequential, 836 MB in 2 chunks, 1197 MB in 4). Sharing the 8 between the chunks would keep a parallel run near a sequential one's memory; the 3840x2160 cap on Auto (docs/decisions/2026-10-18-parallel-auto-threshold.md) limits it meanwhile
  - Status: Open

- Parallel detection: hand the outer reader's packet list to the chunks' readers
  - Why: Each chunk's `new VideoReader` (ShotDetection.ScoreChunked) probes the file and reads every packet's pts again, N times at once: about 0.1 s on mp4, growing with the file on MPEG-TS (the release-candidate performance review, 2026-10-18). The outer reader already has the list
  - Status: Open

- FrameReader: unknown `InputOptions` names fail, as `CodecOptions` and the encoders' do
  - Why: `Demuxer.Open` passes `FrameDecoderOptions.InputOptions` to avformat_open_input and drops what's left, so a misspelt demuxer option is ignored without a word (the release-candidate security review, 2026-10-18), though FrameReader's README says unknown option names are an `ArgumentException`. `CodecOptions` checks its leftovers since 2026-10-18. Changing it could break callers passing options some demuxers lack (ShotDetector passes the image sequences' `framerate`), so it needs a look per caller
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

- ARM's results with FastYuv for 10-bit, 4:2:2, MPEG-2 and MJPEG video (MPEG-2 and MJPEG done)
  - Why: FastYuv gives x86's results on ARM for 8-bit 4:2:0 only (arch-diff's table, 2026-10-17): 10-bit and 4:2:2 take other swscale paths, and MPEG-2's and MJPEG's decoded frames already differ (their IDCTs round per platform), so those still differ on ARM
  - Status: 4:2:2 done (2026-10-18, #404): FastYuv converts yuv422p/yuvj422p (`IYuv422Converter`; rotated frames converted before turning), arch-diff requires them (docs/decisions/2026-10-18-fastyuv-422.md). 10-bit 4:2:0: a port reproduces x86's bytes (2026-10-18, #412: `IYuv420P10Converter`, swscale's bicubic filters, MMXEXT vertical scaler and output, the C output for the last two rows; docs/fastyuv-10bit-422.md "As built (10-bit)"), waiting on ARM == x64 in #412's arch-diff and on the owner's choice for x64, where it gives the same bytes as the default path but slower on small video (on every CPU, off x86 only, or a frame-scoped conversion first); MPEG-1/2 and MJPEG done (2026-10-17): ShotDetector decodes them with FFmpeg's C IDCT (`-idct simple`; in-process FrameReader's new `FrameDecoderOptions.CodecOptions`, so FrameReader itself still gives ffmpeg's default bytes), which x64's default already gives (checked with FFmpeg 8.1 and 9.0) and ARM's NEON IDCT doesn't (#384's checksums); arch-diff requires them too

## Maybe

- Warn for a local playlist whose segments are https
  - Why: DetectionResult.Warnings (2026-10-18) covers an https playlist decoded by the ffmpeg executable; a local .m3u8 naming https segments takes the same unchecked path, but the decision left it undecided (docs/decisions/2026-10-18-library-warnings.md)
  - Status: Maybe

- Warnings for streaming callers (DetectStreamAsync)
  - Why: DetectStreamAsync yields shots and has no result object, so it can't carry DetectionResult.Warnings; a streaming caller decoding an https playlist through the executable gets no warning
  - Status: Maybe

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
