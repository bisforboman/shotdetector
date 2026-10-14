# scenedetect 0.6.4 on Linux (2026-10-03)

Its `ThresholdDetector` is nondeterministic on Linux (the decode thread can hand a frame over with the wrong frame
number). **Decision:** the CI compare for 0.6.4 covers content and adaptive (and histogram) only; all three matched
on Windows.
