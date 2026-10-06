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
| Telecined footage, VP9/AV1 in webm/mkv, mpeg4 in avi, fragmented mp4, image sequences (`%04d.png`) | More decode paths | Done (2026-10-06): all exact |
| 10-bit video on Linux: ±1 in red/green on some pixels vs OpenCV's bundled FFmpeg (cuts identical) | No ffmpeg CLI option reproduces it; an exact match needs our own 10-bit→BGR conversion in C# (like FastYuv for 8-bit). Compared informationally in CI | Maybe |
| AV1 on Linux: OpenCV's wheel can't decode it (0 frames), so nothing to compare against there | Matches on Windows; excluded from the Linux real-world matrix | Done (2026-10-06) |
| Damaged files (`tools/make-broken.py`): corrupted mp4/TS, truncated mp4/TS, duplicate timestamps, a resolution change | Found and fixed: frames after a decoder-dropped frame were mislabeled; a piped TS not starting at 0 was shifted. All identical to scenedetect now, except the resolution change, where OpenCV freezes on the last frame before it (documented, not reproduced) | Done (2026-10-06) |
| Damaged data: concealed pixels can differ from OpenCV's (decoder threading), so stats differ a little on corrupt files | Matching would need OpenCV's exact decoder threading setup; cuts matched on every damaged file tested | Maybe |
| Container edge cases on streamed input: fragmented mp4, HLS/DASH segments, RTSP | The streaming mode relies on ffmpeg's demuxer on a pipe; verify timestamps come out right | Open |
| Unit test for scenedetect 0.6.4's histogram re-initialisation at frame 1 | A guard reproduced without a test; add it and a mutation | Done (2026-10-06) |
| Windows OpenCV wheel (FFmpeg 7.1) colour difference on BT.709 | Documented; decide whether to offer a `ColorMatrix` override for users comparing on Windows | Maybe |

## 2. Performance

Measured 2026-10-06 (dotnet-trace thread sampling plus interleaved best-of-3 timings, Tears of Steel 1280x534):
our resize and scoring are ~4% of sampled thread time, and a full run takes 3.8 s against 2.8 s for ffmpeg
decoding and converting the same frames into nothing. On large frames both tools are bound by decoding, so
faster C# can't help there; what is left is the pipe (~0.5 s per 2 min), startup with ffprobe, and decode threads.

| Item | Why | Status |
|---|---|---|
| Benchmark script and CI job (`tools/bench.py`, `benchmark.yml`, weekly and on demand): fps and peak memory vs scenedetect on the films | Desktop timings vary by ±50% between identical runs; claims need numbers from a quiet runner | Done (2026-10-06) |
| README performance section from the CI numbers, including memory | Users choosing between the tools want numbers | Open |
| Decode threads: 8 by default on the yuv420p fast path (decode-bound: 13% faster on HD, +70 MB at 1080p, user's choice); the core path stays at 4 (pipe-bound: more threads don't help) | Biggest lever that keeps exact results | Done (2026-10-06) |
| Pipe bytes on the core path: it pipes full-size BGR (3 bytes per pixel) although the exact resize reads ~1/5 of the pixels; ffmpeg has no filter that keeps exactly those rows and columns, and converting yuv420p in C# is what the LGPL FastYuv package is | The core path's bottleneck on HD (~15%); FastYuv is the answer today | Maybe |
| Probe cost: scanning packets in the background while ffmpeg decodes | Measured no gain (the end check needs timestamps from the first frame; short files scan fast); reverted | Dropped (2026-10-06) |
| SIMD in the resize/HSV/score loops | ~4% of the time; not worth it until the above are done | Maybe |

## 3. Toward 1.0

| Item | Why | Status |
|---|---|---|
| API freeze review: naming, nullability, records vs classes, what is `internal` | Minor versions may still change the API until 1.0 | Open |
| Docs pass: README sections per use case (library, CLI, streaming), XML docs on every public member, samples folder with a small app | First impression for NuGet users | Open |
| Frame-rate override (`-f/--frame-rate`, incl. image sequences): `DetectionOptions.FrameRate`, CLI `--frame-rate`, exact on video at 30/29.97/23.976/12.5, image sequences and 0.6.4 | Image sequences and broken headers need it | Done (2026-10-06) |
| split-video options (`--copy`, `--high-quality`, `--rate-factor`, `--preset`, `--args`, `--expand`): `SplitOptions`, CLI `--split-*`; 60 of 60 clips byte-identical to scenedetect's. Also fixed: `-sn` (subtitles dropped) in 0.7.1 mode too | Parity on the command surface | Done (2026-10-06) |
| save-html / export-html: `Shots.Html`, CLI `--save-html`; byte-identical pages (with and without thumbnails) | Parity on the command surface | Done (2026-10-06) |
| list-scenes `--skip-cuts`, `-q`; the CSV's `Timecode List:` row | Our CSV lacked scenedetect's first row | Done (2026-10-06) |
| scenedetect features still missing: save-images `--quality`/`--compression` (our images come from ffmpeg, so only approximately), split-video `--mkvmerge` (external tool), filename templates (`$VIDEO_NAME`...) | Parity on the command surface, not only on results | Maybe |
| 1.0 release plan: what must hold (parity matrix green for N releases of scenedetect/ffmpeg), version policy afterwards | So 1.0 means something | Open |

## Done

| Item | Landed |
|---|---|
| Streamed input: `Detect(Stream)`, URLs, CLI `-i -` | 0.4.0 |
| Time range, frame skip, crop; image sizing options | 0.4.0 |
| Histogram and hash detectors; API review; repo polish | 0.3.0 |
| DetectStreamAsync, cancellation, progress, FfmpegDirectory, CLI tool + AOT binaries, Alpine, rotation | 0.3.0 |
| 0.6.4 compatibility mode, VFR, edges (`delta_edges`), exports, FastYuv package, release pipeline | 0.1.0–0.2.0 |
