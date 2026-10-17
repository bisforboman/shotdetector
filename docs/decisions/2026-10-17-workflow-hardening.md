# Workflow hardening (2026-10-17)

### Question

A security review with the advisor (after the 2026-10-16 fixes) found the workflows' remaining exposure: eight
workflows (benchmark, ci, frame-reader-bench, mutation, native, parity-os, realworld, and release.yml's `version` and
`native` jobs) ran with the repository's default token, and every action was referenced by a moving tag (`@v7`), so a
compromised or moved tag would run in CI and in the release workflow beside its publishing token. Dependabot alerts
are off (a repository setting). What to do?

### Choices

1. Least-privilege workflow tokens: `permissions: contents: read` at the top of those workflows (recommended).
2. Pin actions by commit SHA, the tag in a comment (Dependabot's github-actions updates move both).
3. A backlog item for SHA pinning.

### Answer

**All three** (multiple choice): both done in one PR, and the backlog item recorded as Done. Dependabot alerts stay
for the owner to turn on in the repository's settings.
