# Windows and macOS runners pinned too (2026-10-31)

### Question

The Ubuntu runners are pinned so an image change can't move CI unannounced
([2026-10-11-ubuntu-runners-after-the-26-04-check.md](2026-10-11-ubuntu-runners-after-the-26-04-check.md)), and the
macOS native build moved to `macos-15` rather than `macos-latest` for the same reason
([2026-10-30-native-macos-15.md](2026-10-30-native-macos-15.md)). But the AOT job (ci.yml), the release binaries
(release.yml, which bundle the native dylibs), parity-os.yml and compare.yml's default still ran on `windows-latest`
and `macos-latest`: an image move could change what ships, or raise the binaries' minimum macOS, without a PR. Found
in a review of every decision. Pin them too?

### Choices

1. `windows-2025` and `macos-15` everywhere, as Ubuntu (a do-not-merge PR checks a new image first), with a check of
   the macOS binary's minimum version (recommended).
2. Only release.yml's binaries.
3. Stay on `-latest`, with the reason recorded.

### Answer

**1** (the owner, 2026-10-31). The pull request that pins them runs the AOT job on both images before it merges (the
check the Ubuntu pins had); parity-os.yml's nightly follows. ci.yml's AOT job also checks that
`shotdetect` on osx-arm64 loads on macOS 14, as its bundled libraries do (native.yml's `MACOSX_DEPLOYMENT_TARGET`,
checked with `otool -l`).
