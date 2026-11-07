# FrameReader in its own repository

Status: planned (2026-11-07). The owner's choices are in
[decisions/2026-11-07-framereader-own-repository.md](decisions/2026-11-07-framereader-own-repository.md). This plan
follows [frame-reader-product.md](frame-reader-product.md), which made FrameReader a product of its own inside this
repository.

## What moves

To `bisforboman/framereader`, with history:

- `src/FrameReader/` (its README, CHANGELOG.md, CLAUDE.md), `src/FrameReader.Native/`, `tools/native/`
- `tests/FrameReader.Tests/`, `tests/AudioWithoutFfmpeg/`
- `.github/workflows/native.yml`, `frame-reader-bench.yml`
- `docs/frame-reader-library.md`, `changes/framereader/`

Copied, not moved, and each repository trims its copy: Directory.Build.props/.targets, Directory.Packages.props,
.editorconfig, .config/dotnet-tools.json, .githooks, the security analyzer setup, `.github/actions/btbn-ffmpeg`,
ci.yml's FrameReader jobs, release.yml's FrameReader half, codeql.yml, dependabot, vulnerable-packages.yml,
tools/changelog.py, the session-start hook, and the root CLAUDE.md's general rules.

What stays here: ShotDetector, FastYuv, the CLI, `tests/ShotDetector.Tests`, `tests/ClipMaker`,
`tests/FramesWithoutFfmpeg`, the scenedetect tools, and docs/decisions/ (FrameReader's decisions too, as the record of
when they were made; the new repository's decisions/README links here for the ones before the move).

A dry run on 2026-11-07 with `git filter-repo --path ...` on a full clone kept 275 commits and all 30
`framereader-v*` tags (the `v*` tags are deleted first).

## Steps

1. **The owner creates the repository**: an empty `bisforboman/framereader`, MIT, with the Claude GitHub app given
   access to it. On nuget.org, a trusted publishing policy for the new repository: workflow `release.yml`, package
   glob `FrameReader*`. The environments `release` (with the owner's approval) and `prerelease` go on it too. A
   cloud session cannot do any of this.
2. **History.** A full clone of this repository (`git fetch --unshallow --tags`), `git tag -l 'v*' | xargs git tag
   -d`, then `git filter-repo` with the paths above, pushed to the new repository's `main` with its tags. On top, one
   commit adds the copied build files, trimmed to FrameReader: the solution `FrameReader.slnx`, a single `Version` and
   `ApiBaselineVersion` (no FrameReader prefixes), and a release.yml for one product with tags `vX.Y.Z`. The old tags
   stay as `framereader-v*`, so release.yml reads both forms when it finds the newest tagged version.
3. **CI there**: build, tests (with our native build from native.yml, as here), the AOT and format checks,
   CodeQL, mutation entries for FrameReader's quirks (moved from tools/mutation/mutations.psd1), the required checks and
   main's protection, auto-merge, dependabot. There is no tools/reach: every PR there runs everything.
4. **The ShotDetector parity check** on FrameReader's pull requests (the cost the owner accepted, narrowed): pack the PR's
   FrameReader as `0.0.0-pr.<n>` to a local folder, check out ShotDetector's `main`, restore it with that folder as a
   source and `-p:FrameReaderPackageVersion=0.0.0-pr.<n>`, then run ShotDetector's tests and `tools/compare.py` on
   the samples. It is required on PRs that change decoding (FrameDecoder, BgrConverter, the deinterlacers, seek,
   VideoReader), and informational on the rest. This repository needs a property for it in step 6.
5. **The first release from the new repository**: a prerelease first (`2.26.0-pre`), to check the trusted
   publishing policy and the native packages, then the next stable version. FrameReader keeps its version numbers;
   its CHANGELOG continues.
6. **This repository takes the package**: one PR that
   - replaces the ProjectReferences (ShotDetector, ClipMaker) with a PackageReference to `FrameReader`, its version in
     Directory.Packages.props through `FrameReaderPackageVersion` (step 4 overrides it), lower bound as today;
   - takes the CLI's and the tests' FFmpeg libraries from the `FrameReader.Native.<rid>` packages instead of
     native.yml's artifact (`SHOTDETECTOR_FFMPEG_LIBS` from the restored package in CI);
   - deletes what moved, plus release.yml's FrameReader half, `--package framereader` in tools/changelog.py,
     changes/framereader, tools/reach's FrameReader classification (ci-changes.sh then has one product), and the
     feature count in frame-reader-product.md;
   - keeps `ShotDetector.Native.<rid>`, which already only depends on `FrameReader.Native` (ask the owner in this step
     whether to deprecate them);
   - updates README.md, CLAUDE.md (layout, release policy, the "Where things are" table), the backlog (FrameReader's
     rows move to the new repository's backlog).
   From then on Dependabot opens the FrameReader bumps here; minor and patch auto-merge once ShotDetector's checks,
   the scenedetect comparison included, pass.
7. **Issues.** Transfer the open issues labelled `framereader` (GitHub's issue transfer, by the owner or a session
   with access to both repositories). FrameReader's README and package metadata point to the new repository; the
   `framereader` label here then points there.
8. **Freeze.** From step 2 until step 6 merges, FrameReader changes go to the new repository only. A FrameReader PR
   open here at that point is moved over by hand.

## Open questions (ask when the step comes)

- Whether `ShotDetector.Native.<rid>` is deprecated on nuget.org, or kept as an alias (step 6).
- Whether the parity check of step 4 is required on every FrameReader PR, or only on those that change decoding
  (assumed only those).
