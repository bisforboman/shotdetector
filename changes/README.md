# Changelog fragments

What's changed since the last release, one file per pull request, so parallel PRs never edit the same lines of
CHANGELOG.md. Name it after the branch (`changes/fix-videowriter-filters.md`); write it as the release notes'
reader will read it.

```markdown
### Fixed

- `VideoWriter` with ShotDetector.Native.Gpl's libraries: every writer failed with "Filter not found" (issue #127).
```

Sections, in the release notes' order: `### Breaking changes` (words may follow, e.g. `(FrameReader only)`),
`### New`, `### Faster`, `### Fixed`. A fragment can have several. Changes nobody using the packages would notice
(CI, tests, docs) need none.

The release PR folds them into CHANGELOG.md and deletes them:

```bash
python tools/changelog.py 1.5.0 "One line on what the version is."
```
