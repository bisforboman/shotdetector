### New

- `shotdetect diff <old> <new>`: what changed between two cuts of the same programme, shot by shot (issue #771). Each
  shot of the old cut is unchanged, moved, trimmed (with the frames trimmed at its head and tail) or removed in the new
  one, and each shot of the new cut not in the old one is added, with frames and timecodes in both cuts: a table on
  standard output, `--json <file>` (or `-`) in the new cut's order, exit code 3 when anything changed. Shots are matched
  by a perceptual hash of each frame's middle, so re-encoding, scaling, a new grade or a logo near an edge don't count
  as changes; a cut the detector sees in only one of the cuts gives a row for a run of shots (`lastShot`).
  ShotDetector's own, not in scenedetect (README, docs/decisions/2026-11-09-shotdetect-diff.md); `tools/recut.py`
  measures it on re-cuts with known edits.
