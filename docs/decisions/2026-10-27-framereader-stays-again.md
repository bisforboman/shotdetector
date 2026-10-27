# FrameReader stays in this repository, again (2026-10-27)

At FrameReader 2.11.1 the repository-split trigger (docs/frame-reader-product.md, "When to reconsider") was met for
the second time in a row: 0 of 25 counted commits since 2.11.0 touch both products (the cancellation fixes of issues
#522 and #523 each touch one). Asked again: split FrameReader into its own repository?

### Choices

1. **Don't split, keep counting**: the reasons of docs/decisions/2026-10-24-framereader-stays.md still hold.
2. Don't split and stop counting: one repository by design.
3. Plan the split after this release.

### Answer

**1** (the owner, 2026-10-27). Nothing from the earlier decision's "What would change the answer" has happened: no
FrameReader issues or pull requests from outside, no need for its own release pace, no browser consumer wanting an
FFmpeg-free FrameReader on its own.
