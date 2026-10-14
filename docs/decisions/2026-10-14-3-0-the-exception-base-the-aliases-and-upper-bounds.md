# 3.0: the exception base, the aliases and upper bounds (2026-10-14)

Asked what goes into 3.0 and when: **3.0 now with #239** (over waiting for FFmpeg 9, or #239's non-breaking shape in a
2.3). The shape: **an abstract `MediaException : InvalidOperationException` in FrameReader**, under both exceptions,
each keeping its own `Reason` (over a shared reason enum too, or one exception type). With it: **upper bounds on our
dependencies** (ShotDetector on FrameReader and FastYuv on ShotDetector up to the next major, FFmpeg.AutoGen below 9)
and **no more ShotDetector.Native aliases**. Built **on main, with a preview first** (over a v3 branch, or straight to
3.0.0).

Inserting the base breaks neither API (package validation passes), so FrameReader goes to 2.3.0, as a major without
breaking changes misleads semver users (2026-10-14, FrameReader's own versions); ShotDetector's 3.0 is for the aliases
and the bounds.
