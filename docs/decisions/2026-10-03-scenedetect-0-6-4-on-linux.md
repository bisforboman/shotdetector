# scenedetect 0.6.4 on Linux (2026-10-03)

Its `ThresholdDetector` is nondeterministic on Linux (the decode thread can hand a frame over with the wrong frame
number). **Decision:** the CI compare for 0.6.4 covers content and adaptive (and histogram) only; all three matched
on Windows.

Since 2026-10-07
([2026-10-07-dropping-scenedetect-0-6-4-compatibility.md](2026-10-07-dropping-scenedetect-0-6-4-compatibility.md)) the
0.6.4 mode is gone (0.9.0), so this applies to no current mode.
