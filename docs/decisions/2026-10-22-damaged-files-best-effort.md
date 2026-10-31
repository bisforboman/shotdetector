# Damaged files are best effort (2026-10-22)

### Question

What should "identical" mean for damaged files?

The backlog row "Repeated runs on damaged files give different scene or stats results at the same thread count" was
measured (FFmpeg 8.1 shared build, 4 vCPUs, on a stand-in 640x360 29.97 fps 15 s Sintel trailer, because the real
854x480 24 fps trailer couldn't be downloaded; samples/broken built from it by tools/make-broken.py):

- At one decoder thread (`--threads 1`) our results are fully deterministic, and identical in-process and through the
  ffmpeg executable. At 4 or 8 threads corrupt.ts and corrupt.mp4 give a different stats CSV almost every run (87-174
  rows differ), and about 1 run in 10 adds or moves a cut; truncated.ts varies only in its last frame or two (cuts
  never change). truncated.mp4 and base.mp4 never vary.
- The cause is libavcodec itself: `ffmpeg -threads N -f framemd5` varies the same way at 4 and 8 threads and never at
  1; slice threads vary too.
- scenedetect 0.7.2 (OpenCV, 4 threads) varies the same way: 15-20 distinct stats files in 20 runs on corrupt.* (its
  cuts were stable in those 20 runs). Median rows differing: scenedetect against scenedetect 110, ours against ours
  111, ours against scenedetect 110. No threaded run of ours was byte-equal to any scenedetect run on corrupt.*; on
  truncated.ts our one-thread runs and 26-27 of 30 default runs equal scenedetect's most common output.
- OpenCV's thread count can't be set through `OPENCV_FFMPEG_CAPTURE_OPTIONS` (`threads;1` has no effect), only with
  `CAP_PROP_N_THREADS` through the API, and at one thread OpenCV numbers frames without the gaps 4 threads leaves (ours
  follow the 4-thread numbering at any thread count), so one thread on both sides wouldn't match either.
- CI compares base.mp4, truncated.mp4, truncated.ts, dup_pts.mkv and res_change.ts exactly with stats; corrupt.ts is
  informational and corrupt.mp4 isn't run. truncated.ts hasn't flaked on CI (likely the real trailer's cut leaves no
  frame concealed variably; unverified).

### Choices

1. **Best effort, documented** (recommended): damaged files vary from run to run in both tools; say so, and point to
   `--threads 1` for reproducible results.
2. **One thread on errors**: decode with one thread when a file turns out damaged (reproducible, but not
   scenedetect's numbering, and slower).
3. **Compare cuts only**: promise identical cuts on damaged files and leave the stats out.

### Answer

**1. Best effort, documented.** The variation comes from FFmpeg's threaded error concealment, which scenedetect has
too, so no setting makes both tools repeat each other there. README's damaged-video paragraph says so and names
`--threads 1` (`DetectionOptions.DecodeThreads = 1`) for reproducible results, identical in-process and through the
executable though not necessarily scenedetect's; the 2026-10-11 decision got a note; CI stays as it is (if truncated.ts
ever flakes, it becomes informational).

### Also asked

Add a Maybe row for the duplicate-timestamp frame on corrupt.ts? (**Yes, Maybe row** (recommended) or No.) Answer:
**Yes**. On the stand-in's corrupt.ts the decoder outputs two frames with the same pts (291); OpenCV numbers both, we
keep one, so our cuts come one frame later and ~236 stats rows differ, at any thread count and on both decoder paths.
The row says to check first whether it happens on CI's real samples.

### Note (2026-10-27): damaged MPEG-1/2 and one thread

Measured for the backlog row "Damaged MPEG-2 decodes differently from run to run with slice threads": two causes.
Slices raced between threads (a damaged picture's slices write rows in another thread's band; the thread count alone
changes the result: 8 threads differ from 4 on the same file), and leftover frame buffers (macroblocks never written or
concealed keep an earlier frame's pixels, and which buffer comes back depends on when frames are released on another
thread: always through the ffmpeg executable, and in-process when converting on a separate thread). So `--threads 1`
repeats only in-process with the conversion on the decoding thread; there it equalled scenedetect at one thread on all
four damaged MPEG-1/2 test files. scenedetect at its default threads gave 2 to 6 distinct stats CSVs in 8 runs on the
same files, and our default runs usually equalled one of them. Making MPEG-1/2 always repeat in-process (one thread,
converting on the decoding thread) would cost undamaged MPEG-2 about +28% chunked on 1080i and 2.1x single-pass; one
thread only after a corrupt frame needs FrameDecoder to report corrupt frames and a second pass. Asked (choices: A,
document and stay best effort; B, one thread for MPEG-1/2; C, one thread only once damaged), the owner chose **A**
(2026-10-27): the README's claim that one thread gives repeatable results, identical through the executable, is
corrected; nothing else changes.

The duplicate-pts frame on corrupt.ts: [2026-10-28-damaged-frame-pts.md](2026-10-28-damaged-frame-pts.md).
