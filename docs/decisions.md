# Decisions

Design questions that came up while building ShotDetector, the choices considered, and what the user decided.
Newest first. Add an entry whenever a design question is put to the user.

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
