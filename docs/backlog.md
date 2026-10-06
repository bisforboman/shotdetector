# Backlog

What ShotDetector aims to do next, in order (the user's priorities, 2026-10-06: robustness on real videos first,
then performance, then the road to 1.0). Update a status when work starts or lands; add an item when a divergence
or a gap turns up, with why it matters. Design questions go to [decisions.md](decisions.md).

Status: **Released** (on nuget.org), **Done** (on `main`, in the next release), **In progress**, **Open**, **Maybe**.

## 1. Robustness on real videos

| Item | Why | Status |
|---|---|---|
| Real-world check: whole Blender films and codec clips vs scenedetect (`tools/realworld.py`, nightly) | Synthetic clips and trailers can't show divergences on hours of footage, fades, dark scenes, or in HEVC/VP9/AV1 decoding | Done (2026-10-06) |
| Fix every divergence the real-world check finds | The promise is identical results | Open (none known; MPEG-PS timestamps fixed 2026-10-06) |
| More sources: interlaced, 10-bit (h264/hevc), odd sizes (1366x768, 427x241), 4:2:2, long GOPs, MPEG-2 in PS/TS | Each is a different decode/convert path | Done (2026-10-06): all exact after the MPEG-PS fix; `.mpg` and 10-bit samples added to the parity set |
| Telecined (pulldown) footage, broken/duplicate timestamps, image sequences | Not covered yet | Open |
| Container edge cases on streamed input: fragmented mp4, HLS/DASH segments, RTSP | The streaming mode relies on ffmpeg's demuxer on a pipe; verify timestamps come out right | Open |
| Unit test for scenedetect 0.6.4's histogram re-initialisation at frame 1 | A guard reproduced without a test; add it and a mutation | Done (2026-10-06) |
| Windows OpenCV wheel (FFmpeg 7.1) colour difference on BT.709 | Documented; decide whether to offer a `ColorMatrix` override for users comparing on Windows | Maybe |

## 2. Performance

| Item | Why | Status |
|---|---|---|
| Benchmark script (`tools/bench.py`): fps and peak memory vs scenedetect on the real-world films, numbers in the README | Claims need numbers that are reproduced in CI | Open |
| Profile the exact path: resize + HSV + diff per frame; SIMD (`Vector256`) where it keeps bit-exactness | Current speed-up comes from threads and the yuv420p sampled path; the scoring loop is scalar | Open |
| Pipe throughput: larger pipe buffers on Linux/macOS, avoid the copy into the frame buffer | Decoding is often the bottleneck for small frames | Open |
| Startup: trim the ffprobe call when nothing but the frame list is needed; AOT size | Matters for the CLI on many short clips | Maybe |

## 3. Toward 1.0

| Item | Why | Status |
|---|---|---|
| API freeze review: naming, nullability, records vs classes, what is `internal` | Minor versions may still change the API until 1.0 | Open |
| Docs pass: README sections per use case (library, CLI, streaming), XML docs on every public member, samples folder with a small app | First impression for NuGet users | Open |
| scenedetect features still missing: `export-html`, `--min-scene-len` as a time, `list-scenes` `-q`/`-s` options, `split-video` `--copy`/`--high-quality` args | Parity on the command surface, not only on results | Open |
| 1.0 release plan: what must hold (parity matrix green for N releases of scenedetect/ffmpeg), version policy afterwards | So 1.0 means something | Open |

## Done

| Item | Landed |
|---|---|
| Streamed input: `Detect(Stream)`, URLs, CLI `-i -` | 0.4.0 |
| Time range, frame skip, crop; image sizing options | 0.4.0 |
| Histogram and hash detectors; API review; repo polish | 0.3.0 |
| DetectStreamAsync, cancellation, progress, FfmpegDirectory, CLI tool + AOT binaries, Alpine, rotation | 0.3.0 |
| 0.6.4 compatibility mode, VFR, edges (`delta_edges`), exports, FastYuv package, release pipeline | 0.1.0–0.2.0 |
