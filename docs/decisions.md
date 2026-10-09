# Decisions

Design questions that came up while building ShotDetector, the choices considered, and what the user decided.
Newest first. Add an entry whenever a design question is put to the user.

## FrameReader speed-ups for extracting pictures (2026-10-09)

The user asked whether a picture every second (ffmpeg's fps) could be extracted faster, then to try the options. Measured
on Tears of Steel (12 min) as is and re-encoded with x264 defaults: ffmpeg's decoder tricks (keyframes only 10-15x less
CPU but different pictures; skipping deblocking -25%, different; D3D11VA hardware decoding 2.5x less CPU and identical
pictures on the user's GPU) and FrameReader's exact ways (seeking to each picture 2.6-9x less CPU at one per 10 s, worse at
one per second; 4 parallel segments the same CPU in 2-2.5x less wall time). Asked which to build: **all four** (smart skip
in TryReadForwardTo, recommended; parallel segments; a KeyframesOnly mode, documented as approximate; opt-in hardware
decoding on Windows). Hardware decoding thereby replaces the earlier "skip for now" (below).

## FrameReader's audio; no hardware decoding for now (2026-10-09)

Asked before milestone 6's audio:

- API: **an AudioReader class (recommended)**, like VideoFrameReader (TryRead, Seek), over one MediaReader
  interleaving video and audio.
- Samples: **float32 at a chosen rate and channel count (recommended)**, the samples of `ffmpeg -ar R -ac C -f f32le`
  (checked byte for byte, seeking included), over also offering int16.
- Hardware decoding: **skip for now (recommended)**, over an opt-in that may differ from software decoding: worth it
  only if bit-exact, which needs proving per codec and GPU, and CI runners have no GPUs.

## FrameReader's pixels and formats (2026-10-09)

Asked before building its public frame API (VideoFrameReader):

- Pixels: **the same bytes as ffmpeg's command line (recommended)** (`-vf scale=W:H:flags=bicubic,format=F`), over
  "correct, not exact" and "both, selectable". Checked byte for byte in its tests; it mirrors the scale filter: the
  input read as progressive, its chroma location replaced by unspecified (in_chroma_loc), the output's tags those the
  link negotiates.
- Formats in the first version: **Bgr24/Rgb24, Gray8, Bgra32/Rgba32 and Yuv420p** (all offered).

## Frame-reader library: name, packages, publishing (2026-10-08)

Asked before starting the split (docs/frame-reader-library.md), recommended option first each time:

- Name: **FrameReader** (project, namespace; package id later), over ShotDetector.Video and FastFrames.
- Native libraries: **keep ShotDetector.Native** (the library loads from the same files), over new
  FrameReader.Native.<rid> packages.
- Publishing: **internal until the API settles** (ShotDetector's package carries FrameReader.dll), over a prerelease
  package from the start.
- **Build our own** over contributing to FFMediaToolkit (FFmpeg 7, no macOS, no native packages).

## ShotDetector's API may change for the frame-reader split (2026-10-08)

While analysing the frame-reader library (docs/frame-reader-library.md), the user: the frozen API "isn't that
important yet. No one is using this but me, yet." So the extraction may change ShotDetector's public API where that
makes the split cleaner (a 2.0.0 under semantic versioning, with package validation's baseline moved); its results
must still not change.

## The public API for 1.0 (2026-10-08)

After 0.10.0 the user chose "1.0 preparation (Recommended)" over exact 10-bit colour, scenedetect CLI parity and more
stream checks, then answered the API review:

- Positional records (VideoInfo, DetectionResult, DetectionProgress): **init-only properties (recommended)** over
  keeping them positional, so new fields after 1.0 aren't breaking. Shot and Fps stay positional (complete).
- VideoReader: **a read-only result view (recommended)** (constructors and Frames() internal) over keeping it all public.
- Stats.Set: **internal (recommended)** over public.
- Breaking-change guard: **SDK package validation (recommended)** over PublicApiAnalyzers or both. The baseline is
  empty until 1.0 is on nuget.org, then the last stable version (`ApiBaselineVersion`).

## Streamed mp4 without faststart: spill only those (2026-10-08)

Question: streamed mp4s with their headers (moov) last can't be read until the whole stream has arrived; how should
`Detect(Stream)` / `DetectStreamAsync` handle them? Choices: spill only those to a temporary file (recommended), spill
every stream (as fast as a file path, but no shots until the stream is in, always disk), or an opt-in option.
Answer: **spill only those**. Detected from the first bytes (an mdat box before the moov); other streams keep streaming.

## Row-wise yadif: in FastYuv, 9-16 bits follow FFmpeg's C code (2026-10-08)

Faster deinterlacing was the user's pick after 0.9.0 ("Faster deinterlacing (Recommended)"); when it first lost to
FFmpeg's threaded yadif on many cores, the user chose "Keep digging" over shelving it or landing it opt-in for small
machines (the cause turned out to be side data piling up on the output frame). The port is of FFmpeg's LGPL code, so
it lives in ShotDetector.FastYuv behind `IYadif`, like the yuv420p converter. FFmpeg 8.1's x86 SIMD yadif is
nondeterministic for 9 to 16 bits (a different output on every run of the same command); 8-bit SIMD equals the C
code. The port follows the C code, so 10-bit results are deterministic and equal `ffmpeg -cpuflags 0 -vf yadif`.

## Interlaced video: not reproducing OpenCV's black frames (2026-10-08)

Found by the broadcast samples (issue #42): on interlaced video, opencv-python 5.0.0.93's FFmpeg 8 swscale refuses
the conversion ("Cannot convert interlaced to progressive frames or vice versa") and OpenCV returns the same nearly
black image (min 0, max 97, mean 0.0) for every frame; ffmpeg decodes the same files correctly. Progressive video is
pixel-identical. Reproducing it would mean analysing black frames, so ShotDetector reads the real ones: a documented
difference (README), compared informationally in CI with deinterlacing off and exactly with Auto against a lossless
yadif copy. Decided during unattended work (the user was away), as the only sensible behaviour; open to revisit.

## 1.0 without a quiet period (2026-10-08)

The user: "We don't need any quiet weeks. No one is using this yet, so I think we should just focus on fixing things
pr testing it - that will make things stable for a 1.0." This replaces "after a quiet 0.7 (two weeks, no API change)"
in the 1.0 plan: 1.0 comes when the known issues are fixed and the checks are green. The versioning policy after 1.0
is unchanged.

## Issue #42: in-process deinterlacing, decoder visibility, probe scope, broadcast samples, 1.0 (2026-10-07)

### Question

After adopting 0.8.0 the same user asks for: deinterlacing in-process (15.2 s vs 3.9 s on an 80 s 1080i MXF, 2 CPUs);
seeing which decoder ran from DetectStreamAsync, a distinct error and a startup check for missing libraries; the
container name and audio presence in the probe (and passing the probe back); interlaced broadcast formats in CI; and
1.0. Which parts, and how do they line up with 1.0?

### Choices

- Which: in-process deinterlacing; decoder visibility + fail fast; probe container + audio; broadcast samples in CI.
- 1.0: the API additions in 0.9.0, then freeze; or freeze now and the additions in 1.1.
- Reply: when it ships; draft for approval.

### Answer

**All four; the API additions in 0.9.0 (with the 0.6.4 removal and ARM), then two quiet weeks to 1.0; reply when it
ships.** In-process deinterlacing and the CI samples change no API, so they can land during the freeze. Passing the
probe back stays out (detection's cost is the packet scan).

## Dropping scenedetect 0.6.4 compatibility (2026-10-07)

The user: "we can drop the 0.6.4 compatibility now". Removed `PySceneDetectVersion.V0_6_4` and every 0.6.4 code
path (frame-number positions, whole-number downscale, fade split, histogram re-initialisation, save-images frame
picks), its tests, mutation guard and CI parity jobs. Kept the `Compatibility` option and the enum with
`V0_7_1`: the versioning policy keeps the previous release available when the default follows a newer scenedetect.
Breaking, so in 0.9.0, and the two quiet weeks before 1.0 restart.

## Issue #35: a public probe and automatic deinterlacing (2026-10-07)

### Question

A worker probes each file with ffprobe for the field order (to set Deinterlace), then ShotDetector probes it again.
It asks for a public probe result (field order included) that detection could take back, an Auto deinterlace mode,
and possibly audio and container details. What fits, and when?

### Choices

- Scope: Auto deinterlace + a header-only public probe (no pass-back, no audio); Auto deinterlace only; everything
  asked; a reply only.
- Timing: 1.1 after 1.0 (keeps the quiet period); now as 0.8 (restarts it).
- Reply: draft for approval; post when it ships.

### Answer

**Auto deinterlace + a public probe, now as 0.8, reply when it ships.** Passing the probe back saves little: the
costly part of detection's probe is reading every packet's timestamp, which a header probe doesn't do. Deinterlace
became an enum (Off/On/Auto), a breaking change before 1.0; the two quiet weeks before 1.0 restart with 0.8.0.

## ShotDetector.Native: where the libraries come from, packaging, CLI, platforms (2026-10-07)

### Question

How should FFmpeg's shared libraries ship, so in-process decoding works without installing them?

### Choices

- Source: our own minimal LGPL build (decoders, demuxers, swscale) in CI; BtbN's LGPL builds (Windows and glibc
  Linux only, large).
- Packaging: one package per platform; one package for all.
- CLI: the tool and binaries bundle them; library only.
- Platforms: win-x64, linux-x64 + linux-arm64, linux-musl-x64, osx-arm64.

### Answer

**Our own minimal build, one package per platform, bundled with the CLI, all five platforms.** No AV1 software
decoder (dav1d is an external library), as in OpenCV on Linux; Auto falls back to the executable for it.

## The 1.0 plan and versioning (2026-10-07, issue #18)

### Question

When should 1.0 ship, how does the default follow a scenedetect release that changes results, and what counts
as breaking afterwards?

### Choices

- When: after a quiet 0.7 (two weeks, no API change, nightlies green); right after 0.6.1; when the native
  libraries package lands.
- New scenedetect: a new mode in a minor with the default switched in a major; or the default follows the latest
  in a minor.
- Breaking: API and default results; or API only.

### Answer

**After a quiet 0.7. The default follows the latest scenedetect in a minor. API and default results are
breaking**, except changes that come from following scenedetect. README "Versions" has the policy.

## After 0.6.1: defaults, native libraries, 10-bit, 1.0 (2026-10-07)

### Question

With in-process decoding faster than scenedetect (Sintel, Tears of Steel) or within 4% (Big Buck Bunny) at half
the default path's memory, while the default ffmpeg-executable path stays 1.2-2x slower on 2 CPUs: what next?

### Choices

1. In-process by default when FFmpeg 8.1's libraries are found, the executable otherwise (reverses the opt-in).
2. A `ShotDetector.Native` package (LGPL) carrying FFmpeg 8.1's shared libraries per platform.
3. An experiment: does the in-process converter (OpenCV's legacy swscale interface) make 10-bit video exact?
4. A 1.0 plan (issue #18).

### Answer

**All four**, after 0.6.1, in that order.

## In-process decoder threads (2026-10-07, issue #12)

### Question

Measured on Linux with 2 CPUs (Sintel 1080p, Big Buck Bunny, Tears of Steel; best of 3, one runner): in-process
with 1 decoder thread used the least CPU (below scenedetect on two films) but took 1.2-1.9x scenedetect's time;
2 threads cut wall time 10-23% for 30-48% more CPU (within 4-13% of scenedetect on two films). Decoding ahead on a
task gave no more speed than a second decoder thread for more CPU, so it was dropped. Which default?

### Choices

1. **All CPUs**, as OpenCV does.
2. One fewer than the CPUs, as the executable path (least CPU).

### Answer

**Choice 1.** `--threads 1` remains for the lowest CPU. Big Buck Bunny stays ~1.7x scenedetect's time in-process
(backlog).

## Issues #16-#19: deinterlacing, exceptions, supply chain, tracing (2026-10-06)

### Question

Four requests from a production user: deinterlace interlaced sources before analysis (#16), exception types that
tell a missing ffmpeg from bad input (#17), supply-chain signals (#18: signing, SourceLink, 1.0, a second
maintainer), and an OpenTelemetry `ActivitySource` (#19). Which to do, in what shape, and how to sign?

### Choices

- Which: #17, #19, #18 (SourceLink and symbols), #16.
- Deinterlacing: a `Deinterlace` option (yadif); a free-form ffmpeg filter string; both.
- Signing: GitHub build attestations (free, `gh attestation verify`); author-signed NuGet packages (a paid
  certificate); none for now.
- Issue replies: drafted for approval; posted when merged; none.

### Answer

All four issues. **A `Deinterlace` option** (narrow and testable; a free-form filter could change frame counts
and timing). **Build attestations.** **Replies posted when the work merges.** The 1.0 date and a second
maintainer stay with the user.

## In-process decoding (2026-10-06, issue #12)

### Question

On Linux with 2 CPUs ShotDetector used ~1.5x scenedetect's CPU, two thirds of it in the ffmpeg command line. A
prototype decoding in-process with FFmpeg.AutoGen (MIT bindings, FFmpeg 8.1 libraries) gave byte-identical pixels for
40% less CPU than the command line (1 thread), and OpenCV's speed with 2 threads. But it needs FFmpeg's shared
libraries in the version the bindings were built for. How should it ship?

### Choices

1. **Opt-in backend**, the ffmpeg-executable pipeline stays the default and the fallback.
2. Automatic when compatible libraries are found.
3. In-process only.
4. Not now.

### Answer

**Choice 1.** It lives in the core package (the bindings are MIT); inputs it doesn't handle yet fall back to the
ffmpeg executable.

## API freeze toward 1.0 (2026-10-06)

### Question

Which parts of the public API should 1.0 promise?

### Choices and answers (all four as recommended)

1. **Detector classes, `IDetector`, `ContentScorer`, `EdgeDetector`: internal.** Users go through
   `ShotDetection` with `DetectorKind`; internals stay free to change. (Alternative: keep public as "advanced".)
2. **`VideoReader`: trimmed to the essentials.** `FrameAt`, `SeekFrame`, `PositionAfterDecoding`, `FrameCountHint`
   internal. (Alternatives: hide it behind a VideoInfo record; keep as is.)
3. **`DetectAsync` added** next to `Detect`. (Alternative: callers wrap `Detect` in `Task.Run`.)
4. **`FrameTime`'s raw fields hidden**; `ToString()` is the timecode, `FrameTime.Frame` the one public factory.
   (Alternative: keep as is.)

## After the robustness checks (2026-10-06)

### Question

Every decode path tried matches scenedetect (after the MPEG-PS fix), and profiling showed large frames are bound by
ffmpeg's decoding, not by our code. What next?

### Choices

1. Speed on large frames (decode threads, pipe bytes, probing).
2. Missing scenedetect features (`-f/--framerate`, min-scene-len as time, export-html, list-scenes/split-video options).
3. API freeze and docs for 1.0.
4. Broken-file robustness (corrupt packets, duplicate timestamps, stream switches).

### Answer

**All four.** Order: 1, 2, 4, then 3 last, since the new features touch the API.

## Working setup for unattended work (2026-10-06)

### Question

The user wants the project to keep moving overnight. What of the StyleBro repository's setup should be copied?

### Choices

1. `CLAUDE.md` with status and gotchas, `docs/backlog.md`, `docs/decisions.md`.
2. PR pipeline with a protected `main`.
3. Real-world workflow: public videos vs scenedetect, nightly.
4. Mutation testing of the scenedetect-quirk guards.

### Answer

**All four.** And the backlog's order: robustness on real videos, then performance, then the road to 1.0, with
0.4.0 released first.

## Streamed input (2026-10-06)

### Question

Users stream shots out; can the video come in as a stream too, and should URLs and live feeds be supported?

### Choices

1. Stream in → shots out (`Detect(Stream)`, `DetectStreamAsync(Stream)`, CLI `-i -`).
2. URLs and live feeds (ffmpeg reads them; one pass, timestamps from `showinfo`, no seeking).
3. Only files.

### Answer

**1 and 2.** Container headers must come first (mkv/webm/ts/mov, faststart mp4); a plain mp4 gets a clear error;
`StartTime` is refused when streaming; exports need a path.

## Releases (2026-10-03 – 2026-10-06)

The user says when to release ("when everything is ready, push a 0.3.0 release"; "Release 0.4.0 now"). Stable
tags wait for the user's approval of the `release` environment; prerelease tags publish at once.

## Remaining scenedetect options (2026-10-05)

Picked from the backlog: API review toward 1.0, repo polish, and the remaining scenedetect options (`time`,
`--frame-skip`, `--crop`, image sizing). Decisions inside: the end of a range is compared as frame numbers
(scenedetect's `FrameTimecode` falls back to that), VFR seeking takes the first frame at or after half a frame
before the target (OpenCV's own seek is an artifact, 0.4 s off on the 24→48 fps clip).

## Hash detector exactness (2026-10-04)

`detect-hash` matches except on flat frames, where the hash is DCT rounding noise in scenedetect as much as here.
**Decision:** keep it, compare it informationally in CI (no failure), document it.

## scenedetect 0.6.4 on Linux (2026-10-03)

Its `ThresholdDetector` is nondeterministic on Linux (the decode thread can hand a frame over with the wrong frame
number). **Decision:** the CI compare for 0.6.4 covers content and adaptive (and histogram) only; all three matched
on Windows.

## Colour conversion with FFmpeg 8 (2026-10-02)

FFmpeg 8+ ignores `in_color_matrix`, so OpenCV built on it follows the stream's colour tags, and so does the
scenedetect reference on Linux. **Decision:** follow the colour tags (BT.601/709, limited/full range) like
OpenCV + FFmpeg 8; the Windows OpenCV wheel (FFmpeg 7.1) differs on BT.709 and is documented, not matched.

## Versions to support (2026-10-02)

"It's nice if our code can run against multiple versions, but we don't have to manage multiple versions."
**Decision:** test against the latest scenedetect (0.7.1) and the latest stable FFmpeg (9.0, BtbN build); add
0.6.4 as an explicit compatibility mode since it was asked for.

## CLI and licensing (2026-10-01 – 2026-10-03)

### Question

The swscale port (yuv420p→BGR, exact) is LGPL; the rest is MIT. How to ship?

### Choices

1. Everything LGPL.
2. MIT core `ShotDetector` + separate LGPL `ShotDetector.FastYuv` package; the CLI skipped for now.
3. No fast path at all.

### Answer

**2** ("Let's do 2 then"); the CLI was shipped later (2026-10-03) as a dotnet tool and AOT binaries built
without the LGPL package (a dev-only `WithFastYuv` build exists for the compare script).

## Exactness vs speed (2026-10-01)

"Yes, do that for now. Better to have a correct baseline." **Decision:** bit-exact ports of OpenCV's operations
are the default path; faster approximations (`FfmpegResize`) are opt-in and documented as differing.
