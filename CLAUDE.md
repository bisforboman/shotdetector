# ShotDetector

A C# port of PySceneDetect's shot/cut detection that gives **identical results** to `scenedetect` (cuts, scene
list CSVs, per-frame stats, timecodes digit for digit), decoding through an ffmpeg subprocess with no native
bindings. Repo: `github.com/bisforboman/shotdetector`. NuGet: `ShotDetector` (MIT), `ShotDetector.FastYuv`
(LGPL-2.1, a swscale port), `FrameReader.Native.<rid>` (our FFmpeg build, LGPL),
`ShotDetector.Cli` (dotnet tool `shotdetect`, plus Native AOT binaries on releases).

## Core idea

Exactness over speed ("Better to have a correct baseline", user, 2026-10-01): every pixel operation is a bit-exact
port of what OpenCV/scenedetect do (BGR→HSV fixed-point tables, `cv2.resize` INTER_LINEAR/INTER_AREA, Canny+dilate,
calcHist/compareHist, FrameTimecode's half-to-even rounding, the flash filter in float seconds…). Being faster and
lighter than the Python version is the second goal, never at the cost of a differing result. Anywhere we deliberately
differ is documented in README.md and in `docs/decisions/`.

## Status

Releases, newest first, and what each brought: CHANGELOG.md.
InProcessTests need `SHOTDETECTOR_FFMPEG_LIBS` (an FFmpeg 8.1 shared build's folder) or they pass without checking. The release notes are the version's
CHANGELOG section (release.yml extracts it), so keep sections grouped: breaking changes, new, faster, fixed.
All five detectors, scenedetect 0.7.2 (`PySceneDetectVersion.V0_7_2`, default) and 0.7.1 (`V0_7_1`; the 0.6.4 mode was
dropped in 0.9.0), exports
(save-images, save-html, split-video), stats, streaming out (`DetectStreamAsync`) and in (`Detect(Stream)`, URLs,
CLI `-i -`). CI compares every sample clip against scenedetect 0.7.2 and 0.7.1 on Linux for the core and FastYuv
paths (10-bit h264 too), 0.7.2 both through the ffmpeg executable and in-process (our FFmpeg build; `compare.py`
adds `SHOTDETECTOR_OURS_ARGS` to every run, CI sets `--decoder`). `Detect` chunks long in-process files on several
cores by default from 4 CPUs on (docs/parallel-detection.md; `--sequential` one pass, `--parallel` also above 4K and
says why it couldn't, hidden `--chunks N`); detect-hash and interlaced 10-bit (AVC-Intra, ProRes) are informational. `realworld.yml` runs whole films nightly (and FrameReader's audio on their soundtracks); `benchmark.yml`
weekly. `docs/backlog.md` has what's next, in order (Next, Waiting, Maybe; finished rows in `docs/backlog-done.md`);
`docs/decisions/` what the user decided and why.

**Release policy:** the user says when to release ("when everything is ready, push a 0.x.0 release"). Two products,
released separately since 2.1.0 (docs/frame-reader-product.md): ShotDetector (with FastYuv and the CLI;
CHANGELOG.md, tags `vX.Y.Z`; the ShotDetector.Native aliases ended with 2.x) and FrameReader (with FrameReader.Native, our FFmpeg build;
src/FrameReader/CHANGELOG.md, tags `framereader-vX.Y.Z`). A change to the native build is a FrameReader release. A
product's release PR folds its fragments into its CHANGELOG (`python tools/changelog.py --package shotdetector|framereader
X.Y.Z "summary"`), opened once every PR going into it has merged (never stacked on an open branch); one PR can release
both. Merging it releases: release.yml (on every push to main) releases each product whose newest version isn't tagged
yet, FrameReader first, each with its own tag and GitHub release; ShotDetector's package depends on FrameReader's
current version. Never pass `-p:Version` (it would reach FrameReader through ShotDetector's reference):
`-p:ShotDetectorVersion` and `-p:FrameReaderVersion`. At each FrameReader release, count the commits touching both
products (docs/frame-reader-product.md, "When to reconsider") and tell the user when the split trigger is met. Prereleases (`-`) publish without
approval (the `prerelease` environment allows `v*-*` tags, and `main` once the user adds it; with tags only, push
`vX.Y.Z-pre` on the release commit), stable ones wait for the user's approval of the `release` environment. A hand-pushed `v*` tag still works.
After a stable release is on nuget.org, set that product's baseline in Directory.Build.props to it: `ApiBaselineVersion`
(ShotDetector, FastYuv) or `FrameReaderApiBaselineVersion` (release.yml opens an issue per product as the reminder):
packing then fails on any breaking change (SDK package validation, CP000x errors). FrameReader.dll, inside
ShotDetector's package, isn't under semver: its changes go in src/ShotDetector/CompatibilitySuppressions.xml (regenerate
with `-p:ApiCompatGenerateSuppressionFile=true`, FrameReader.dll entries only).

## Working on this repository

- **PR pipeline**: `main` is protected; work on a branch, push, `gh pr create`, and turn auto-merge on at once (the owner's preference, 2026-10-18; drafts excepted, and a PR the owner must decide on says so in its body and waits for the answer).
  Branch protection requires `build`, `CI passed`, `Real world passed` and `mutation`; the two "passed" jobs are gates
  that need every other job in their workflow (passed or skipped: light PRs skip the heavy ones, and a skipped matrix
  job never reports its per-matrix names). A new job in ci.yml or realworld.yml goes in its gate's `needs`. Don't push
  to main directly.
- **Files an open PR changes**: before editing a file, check the open PRs (`gh pr list`, or the GitHub tools' PR files) for one that changes it; if one does, wait for it to merge and branch from main after it, rather than editing the same lines in parallel (2026-10-18: none of 34 PRs in a week needed a conflict resolved by hand, this way).
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored). Sessions working at
  the same time each use their own worktree, never one checkout, so a commit can't land on another's branch.
- **Changelog**: a PR with a change users of the packages notice adds `changes/shotdetector/<branch>.md` or
`changes/framereader/<branch>.md`, one to each when it changes both (changes/README.md: `### Fixed` / `### New` / ...
and bullets), never a CHANGELOG itself, so parallel PRs don't conflict there.
- **Backlog**: `docs/backlog.md`: Next (in priority order), Waiting (on the owner or an outside event), Maybe. Take
  the first Next item; update its status when work starts, and when it is Done (or Dropped, Won't do) move the row
  to the end of `docs/backlog-done.md` (under "Since 2026-10-18"). Add items when something turns up (a divergence,
  a gap), with why, in Next or Maybe by status, at their priority. Items are list entries (What, then Why: and
  Status:) with a blank line between them, so parallel PRs that edit neighbouring items merge cleanly, on GitHub too.
- **Advisor**: when an advisor tool is available, call it before design decisions or recommendations, before splitting
  work across agents or sessions, and before declaring a larger task done.
- **Where work runs**: long work (multi-step tasks, PRs, CI waits, releases, CI benchmarks) runs as cloud sessions; the
  user's own computer must stay free to restart or turn off. Work that needs it (timings on its Ryzen, Windows-only
  checks) is asked for, never started unasked; a cloud session that needs such a step says so in its PR.
- **Asking the user**: whenever you need input from the user (a choice, an approval, whether to go on or take the
  next step), ask with the multiple-choice prompt (AskUserQuestion), not with a question or an offer at the end of a
  reply: the prompt is what reaches them in the app.
- **Decisions**: when a design question needs the user, ask with the multiple-choice prompt and record the
  question, choices and answer as a new file, `docs/decisions/YYYY-MM-DD-what.md` (its README).
- Every behaviour that reproduces a scenedetect quirk gets a unit test and a line in `tools/mutation/mutations.psd1`
  (the mutation job breaks the guard and needs a test to fail).
- Before pushing: `dotnet test ShotDetector.slnx` (needs ffmpeg on PATH; ~3 min, the video tests make clips),
  and for detector changes `python tools/compare.py samples/*.mp4 ... --detector all --stats` (`tools/make-samples.ps1`
  creates `samples/`; `pip install scenedetect==0.7.2 opencv-python-headless`; `--compat 0.7.1` with 0.7.1 installed
  for the previous release).
- Code style: StyleBro (analyzers on build; rules chosen in `.editorconfig`, docs/decisions/). The
  pre-commit hook formats the staged .cs files (`git config core.hooksPath .githooks` once per clone; ~15-30 s, only
  when .cs files are staged; it stops a commit whose .cs files also have unstaged changes, and one that rewrites a file's line endings, as CI's
  line-endings job does). By hand: `dotnet tool run
  stylebro-migrate format ShotDetector.slnx` (the version is in .config/dotnet-tools.json, with the analyzers' in
  Directory.Packages.props; both move in one PR, by hand: Dependabot's NuGet updater only moves a prerelease to
  prereleases of the same version or a stable, so upstream.yml files an issue for a newer StyleBro); CI checks it
  with `--verify-no-changes` (clean on Linux too since StyleBro 0.6.0). Feedback on StyleBro goes to its GitHub issues.
- Docs don't copy benchmark numbers: speed and memory figures go stale with every change. READMEs link the weekly
  runs (`benchmark.yml`, `frame-reader-bench.yml`; their summaries have the tables) and keep only claims that hold.
- Dependabot: its patch and minor PRs auto-merge once green (`dependabot-auto-merge.yml`); majors wait for a look.
  FFmpeg.AutoGen stays on 8.x until the native build moves to FFmpeg 9 (with opencv-python's wheels): Dependabot is
  told to ignore 9.
- Commit messages: what and why, like the history. Small commits.

## Layout

- `src/ShotDetector`: the library. `ShotDetection` (Detect/DetectStreamAsync, `DetectionOptions`), `VideoReader`
  (ffprobe + ffmpeg rawvideo over a pipe, named pipe on Windows; exact seek with `-ss` two frames early + `-copyts`
  + `select=gte(pts,P)`; streaming mode parses `showinfo` for timestamps), `Detectors` (adaptive/content/threshold/
  histogram), `HashDetector`, `ContentScorer`, `EdgeDetector`, `Hsv`, `CvResize`, `FrameTime` (FrameTimecode
  arithmetic), `Shots`, `Stats`, `Export`, `IYuv420Converter`.
- `src/FrameReader`: the general frame-reader library (docs/frame-reader-library.md), MIT, its own package `FrameReader`
  at ShotDetector's version, stable from 1.5.0 (semver and package validation like ShotDetector's); ShotDetector
  depends on it, one way, through its public API only. So far
  `FFmpegLibraries` (loading FFmpeg 8's libraries), `MediaProbe` (headers, packet pts, Stream input; ShotDetector's
  `InProcessProbe` formats it as ffprobe text), `VideoFrameReader` (frames at a size and format, the bytes of ffmpeg's scale+format filters),
  `FrameDecoder` (decode, seek, deinterlace, autorotate), `BgrConverter` (a decoded frame as ffmpeg's
  scale,format=bgr24, whole or some rows; ShotDetector's `FrameWriter` builds the pipelines' layouts on it), `FrameReaderException` (ShotDetector maps it in
  `InProcess.Map`); tests in `tests/FrameReader.Tests`.
- `src/ShotDetector.FastYuv`: LGPL swscale yuv420p→BGR port (`SwscaleYuv420`), its own COPYING.LGPL and README.
- `src/ShotDetector.Cli`: `shotdetect` (list-scenes table/CSV/JSON, `--stats`, `--save-images`, `--split-video`,
  time/crop/skip options). `-p:WithFastYuv=true` builds a dev-only LGPL variant for `compare.py --fast-yuv`.
- `tests/ShotDetector.Tests`: xunit. `Clips` fixture makes mpeg4 clips (150 frames @ 25 fps, cuts at 50/100): with
  SHOTDETECTOR_FFMPEG_LIBS through `tests/ClipMaker` (FrameReader, in its own process: loaded in the test process the
  libraries would switch every test to in-process decoding), else with ffmpeg. RangeTests (time/skip/crop), VideoTests (reader, streaming, cancellation), InProcessTests, HashDetectorTests,
  PropertyTests (FrameTime and PyFloat against scenedetect's answers in `Data/oracle.txt`, which `tools/oracle.py` writes:
  rerun it when the scenedetect pin moves; FsCheck invariants), PixelTests (resize, HSV, gray, edges, content score on
  generated images against cv2 and scenedetect: `Data/pixels.txt` from `tools/pixel_oracle.py`, which CI regenerates on
  Linux and diffs).
- `tools/compare.py`: runs scenedetect and shotdetect on videos, diffs cuts/CSVs/stats, Markdown report.
  `tools/make-samples.ps1`: synthetic + Blender trailers + VFR/rotation/colour variants. `tools/realworld.py`:
  whole Blender films and codec clips, pinned by SHA-256. `tools/mutation/`: guard mutations.
- `.github/workflows`: `ci.yml` (build/test, AOT matrix incl. Alpine, parity matrix, `arch`/`arch-diff`: x64 against ARM on every sample; `parity-os.yml`: the main comparison on Windows and macOS, informational, nightly), `compare.yml` (on demand: a branch's microbenchmarks against a base on chosen runners), `mutation.yml`,
  `realworld.yml` (also nightly 02:17 UTC), `benchmark.yml` (weekly + on demand; `tools/bench.py`), `upstream.yml` (Mondays: an issue when PyPI has a newer scenedetect than CI's pin, or a newer FFmpeg point release, zlib or dav1d than the native build's, or a new FFmpeg in opencv-python's Linux wheel), `release.yml` (tag → NuGet trusted publishing + GitHub release + binaries), `codeql.yml` (code scanning of the C#: PRs, main, weekly; findings under Security, not a required check).
- `Directory.Build.props` (version; release.yml overrides from the tag), `Directory.Packages.props` (central versions).

## Gotchas (each cost time once)

- Scripted edits: write Python edit scripts with the Write tool, not bash heredocs (`\\` and `\n` collapse).
  Line endings are per file (most CRLF, some LF; no .gitattributes): keep each file's own. Python's write_text
  writes CRLF on Windows and Git Bash's `sed -i` writes LF, so check `git diff --stat` for whole-file rewrites.
  Git Bash's grep doesn't see a line's CR (`grep -c $'$'` says 0 on a CRLF file): count endings by bytes (Python).
  Never `git checkout -- <file>` to undo an experiment on a file with uncommitted work: copy it aside first.
  CI's `line-endings` job fails a PR in which more than 20 of a file's lines changed only their ending; for a
  conversion on purpose, label the PR `line-endings` before pushing (CI reads labels when a run starts).
- ffmpeg `-ss` lands on different frames per codec; only the `-copyts` + `select` seek is exact (VideoReader).
- Crop: ffmpeg must crop *after* `scale,format=bgr24`; scenedetect's downscale factor uses crop size + 1 (both dims).
- End of a time range is compared as frame numbers (`Position(i).PlusFrames(1).FrameNum >= end`), with
  `-e`/`-d` rounded to frames at the average rate; frame skip reads ahead before checking.
- FFmpeg 8+ ignores `in_color_matrix`: we follow the colour tags like OpenCV with FFmpeg 8 (Windows OpenCV wheels
  still carry FFmpeg 7.1 and always use BT.601; compare.py passes the hidden `--always-bt601` there; Linux is the
  reference).
- MPEG-PS packets mostly lack a pts: OpenCV uses the frame's dts, so the reader takes per-frame timestamps
  from an ffprobe decoding pass when any packet has none (`FrameTimestamps`).
- Interlaced video: opencv-python 5.0.0.93 can't convert interlaced frames (FFmpeg 8 swscale: "Cannot convert
  interlaced to progressive") and gives scenedetect one nearly black frame throughout; we read the real frames. CI:
  informational with deinterlacing off, exact with `--deinterlace auto` vs scenedetect on a lossless yadif copy.
- 10-bit (any format without swscale's unscaled path to BGR): chroma is interpolated from where the samples sit. OpenCV's
  `sws_scale_frame` takes that from the frame (h264/HEVC 4:2:0: left); ffmpeg's command line drops it before its filters
  (center), so the executable path passes `in_chroma_loc` (the first frame's, read with ffprobe or FrameDecoder: a container's
  header can contradict the stream's own, as can its colour matrix and range, which FastYuv takes from there too) and
  BgrConverter's row context sets swscale's position. Linux
  OpenCV can't decode AV1 at all. Use a throwaway `debug/**` branch with a push-triggered workflow for Linux-only experiments
  (files for it outside a `debug/` folder: .gitignore ignores those, so they'd silently not be committed). To time a
  change against main on other runners (ARM, macOS), dispatch `compare.yml` on its branch instead: no branch to delete.
- Frame labels (0.7.1): each decoded frame's showinfo time is matched to its packet (`PacketOf`), so frames
  the decoder drops make numbers skip as in OpenCV. Files always run with `-copyts` (absolute times); streamed
  times already start at 0 (ffmpeg shifts them), so `_startPts` must not be subtracted there.
- The core path's frames: ffmpeg converts to BGR, then `remap` (two looping 16-bit PGM maps, x and y) keeps only
  `CvResize.SourceCols` x `SourceRows`; `ResizeSampled` resizes from that. Equivalent to the full frames by test
  (`SampledFramesAreTheFullFramesResized`); `DetectionOptions.FullFrames` (internal) brings the old path back.
- SIMD on ARM pays only with work per vector: the edge suppression with Vector128 (4 lanes of compares per load) took
  0.72-0.88x the time, but the resize's horizontal pass at one pixel per vector lost both ways (portable 2x, AdvSimd
  1.5-1.6x the scalar loop). Time an ARM path on CI's ARM runners (`compare.yml`) before keeping it.
- scenedetect on ARM isn't scenedetect on x86: opencv-python's ARM wheels use KleidiCV/Carotene for `cv2.resize`
  (downscales under 3x) and BGR2HSV, off by 1-2 on some pixels (user's choice: x86 stays the reference). macOS Intel
  wheels have no FFmpeg. `getBuildInformation()`'s "Custom HAL" line names the library. Our own results differ on
  ARM too: FFmpeg's yuv420p to BGR assembly rounds differently there; FastYuv gives x86's results on 8-bit 4:2:0 and
  4:2:2 and on 10-bit 4:2:0 (swscale's scaled MMXEXT path; ci.yml's arch-diff requires that,
  docs/decisions/2026-10-17-arm-results.md, 2026-10-18-fastyuv-422.md, docs/fastyuv-10bit-422.md). The
  IDCTs too: MPEG-1/2, MJPEG and MPEG-4 Part 2 decode with the C one (`VideoReader.IdctOptions`; `-idct simple`, and
  in-process FrameReader's `CodecOptions`, so FrameReader alone keeps FFmpeg's default), except MPEG-4 that
  FFmpeg gives the Xvid IDCT (`Mpeg4Idct`, its own rule: signature, else FourCC; 2026-10-18-mpeg4-xvid-idct.md).
- Benchmarks on this desktop: pin CPUs with `cmd /c start /affinity 3 ...` (children inherit), compare CPU time
  (more stable than wall time here), and interleave runs. The resize and edge detector alone: benchmarks/micro (real
  frames, an output hash: `--hash-only`).
- Streamed input needs the container headers first (mkv/webm/ts/mov or faststart mp4); ffmpeg reports a non-faststart
  mp4 on a pipe as "moov atom not found" or, when the whole file fit the probe prefix, "partial file". ShotDetection
  spills those to a temp file first (`VideoReader.HeadersLast`: mdat before moov in the first bytes).
- Windows named pipe path is `\\.\pipe\…`; stdout's 4 KB pipe is far slower. Kill ffmpeg race-free (`Kill` helper).
- NuGet trusted publishing: the policy's workflow must be `release.yml`, package glob `ShotDetector*`.
- FFmpeg.AutoGen lays structs out for 8-byte `long`: on Windows every AVIOContext field from `checksum` (an `unsigned
  long`) on is 8 bytes off, so don't write `seekable` and the like through it (StreamInput passes callbacks instead).
- Runners are pinned to an Ubuntu version (ubuntu-26.04 since 2026-10-11, after CI passed there), so the image
  can't change under CI unannounced; move the pin after a do-not-merge PR runs CI on the new one. The native builds
  stay on ubuntu-22.04 on purpose (glibc baseline).
- VideoWriter vs `ffmpeg -c:v libx264`: on AVX-512 CPUs, x264's AVX-512 code plus .NET's tiered JIT differ ~1 run in 250,
  so CI's VideoWriter step sets `DOTNET_TieredCompilation=0`. GitHub's runners are a mix of CPUs (EPYC 7763 without
  AVX-512, Xeons and EPYC 9V45/9V74 with it): log `/proc/cpuinfo` when a failure only shows up on some runners.
- GitHub Actions queues can stall; re-run rather than diagnose when every job was cancelled at ~15 min.
- Workflows: external actions are pinned by commit SHA with the tag in a comment (`uses: actions/checkout@<sha> # v7`;
  Dependabot moves both), and each workflow's token defaults to `contents: read`: pin a new action the same way
  (`git ls-remote https://github.com/<owner>/<repo> refs/tags/<tag>`) and give a job that needs more its own `permissions`.
- CI downloads: BtbN's FFmpeg only through `.github/actions/btbn-ffmpeg` (a pinned month-end release, each archive's
  SHA-256 in the action, retries, cached; move the pin by hand: BtbN prunes daily releases), never `curl | tar` (a reset mid-stream left tar a truncated archive); Docker images from
  `public.ecr.aws/docker/library/` (Docker Hub's anonymous rate limit stopped a release).
- CI load: a PR whose files are all docs, `.claude/`, `.githooks/`, `benchmarks/`, `tests/FrameReader.Tests/`,
  workflows CI never runs (release, benchmarks, upstream, Dependabot's, parity-os, compare) or FrameReader source files
  ShotDetector's detection never runs is "light": AOT, ARM, Alpine, parity, real-world and mutation don't start
  (skipped jobs pass the required checks). tools/ci-changes.sh decides, for all three `changes` jobs; the FrameReader
  files come from the built assemblies (tools/reach, so a new use counts at once). The FrameReader benchmark runs on
  demand and weekly only.
  Every push reruns everything: docs go in before `gh pr create`, not as follow-up pushes.
