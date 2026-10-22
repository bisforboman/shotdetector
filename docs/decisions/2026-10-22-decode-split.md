# Decoding on its own thread: C first, then decide (2026-10-22)

### Question

How should we proceed with decoding on its own thread?

The in-process reader's producer thread decodes, deinterlaces and converts each frame in series (backlog row "Decoding
apart from conversion and deinterlacing", profiled 2026-10-21). Three ways to split it were drafted:

- **A**: one conversion thread, with a FrameReader change: a deferred take that keeps row yadif's previous, current and
  next frames referenced, so the conversion thread computes only the rows it reads, deinterlacing included.
- **B**: K conversion workers on top of A.
- **C**: no FrameReader change: where the decoder isn't in row-yadif mode (FFmpeg's yadif, progressive 8- and 10-bit,
  oriented frames), the decoding thread takes refcounted frames (`FrameDecoder.TakeFrame`) and hands them to one
  conversion thread that owns the FrameWriter; row-yadif runs keep today's loop (taking a frame there would
  deinterlace every row, the regression #461 removed). An internal `DetectionOptions.ConvertThreads` and a hidden CLI
  `--convert-threads N` switch it on, default off until measured.

Expected: row-yadif MXF sequential (16.6 s) to about 12-13 s only with A or B; 10-bit h264 about 1.1x with one thread,
more with K = 2; parallel mode already keeps the cores busy, so little there.

### Choices

1. **C first, then decide** (recommended): build C, measure, and decide on A/B with the numbers.
2. **B in full**: the FrameReader change and K workers now.
3. **Park it (Maybe)**: move the row to Maybe.

### Answer

**1. C first, then decide.** C was built in #471, byte-identical with and without the conversion thread (tests on
every pipeline). Measured on 4 vCPUs (min of 3, the CLI with stats, FFmpeg's yadif for the interlaced inputs): one
conversion thread made sequential runs 1.09x (1080i MPEG-2 4:2:2 MXF), 1.16x (1080i 4:2:0 TS), 1.10x (1080p h264) and
1.20x (1080p 10-bit h264) as fast, parallel runs 1.00-1.07x, and 360p 1.00-1.03x (no slowdown); on 2 CPUs 1.06-1.13x
on HD; CPU time unchanged, RSS 1-23 MB more. The default stays off in #471; whether to turn it on, and whether B (A's
deferred take for row yadif, with K workers) is worth a FrameReader change, is the owner's next decision (backlog row).

## The default, and B (2026-10-22)

### Question 1

Should the conversion thread be on by default?

### Choices

1. **On for sequential HD** (recommended): one conversion thread by default where a run reads sequentially in-process
   and the frames are HD; off for parallel runs (their chunks keep the cores busy) and smaller video.
2. **Measure on my desktop first**: keep it off until the owner's 16-thread Ryzen has timed it (the 2026-10-17
   conversion-worker trial there was 1.04-1.07x slower on Big Buck Bunny).
3. **Leave it off**: keep it as the hidden switch.

### Answer

**1. On for sequential HD.** As built: `DetectionOptions.ConvertThreads` unset (null) means
`VideoReader.DefaultConvertThreads`: 1 for frames of at least 1280 x 720's pixels (the probed coded size, before
rotation, so 720 x 1280 portrait and HD stored sideways count; 1280 x 534 scope doesn't), else 0. Sequential means a
file read without chunks, a stream, a URL or `DetectStreamAsync`, and the read after a chunk fallback; a parallel run's
chunks get 0 (`ShotDetection.ChunkOptions`). An explicit value (the option, the hidden `--convert-threads N`) wins; row
yadif and the executable path convert on the decoding thread whatever the value. The owner's desktop check is optional
(the PR's body has the commands); the default ships unless it shows a slowdown.

### Question 2

What about option B (A's deferred take in FrameReader, so row yadif's conversion and deinterlacing move off the decoding
thread too, then K workers)?

### Choices

1. **Park it** (recommended): a Maybe row; it only helps the library configuration with FastYuv's row yadif.
2. **Build B**: the FrameReader change and K workers now.

### Answer

**1. Park it.** Backlog row "Option B: a FrameReader deferred take so FastYuv's row yadif converts off the decode
thread" (Maybe): expected 1080i MXF sequential with row yadif from ~16.6 s to about 12-13 s, little in parallel mode.
