# Changelog fragments

What's changed since each product's last release, one file per pull request, so parallel PRs never edit the same
lines of a CHANGELOG. Two products, released separately (docs/frame-reader-product.md):

- `changes/shotdetector/`: ShotDetector, ShotDetector.FastYuv, the CLI and the Native packages (`CHANGELOG.md`);
- `changes/framereader/`: FrameReader (`src/FrameReader/CHANGELOG.md`).

A PR that changes both adds one fragment to each. Name it after the branch
(`changes/framereader/fix-videowriter-filters.md`); write it as the release notes' reader will read it.

```markdown
### Fixed

- `VideoWriter`: every writer failed with "Filter not found" (issue #127).
```

Sections, in the release notes' order: `### Breaking changes`, `### New`, `### Faster`, `### Fixed`. A fragment can
have several. Changes nobody using the packages would notice (CI, tests, docs) need none.

A product's release PR folds its fragments into its CHANGELOG and deletes them:

```bash
python tools/changelog.py --package framereader 2.2.0 "One line on what the version is."
```
