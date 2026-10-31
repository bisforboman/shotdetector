# Decoder threads like OpenCV (2026-10-11)

Damaged files decoded like scenedetect's only with OpenCV's thread count (Linux, 4 CPUs: exact at 4, different at
1-3 and 16). Asked: **match OpenCV, min(CPUs, 16), on both paths** (over an opt-in, and leaving it), replacing
issue #12's CPUs - 1 (executable) and the 4/8 caps.

### Note (2026-10-22)

"Exact at 4" held on the run measured, not on every run: with several decoder threads, FFmpeg conceals damage in
corrupt and truncated files differently from run to run, in scenedetect (OpenCV) as in ShotDetector, so neither tool
repeats itself there. OpenCV's thread count still gives the same kind of concealment (the same variation between
runs on both sides); damaged files are best effort since
[2026-10-22-damaged-files-best-effort.md](2026-10-22-damaged-files-best-effort.md).

Since 2026-10-21 (#461) chunked runs, the default for long files from 4 CPUs on, share out up to twice the CPUs (at most
16) for frame-threaded decoders up to 1080p; sequential runs keep this count
([2026-10-21-sequential-decoder-threads.md](2026-10-21-sequential-decoder-threads.md)).
