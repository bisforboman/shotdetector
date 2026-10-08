# ShotDetector

A C# port of PySceneDetect's shot/cut detection that gives **identical results** to `scenedetect` (cuts, scene
list CSVs, per-frame stats, timecodes digit for digit), decoding through an ffmpeg subprocess with no native
bindings. Repo: `github.com/bisforboman/shotdetector`. NuGet: `ShotDetector` (MIT), `ShotDetector.FastYuv`
(LGPL-2.1, a swscale port), `ShotDetector.Cli` (dotnet tool `shotdetect`, plus Native AOT binaries on releases).

## Core idea

Exactness over speed ("Better to have a correct baseline", user, 2026-10-01): every pixel operation is a bit-exact
port of what OpenCV/scenedetect do (BGR→HSV fixed-point tables, `cv2.resize` INTER_LINEAR/INTER_AREA, Canny+dilate,
calcHist/compareHist, FrameTimecode's half-to-even rounding, the flash filter in float seconds…). Being faster and
lighter than the Python version is the second goal, never at the cost of a differing result. Anywhere we deliberately
differ is documented in README.md and in `docs/decisions.md`.

## Status

0.9.0 released 2026-10-08 (0.6.4 dropped, ARM NEON, in-process deinterlacing, decoder visibility, probe
container/audio; issue #42). 0.8.0 released 2026-10-07 (DeinterlaceMode Auto/On/Off, ShotDetection.Probe; issue #35). 0.7.0 released 2026-10-07 (in-process by default, ShotDetector.Native.<rid> packages, CLI bundles FFmpeg's
libraries, in-process probing: no ffmpeg needed for files). 0.6.1 released 2026-10-07 (speed: in-process faster than scenedetect on most films, whole frames for small video
on the executable path). 0.6.0 released 2026-10-07 (opt-in in-process decoding with FFmpeg 8.1's libraries, deinterlacing,
`ShotDetectionException` reasons, ActivitySource tracing, SourceLink/attestations; issues #12, #16-#19); see CHANGELOG.md.
InProcessTests need `SHOTDETECTOR_FFMPEG_LIBS` (an FFmpeg 8.1 shared build's folder) or they pass without checking. The release notes are the version's
CHANGELOG section (release.yml extracts it), so keep sections grouped: breaking changes, new, faster, fixed.
All five detectors, scenedetect 0.7.1 (`PySceneDetectVersion.V0_7_1`; the 0.6.4 mode was dropped in 0.9.0), exports
(save-images, save-html, split-video), stats, streaming out (`DetectStreamAsync`) and in (`Detect(Stream)`, URLs,
CLI `-i -`). CI compares every sample clip against scenedetect 0.7.1 on Linux for the core and FastYuv
paths; detect-hash and 10-bit video are informational. `realworld.yml` runs whole films nightly; `benchmark.yml`
weekly. `docs/backlog.md` has what's next, in order; `docs/decisions.md` what the user decided and why.

**Release policy:** the user says when to release ("when everything is ready, push a 0.x.0 release"). Open a release
PR that moves CHANGELOG.md's `## Unreleased` section to `## X.Y.Z – date`; merging it releases: release.yml (on every
push to main) tags `vX.Y.Z` when that version isn't tagged yet and publishes. Prereleases (`-`) publish without
approval, stable ones wait for the user's approval of the `release` environment. A hand-pushed `v*` tag still works.

## Working on this repository

- **PR pipeline**: `main` is protected; work on a branch, push, `gh pr create`, merge when the checks are green
  (`build`, `aot (*)`, `alpine`, `parity (*)`, `mutation`, `Real world (*)`). Don't push to main directly.
- **Backlog**: `docs/backlog.md`. Take the next open item; update its status when work starts and lands. Add items
  when something turns up (a divergence, a gap), with why.
- **Decisions**: when a design question needs the user, ask with the multiple-choice prompt and record the
  question, choices and answer in `docs/decisions.md` (newest first).
- Every behaviour that reproduces a scenedetect quirk gets a unit test and a line in `tools/mutation/mutations.psd1`
  (the mutation job breaks the guard and needs a test to fail).
- Before pushing: `dotnet test ShotDetector.slnx` (needs ffmpeg on PATH; ~3 min, the video tests make clips),
  and for detector changes `python tools/compare.py samples/*.mp4 ... --detector all --stats` (`tools/make-samples.ps1`
  creates `samples/`; `pip install scenedetect==0.7.1 opencv-python-headless`).
- Commit messages: what and why, like the history. Small commits.

## Layout

- `src/ShotDetector`: the library. `ShotDetection` (Detect/DetectStreamAsync, `DetectionOptions`), `VideoReader`
  (ffprobe + ffmpeg rawvideo over a pipe, named pipe on Windows; exact seek with `-ss` two frames early + `-copyts`
  + `select=gte(pts,P)`; streaming mode parses `showinfo` for timestamps), `Detectors` (adaptive/content/threshold/
  histogram), `HashDetector`, `ContentScorer`, `EdgeDetector`, `Hsv`, `CvResize`, `FrameTime` (FrameTimecode
  arithmetic), `Shots`, `Stats`, `Export`, `IYuv420Converter`.
- `src/ShotDetector.FastYuv`: LGPL swscale yuv420p→BGR port (`SwscaleYuv420`), its own COPYING.LGPL and README.
- `src/ShotDetector.Cli`: `shotdetect` (list-scenes table/CSV/JSON, `--stats`, `--save-images`, `--split-video`,
  time/crop/skip options). `-p:WithFastYuv=true` builds a dev-only LGPL variant for `compare.py --fast-yuv`.
- `tests/ShotDetector.Tests`: xunit. `Clips` fixture makes mpeg4 clips with ffmpeg (150 frames @ 25 fps, cuts at
  50/100). RangeTests (time/skip/crop), VideoTests (reader, streaming, cancellation), InProcessTests, HashDetectorTests.
- `tools/compare.py`: runs scenedetect and shotdetect on videos, diffs cuts/CSVs/stats, Markdown report.
  `tools/make-samples.ps1`: synthetic + Blender trailers + VFR/rotation/colour variants. `tools/realworld.py`:
  whole Blender films and codec clips, pinned by SHA-256. `tools/mutation/`: guard mutations.
- `.github/workflows`: `ci.yml` (build/test, AOT matrix incl. Alpine, parity matrix), `mutation.yml`,
  `realworld.yml` (also nightly 02:17 UTC), `benchmark.yml` (weekly + on demand; `tools/bench.py`), `release.yml` (tag → NuGet trusted publishing + GitHub release + binaries).
- `Directory.Build.props` (version; release.yml overrides from the tag), `Directory.Packages.props` (central versions).

## Gotchas (each cost time once)

- Scripted edits: write Python edit scripts with the Write tool, not bash heredocs (`\\` and `\n` collapse).
  Line endings are per file (most CRLF, some LF; no .gitattributes): keep each file's own. Python's write_text
  writes CRLF on Windows and Git Bash's `sed -i` writes LF, so check `git diff --stat` for whole-file rewrites.
  Never `git checkout -- <file>` to undo an experiment on a file with uncommitted work: copy it aside first.
- ffmpeg `-ss` lands on different frames per codec; only the `-copyts` + `select` seek is exact (VideoReader).
- Crop: ffmpeg must crop *after* `scale,format=bgr24`; scenedetect's downscale factor uses crop size + 1 (both dims).
- End of a time range is compared as frame numbers (`Position(i).PlusFrames(1).FrameNum >= end`), with
  `-e`/`-d` rounded to frames at the average rate; frame skip reads ahead before checking.
- FFmpeg 8+ ignores `in_color_matrix`: we follow the colour tags like OpenCV with FFmpeg 8 (Windows OpenCV wheels
  still carry FFmpeg 7.1 and differ on BT.709; Linux is the reference).
- MPEG-PS packets mostly lack a pts: OpenCV uses the frame's dts, so the reader takes per-frame timestamps
  from an ffprobe decoding pass when any packet has none (`FrameTimestamps`).
- Interlaced video: opencv-python 5.0.0.93 can't convert interlaced frames (FFmpeg 8 swscale: "Cannot convert
  interlaced to progressive") and gives scenedetect one nearly black frame throughout; we read the real frames. CI:
  informational with deinterlacing off, exact with `--deinterlace auto` vs scenedetect on a lossless yadif copy.
- 10-bit on Linux: OpenCV's FFmpeg converts to BGR with ±1 differences no ffmpeg CLI flag reproduces (FFmpeg 8.1/9.0,
  sws flags, dither, cpuflags all tried); compared informationally (`samples/informational/`). Linux OpenCV can't
  decode AV1 at all. Use a throwaway `debug/**` branch with a push-triggered workflow for Linux-only experiments.
- Frame labels (0.7.1): each decoded frame's showinfo time is matched to its packet (`PacketOf`), so frames
  the decoder drops make numbers skip as in OpenCV. Files always run with `-copyts` (absolute times); streamed
  times already start at 0 (ffmpeg shifts them), so `_startPts` must not be subtracted there.
- The core path's frames: ffmpeg converts to BGR, then `remap` (two looping 16-bit PGM maps, x and y) keeps only
  `CvResize.SourceCols` x `SourceRows`; `ResizeSampled` resizes from that. Equivalent to the full frames by test
  (`SampledFramesAreTheFullFramesResized`); `DetectionOptions.FullFrames` (internal) brings the old path back.
- Benchmarks on this desktop: pin CPUs with `cmd /c start /affinity 3 ...` (children inherit), compare CPU time
  (more stable than wall time here), and interleave runs.
- Streamed input needs the container headers first (mkv/webm/ts/mov or faststart mp4); ffmpeg reports a non-faststart
  mp4 on a pipe as "moov atom not found" or, when the whole file fit the probe prefix, "partial file".
- Windows named pipe path is `\\.\pipe\…`; stdout's 4 KB pipe is far slower. Kill ffmpeg race-free (`Kill` helper).
- NuGet trusted publishing: the policy's workflow must be `release.yml`, package glob `ShotDetector*`.
- GitHub Actions queues can stall; re-run rather than diagnose when every job was cancelled at ~15 min.
