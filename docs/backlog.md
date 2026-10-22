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
  - Status: Open (2026-10-19: on x86 one known, stats only: a video whose colour tags change midway, with FastYuv or, on a 10-bit siting change, the executable's default path (every cut exact; the per-frame-tags row in Maybe, the informational samples sintel_matrix_change.mkv and sintel_10bit_siting_change.mkv); on ARM the default results differ by FFmpeg's conversion rounding, documented in docs/decisions/2026-10-17-arm-results.md, while ShotDetector.FastYuv gives x86's results on 8-bit 4:2:0 and 4:2:2 and 10-bit 4:2:0 of even width, and MPEG-1/2, MJPEG and MPEG-4 Part 2 decode as on x86)

- Decoding apart from conversion and deinterlacing (another thread)
  - Why: Profiled 2026-10-21: one producer thread decodes, deinterlaces and converts in series; on MPEG-2 it is ~80% busy with ~35% of its time serial work while the slice threads idle, and the scoring thread is 85-90% idle. Handing reference-counted frames (`TakeFrame`) to a conversion thread, with row yadif's previous/current/next frames kept alive, should take sequential 1080i MXF from 16.6 s to about 11-12 s (decode-only floor ~10.5 s) and help 10-bit h264, where swscale is 88% of the producer. docs/frame-references.md describes K converter workers, which ShotDetector doesn't use yet, and names the obstacle (row-wise deinterlacing stays on the decoding thread). Medium; a decision record on the design first (docs/decisions/2026-10-22-decode-split.md)
  - Status: Open: C done (#471, `ConvertThreads`, hidden `--convert-threads 1`, default off): one conversion thread taking refcounted frames where the decoder isn't in row-yadif mode, byte-identical. Measured 2026-10-22 on 4 vCPUs (min of 3, CLI with stats, FFmpeg's yadif for interlaced): sequential 1080i MXF 4:2:2 36.2 -> 33.1 s (1.09x), 1080i TS 4:2:0 27.4 -> 23.6 s (1.16x), 1080p h264 20.3 -> 18.5 s (1.10x), 1080p 10-bit h264 30.3 -> 25.2 s (1.20x); parallel 1.00-1.07x; 360p 1.00-1.03x; 2 CPUs (taskset) 1.06-1.13x on HD; CPU time the same, RSS 1-23 MB more. Row-yadif runs (FastYuv's Yadif) are unchanged. To decide: turning K=1 on (e.g. for sequential runs of HD), and B (FrameReader deferred take for row yadif, then K workers)

## Waiting

- Parallel detection above 8 CPUs: more than 4 chunks and 16 decoder threads in total
  - Why: `AutoChunks` = min(4, CPUs / 2, whole minutes) and `DefaultDecodeThreads` = min(CPUs, 16): on 16 CPUs 4 chunks of 4 threads, on 32 still 4 x 4. A chunk keeps about 1.5-1.75 cores busy (profiled on 4 vCPUs), so by that estimate half of 32 cores sit idle. Not measured: the weekly benchmark only runs on 4-vCPU runners, so measure first on a larger machine (the owner's 16-thread desktop, asked for, or a larger runner), then raise the caps if it pays; results stay identical (CI compares chunked with sequential)
  - Status: Waiting (on a measurement above 8 CPUs)

- At the next FrameReader release, count the commits touching both products again (docs/frame-reader-product.md, "When to reconsider")
  - Why: 2.8.0 counted 1 of 21, under 1 in 20 for the first time; the trigger needs two releases in a row over at least 20 counted commits, and then means asking the owner, not splitting. 2.9.0 counted 0 of 6 and 2.10.0 0 of 4 (0 of 10 joined), too few: the window joins the next release's
  - Status: Waiting (on the next FrameReader release)

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

- The ffmpeg executable's seek with deinterlacing: land before the wanted frame
  - Why: Found 2026-10-22 (the TS chunk fallback): on interlaced MPEG-TS with `--deinterlace`, a seek for a keyframe's frame (a time range's start) lands on that frame itself, so yadif has no previous frame for it and its pixels differ from a full read's (RangeTests' interlaced-keyframe test fails with `VideoDecoder.FfmpegProcess`). The in-process path reads again from further back since then; the executable's select filter drops the frames before the wanted one unseen, so it would need another sign (a showinfo before the select, or always seeking a GOP further back with yadif). Only the executable decoder with deinterlacing and a start time or seek
  - Status: Maybe

- An end-to-end test of FastYuv's row yadif on a rotated interlaced clip
  - Why: #461 left it unproven beyond the poison test's geometry (`FastYuvTests.RowsOutsideTheRowsReadChangeNothing` covers each turn with crops): a rotated (90/270) interlaced clip, row yadif with FastYuv's converter and with the default one, against FFmpeg's yadif + transpose, scene and stats byte for byte
  - Status: Maybe

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
  - Why: The default path converts each frame from its own chroma siting and colour tags (as OpenCV's sws_scale_frame does); since 2026-10-19 the executable path's scale filter and FastYuv take the first frame's (`FrameDecoder.ChromaLocation`/`ColorSpace`/`ColorRange`, ffprobe's frame entries; docs/decisions/2026-10-19-chroma-and-colour-tags-from-the-frame.md) for the whole video, so a video whose tags change midway still differs there. Measured on built clips (samples/informational/sintel_matrix_change.mkv, sintel_10bit_siting_change.mkv): FastYuv on a matrix or range change, and the executable's default path on a 10-bit siting change, differ in stats after the change only, with every cut exact and 0 CSV cells on every path since #432; not seen in a real file (docs/decisions/2026-10-19-per-frame-tags.md)
  - Options: B1, frames whose tags differ take the in-process BGR fallback (about 5 lines, no speed cost on x64, but ARM's rounding on those frames); B2, a FastYuv converter per tag set chosen per frame (30-60 lines, x86's results on ARM too); C, the executable path's FastYuv from showinfo's per-frame colour line (needs pairing two stderr lines, then B2's choice); D, the executable's default path on a siting change: ffmpeg's scale never follows a frame's siting, so only by restarting ffmpeg at the change (files only, a restart and a GOP per change)
  - Status: Maybe (the owner, 2026-10-19: parked)

- The first frame's colour tags read only when they'll be used
  - Why: The review of 3.10.0 (2026-10-19): ShotDetector reads the first frame's chroma location and colour tags once per detection (`FrameTags`: in-process FrameDecoder, else a one-frame ffprobe) also where only a fallback would use them (in-process without FastYuv, where BgrConverter converts each frame from its own tags), about 0.14 s on 4K 10-bit, and on a live rtsp URL it waits for the first frame before detection starts. Reading them lazily, when the executable path or FastYuv first needs them, would save both
  - Status: Maybe

- corrupt.ts: two decoded frames with the same pts — OpenCV numbers both, we keep one
  - Why: Found 2026-10-22 measuring damaged files (docs/decisions/2026-10-22-damaged-files-best-effort.md): on a stand-in clip's corrupt.ts (a 640x360 29.97 fps Sintel trailer through tools/make-broken.py; the real trailer couldn't be downloaded), the decoder outputs two frames with the same pts (291). OpenCV numbers both (the first as frame 237 at 7.875 s), ShotDetector keeps one, so our cuts come one frame later (09.643 against 09.610) and ~236 stats rows differ; deterministic, at any thread count and on both decoder paths. It is the "frame 236 at 8.24 s, expected 7.87 s" of a chunk fallback message. Check first whether it happens on CI's real samples (corrupt.ts is informational there)
  - Status: Maybe (the owner, 2026-10-22: Maybe row)

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
