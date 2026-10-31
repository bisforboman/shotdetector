# Versions to support (2026-10-02)

"It's nice if our code can run against multiple versions, but we don't have to manage multiple versions."
**Decision:** test against the latest scenedetect (0.7.1) and the latest stable FFmpeg (9.0, BtbN build); add
0.6.4 as an explicit compatibility mode since it was asked for.

Since 2026-10-07
([2026-10-07-dropping-scenedetect-0-6-4-compatibility.md](2026-10-07-dropping-scenedetect-0-6-4-compatibility.md)) there
is no 0.6.4 mode, and since 2026-10-12
([2026-10-12-scenedetect-0-7-2-as-the-default.md](2026-10-12-scenedetect-0-7-2-as-the-default.md)) 0.7.2 is the default,
with 0.7.1 kept as the previous release; when an older mode goes:
[2026-10-31-retiring-scenedetect-modes.md](2026-10-31-retiring-scenedetect-modes.md).
