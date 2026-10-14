# Changelog conflicts (2026-10-10)

CHANGELOG.md's Unreleased section conflicted three times in a day (parallel PRs adding bullets, a release PR
renaming the heading while a fix was open). Asked how to stop it: **changelog fragments**, one file per PR in
changes/, folded into CHANGELOG.md by the release PR (tools/changelog.py), chosen over `merge=union` in
.gitattributes (silent, and GitHub ignores custom merge drivers) and a process-only rule. CLAUDE.md's version
history went too (CHANGELOG.md has it), and release PRs are opened once everything going into them has merged.
