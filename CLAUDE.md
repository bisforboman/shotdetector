# ShotDetector

A C# port of PySceneDetect's shot/cut detection that gives **identical results** to `scenedetect` (cuts, scene
list CSVs, per-frame stats, timecodes digit for digit). It decodes through the ffmpeg executable (a subprocess) or
in-process through FrameReader (FFmpeg 8's libraries via FFmpeg.AutoGen). Repo: `github.com/bisforboman/shotdetector`.
NuGet: `ShotDetector` (MIT), `ShotDetector.FastYuv` (LGPL-2.1, a swscale port), `ShotDetector.Cli` (dotnet tool
`shotdetect`, plus Native AOT binaries on releases), `FrameReader` (MIT, the frame-reader library) and
`FrameReader.Native.<rid>` (our FFmpeg build, LGPL).

## Core idea

Exactness over speed: every pixel operation is a bit-exact port of what OpenCV/scenedetect do (BGR→HSV fixed-point
tables, `cv2.resize` INTER_LINEAR/INTER_AREA, Canny+dilate, calcHist/compareHist, FrameTimecode's half-to-even
rounding, the flash filter in float seconds…). Being faster and lighter than the Python version is the second goal,
never at the cost of a differing result. Anywhere we deliberately differ is documented in README.md and in
`docs/decisions/`.

## Where things are

| Topic | Where |
|---|---|
| Releases and what each brought | CHANGELOG.md, src/FrameReader/CHANGELOG.md |
| Features, CLI, how parity is checked, where we differ | README.md |
| FrameReader's types and what it handles | src/FrameReader/README.md, docs/frame-reader-library.md |
| Two products, released separately | docs/frame-reader-product.md |
| FrameReader moving to its own repository (in progress) | docs/framereader-split.md |
| Chunked detection on several cores | docs/parallel-detection.md |
| What's next | docs/backlog.md, docs/backlog-done.md, the `backlog` skill |
| What the owner decided and why | docs/decisions/ (its README) |
| Light PRs, mutation shards, nightly runs | tools/ci-changes.sh, docs/decisions/2026-10-31-ci-time.md |
| The library's decoding gotchas (seek, crop, timestamps, streams) | src/ShotDetector/CLAUDE.md |
| FrameReader's FFmpeg interop gotchas | src/FrameReader/CLAUDE.md |

## Release policy

- The owner says when to release.
- Two products: ShotDetector (with FastYuv and the CLI; tags `vX.Y.Z`) and FrameReader (with FrameReader.Native;
  tags `framereader-vX.Y.Z`). A change to the native build is a FrameReader release. FrameReader is a package of its
  own, stable from 1.5.0, with its own versions from 2.1.0; ShotDetector's package depends on its current version.
- A release PR (`python tools/changelog.py --package shotdetector|framereader X.Y.Z "summary"` folds the fragments)
  opens once every PR going into it has merged, never stacked; one PR can release both. Release notes are the
  version's CHANGELOG section: keep sections grouped (breaking changes, new, faster, fixed).
- Merging it releases: release.yml (every push to main) releases each product whose newest version isn't tagged,
  FrameReader first. A hand-pushed `v*` tag still works. Never pass `-p:Version` (it would reach FrameReader through
  ShotDetector's reference): `-p:ShotDetectorVersion` and `-p:FrameReaderVersion`.
- Prereleases (`-`) publish without approval (environment `prerelease`: `v*-*` tags, and `main` once the owner adds
  it; until then push `vX.Y.Z-pre` on the release commit); stable ones wait for the owner's approval.
- Once a stable release is on nuget.org, set its baseline in Directory.Build.props (`ApiBaselineVersion` or
  `FrameReaderApiBaselineVersion`; release.yml opens a reminder issue): packing then fails on a breaking change.
- At each FrameReader release, record the count of features needing both products (docs/frame-reader-product.md,
  "When to reconsider"); ask whether to split only when one of its conditions happens.
- A `PySceneDetectVersion` is obsoleted two scenedetect releases back and removed at the next major
  (docs/decisions/2026-10-31-retiring-scenedetect-modes.md).

## Working on this repository

- **PR pipeline**: `main` is protected; work on a branch, push, `gh pr create`, and turn auto-merge on at once (drafts
  excepted, and a PR the owner must decide on says so in its body and waits for the answer). Don't push to main.
- **Required checks**: `build` and `CI passed` (ci.yml), `Real world passed` (realworld.yml), `mutation`
  (mutation.yml). The last three are gates that need every other job in their workflow, passed or skipped (a skipped
  matrix job never reports its per-matrix names). A new job in ci.yml or realworld.yml goes in its gate's `needs`.
- **Files an open PR changes**: before editing a file, check the open PRs (`gh pr list`); if one changes it, wait for
  it to merge and branch from main after it.
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored). Sessions working at
  the same time each use their own worktree, never one checkout.
- **Changelog**: a PR with a change users of the packages notice adds `changes/shotdetector/<branch>.md` or
  `changes/framereader/<branch>.md` (both when it changes both; changes/README.md), never a CHANGELOG itself.
- **Backlog**: take the first Next item of `docs/backlog.md`; set its status when work starts; when it is Done
  (Dropped, Won't do) move the row to the end of `docs/backlog-done.md`. Add items when something turns up, with why,
  in the shape the `backlog` skill gives (docs/decisions/2026-10-18-backlog-shape.md).
- **Advisor**: when one is available, call it before design decisions or recommendations, before splitting work
  across agents or sessions, and before declaring a larger task done.
- **Where work runs**: long work runs as cloud sessions; the owner's computer must stay free. Work that needs it
  (timings on its Ryzen, Windows-only checks) is asked for, never started unasked, and the PR says so.
- **Asking the owner**: whenever you need input (a choice, an approval, whether to go on), ask with the
  multiple-choice prompt (AskUserQuestion), not at the end of a reply. Record a design
  decision's question, choices and answer as `docs/decisions/YYYY-MM-DD-what.md`.
- **Mutations**: every behaviour that reproduces a scenedetect quirk gets a unit test and a line in
  `tools/mutation/mutations.psd1` (`Invoke-Mutations.ps1` breaks the guard and needs a test to fail).
- **Docs first**: every push reruns everything, so docs go in before `gh pr create`, not as follow-up pushes (a
  docs-only PR is light: tools/ci-changes.sh).
- **No benchmark numbers in docs**: they go stale; link the weekly runs' summaries.
- **Code style**: StyleBro (rules in `.editorconfig`). The pre-commit hook formats the staged .cs files (`git config
  core.hooksPath .githooks` once per clone; it stops a commit whose .cs files also have unstaged changes). By hand:
  `dotnet tool run bro format ShotDetector.slnx`; CI runs it with `--verify-no-changes` in ci.yml's `aot` job on
  win-x64. The tool (.config/dotnet-tools.json) and the analyzers (Directory.Packages.props) move together, by hand.
  Feedback on StyleBro goes to its GitHub issues.
- **Security analyzers**: the SDK's Security rules (CA2xxx, CA3xxx, CA5xxx) are errors on every build
  (Directory.Build.props; `CheckSecurityAnalyzers` in Directory.Build.targets fails a build without them). Fix a
  real finding (the owner decides how); suppress a false positive where it is, with a justification
  (docs/decisions/2026-11-06-security-analyzers.md).
- **Dependabot**: patch and minor PRs auto-merge; majors wait for a look. FFmpeg.AutoGen stays on 8.x until the
  native build moves to FFmpeg 9.
- **Commit messages**: what and why, like the history. Small commits.

## Before pushing

- Cloud sessions: `.claude/hooks/session-start.sh` installs the .NET 10 SDK if missing, restores the tool and the
  solution, sets `core.hooksPath`, and puts BtbN's pinned FFmpeg 8.1 gpl-shared (in `~/.cache/shotdetector`) on PATH
  and in `SHOTDETECTOR_FFMPEG_LIBS`; scenedetect and the samples it leaves to you.
- Prerequisites: .NET 10, ffmpeg on PATH, PowerShell (`pwsh`: tools/make-samples.ps1, Invoke-Mutations.ps1), and for
  the comparisons `pip install scenedetect==0.7.2 opencv-python-headless`.
- `dotnet test ShotDetector.slnx` (~3 min; the video tests make clips). One class: `--filter
  "FullyQualifiedName~VideoTests"`.
- `SHOTDETECTOR_FFMPEG_LIBS`: a folder of FFmpeg 8.1's shared libraries; without it the in-process tests pass without
  checking. Locally: our build from a ci.yml or native.yml run (`gh run download <run> -n native-linux-x64`, what
  CI's main test run uses), or BtbN's `gpl-shared` 8.1 archive from the release pinned in
  `.github/actions/btbn-ffmpeg/action.yml`, with `lib/*.so*` copied into `bin/` as the action does.
- For detector changes: `pwsh tools/make-samples.ps1` (once, into `samples/`), then `python tools/compare.py
  samples/*.mp4 ... --detector all --stats`; `--compat 0.7.1` with 0.7.1 installed for the previous release.

## Layout

- `src/ShotDetector`: the library (`ShotDetection`, `VideoReader`, the detectors, `Shots`, `Stats`, `Export`,
  `Timeline`, ...).
- `src/FrameReader`: the frame-reader library (tests in `tests/FrameReader.Tests`). ShotDetector depends on it one
  way, through its public API only.
- `src/FrameReader.Native`: our FFmpeg build (`build-ffmpeg.sh`, run by native.yml).
- `src/ShotDetector.FastYuv`: the LGPL swscale port, its own COPYING.LGPL and README.
- `src/ShotDetector.Cli`: `shotdetect`. `-p:WithFastYuv=true` builds a dev-only LGPL variant for `compare.py
  --fast-yuv`.
- `tests/ShotDetector.Tests`: xunit. With `SHOTDETECTOR_FFMPEG_LIBS` its clips are made by `tests/ClipMaker` in a
  process of its own: loaded in the test process, the libraries would switch every test to in-process decoding.
  Rerun `tools/oracle.py` (`Data/oracle.txt`) when the scenedetect pin moves; CI regenerates `Data/pixels.txt`
  (`tools/pixel_oracle.py`) on Linux and diffs.
- `tests/FramesWithoutFfmpeg`, `tests/AudioWithoutFfmpeg`: processes of their own that fail if detection on the
  caller's frames, or FrameReader's audio measures, load anything of FFmpeg.
- `examples/ShotDetector.Example`: ShotDetector and FrameReader end to end.
- `tools/`: `compare.py` (scenedetect against shotdetect), `make-samples.ps1`, `realworld.py` (whole films pinned by
  SHA-256), `mutation/`, `ci-changes.sh`.
- `.github/workflows`: `ci.yml`, `realworld.yml` (nightly; on PRs its jobs skip and the gate reports), `mutation.yml`
  (PRs: the entries their files concern; nightly: all), `native.yml` (our FFmpeg build, which the others call),
  `release.yml`, `benchmark.yml`, `frame-reader-bench.yml`, `compare.yml` (on demand), `parity-os.yml` (Windows and
  macOS, informational), `upstream.yml`, `codeql.yml`, `dependabot-auto-merge.yml`, `vulnerable-packages.yml`
  (weekly: an issue when main has a package with a NuGet advisory; every restore already fails on one). Each header
  says the rest.

## Gotchas (each cost time once)

- Scripted edits: write Python edit scripts with the Write tool, not bash heredocs (`\\` and `\n` collapse).
  Line endings are per file (most CRLF, some LF; no .gitattributes): keep each file's own. Python's write_text
  writes CRLF on Windows and Git Bash's `sed -i` writes LF, so check `git diff --stat` for whole-file rewrites.
  Git Bash's grep doesn't see a line's CR (`grep -c $'$'` says 0 on a CRLF file): count endings by bytes (Python).
  Never `git checkout -- <file>` to undo an experiment on a file with uncommitted work: copy it aside first.
  CI's `line-endings` job fails a PR in which more than 20 of a file's lines changed only their ending; for a
  conversion on purpose, label the PR `line-endings` before pushing (CI reads labels when a run starts).
- Where OpenCV itself varies (FFmpeg 8 ignoring `in_color_matrix`, the Windows wheel's BT.601, black interlaced
  frames, ARM's KleidiCV and IDCTs; x86 Linux is the reference): README, "Where this differs from PySceneDetect",
  and docs/decisions/ on colour (2026-10-02), interlacing (2026-10-08), ARM (2026-10-13, 2026-10-17) and the IDCTs.
- SIMD on ARM pays only with work per vector (the edge suppression won, a one-pixel-per-vector resize pass lost:
  docs/backlog-done.md). Time an ARM path on CI's ARM runners (`compare.yml`) before keeping it.
- Experiments: a Linux-only one goes on a throwaway `debug/**` branch with a push-triggered workflow (its files outside
  a `debug/` folder, which .gitignore ignores). To time a change against main on other runners (ARM, macOS), dispatch
  `compare.yml` on its branch instead: no branch to delete.
- Benchmarks on the owner's desktop: pin CPUs (`cmd /c start /affinity 3 ...`), compare CPU time, interleave runs.
  benchmarks/micro times the resize and edge detector alone.
- NuGet trusted publishing: the policy's workflow must be `release.yml`, package glob `ShotDetector*`.
- Runners are pinned to an image version (ubuntu-26.04, windows-2025, macos-15); move a pin after a do-not-merge PR
  runs CI on the new one. native.yml's glibc and Windows builds stay on ubuntu-22.04 (glibc baseline); musl builds in
  Alpine on 26.04, osx-arm64 on macos-15.
- A failure on only some runners: GitHub's mix CPUs, some with AVX-512; log `/proc/cpuinfo`
  (docs/decisions/2026-10-10-videowriter-s-rare-ci-mismatch.md).
- GitHub Actions queues can stall; re-run rather than diagnose when every job was cancelled at ~15 min.
- Failed tests and differing comparisons are check-run annotations, readable where job logs aren't: `gh api
  repos/bisforboman/shotdetector/check-runs/<id>/annotations` (10 per step, 50 per run).
- Workflows: pin external actions by commit SHA with the tag in a comment (`git ls-remote
  https://github.com/<owner>/<repo> refs/tags/<tag>`); tokens default to `contents: read`, so give a job that needs
  more its own `permissions`.
- CI downloads: BtbN's FFmpeg only through `.github/actions/btbn-ffmpeg` (pinned, checksummed, cached; move the pin by
  hand: BtbN prunes daily releases), never `curl | tar`; Docker images from `public.ecr.aws/docker/library/` (Docker
  Hub's rate limit stopped a release).
