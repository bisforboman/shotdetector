# The split trigger: a minimum sample, tests counted (2026-10-17)

At FrameReader 2.6.1 the repository-split trigger (docs/frame-reader-product.md) was met for the first time: 0 of 6
commits touching both products at 2.6.0 and 0 of 2 at 2.6.1, two releases in a row under 1 in 20. But that was 8
commits over two days, and the count left out tests: DecodedFrame (#349, FrameReader) was built for ShotDetector and
tested in ShotDetector.Tests, yet counted as FrameReader-only; the window before (2.5.0) was 2 of 4. Asked: **not now,
and tighten the trigger** (at least 20 counted commits, with the windows joining until then; tests count on both sides),
over a split design doc for review or splitting now.

Since 2026-10-28 ([2026-10-28-split-count-features.md](2026-10-28-split-count-features.md)) the count is of features,
and since 2026-10-31 ([2026-10-31-split-trigger-criteria.md](2026-10-31-split-trigger-criteria.md)) it no longer
triggers the question.
