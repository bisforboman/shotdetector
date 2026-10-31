# The FrameReader split question comes back on named conditions (2026-10-31)

### Question

The repository-split trigger (docs/frame-reader-product.md, "When to reconsider"), a count of FrameReader features
that also needed ShotDetector, fired four times in two weeks (2026-10-17, 10-24, 10-27, 10-30), and each time the
owner chose not to split for reasons the count doesn't measure: no FrameReader issues or pull requests from outside,
no need for its own release pace, no consumer wanting FrameReader on its own
([2026-10-24-framereader-stays.md](2026-10-24-framereader-stays.md), "What would change the answer"). FrameReader
releases nearly daily, so "two releases in a row" is a short window and the question keeps coming back with a known
answer. Found in a review of every decision. What should bring the question back?

### Choices

1. The qualitative conditions of 2026-10-24's "What would change the answer"; the count kept for information, with
   no question at each release (recommended).
2. The count, but asked at most once a month.
3. As it is: asked at each FrameReader release where the count is met.

### Answer

**1** (the owner, 2026-10-31). The question comes back when one of these happens: a FrameReader issue or pull request
from outside the project; FrameReader needing a release pace of its own; a consumer wanting an FFmpeg-free FrameReader
surface on its own (a package split inside this repository, a different question; shotdetector-web already uses
FrameReader's FFmpeg-free `ReferenceToneFinder` and `WaveformBuilder`, #494, without asking for a package). A FrameReader
release still records the feature count in docs/frame-reader-product.md, as information.
