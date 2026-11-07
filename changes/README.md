# Changelog fragments

What's changed since the last release, one file per pull request in `changes/shotdetector/` (ShotDetector,
ShotDetector.FastYuv and the CLI: `CHANGELOG.md`), so parallel PRs never edit the same lines of the CHANGELOG.
FrameReader has its own fragments in its own repository (github.com/bisforboman/FrameReader). Name it after the branch
(`changes/shotdetector/fix-save-images.md`); write it as the release notes' reader will read it.

```markdown
### Fixed

- `Export`: what the fix changes, as the release notes' reader sees it (issue #123).
```

Sections, in the release notes' order: `### Breaking changes`, `### New`, `### Faster`, `### Fixed`. A fragment can
have several. Changes nobody using the packages would notice (CI, tests, docs) need none.

A release PR folds the fragments into the CHANGELOG and deletes them:

```bash
python tools/changelog.py 3.31.0 "One line on what the version is."
```
