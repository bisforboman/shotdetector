# Parallel detection: opt-in, in-process files, automatic chunks, a prototype first (2026-10-16)

Asked how parallel detection of long files should work (docs/parallel-detection.md): **opt-in first, the default
later** once CI has compared it with sequential runs (over on by default from the start); **in-process, local files
only** (over the executable path too); **automatic chunks by cores and length** (over a fixed chunk length); and a
**measured prototype first** (over going straight to the full implementation).

Since 2026-10-27 ([2026-10-27-executable-path-fixes.md](2026-10-27-executable-path-fixes.md)) the executable path chunks
too, a process per chunk; the default: [2026-10-18-parallel-default.md](2026-10-18-parallel-default.md).
