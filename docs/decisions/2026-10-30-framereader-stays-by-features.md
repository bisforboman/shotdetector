# FrameReader stays in this repository, by the feature count too (2026-10-30)

At FrameReader 2.17.0 the repository-split trigger (docs/frame-reader-product.md, "When to reconsider") was met for the
first time since it counts features (docs/decisions/2026-10-28-split-count-features.md): 2.17.0 has one FrameReader
feature, MPEG-TS frame skipping (#610), which didn't need ShotDetector; joined with 2.15.1's and 2.16.0's window that is
0 of 3, after 2.15.0's 0 of 4, so two releases in a row without one. Asked: split FrameReader into its own repository?

### Choices

1. **Don't split, keep counting**: the reasons of docs/decisions/2026-10-24-framereader-stays.md still hold.
2. Don't split and stop counting: one repository by design.
3. Plan the split after this release.

### Answer

**1** (the owner, 2026-10-30). Nothing from "What would change the answer" (docs/decisions/2026-10-24-framereader-stays.md)
has happened: no FrameReader issues or pull requests from outside, no need for its own release pace, no browser
consumer wanting an FFmpeg-free FrameReader on its own (#597 made ShotDetector's frame-source paths FFmpeg-free, not
FrameReader).
