# The FrameReader split count counts features, not commits (2026-10-28)

The split trigger (docs/frame-reader-product.md, "When to reconsider") counted commits that touch both products. The
user asked whether the measure was reversed. It isn't (few crossings would mean FrameReader changes on its own, so a
split would cost little), but it misses most coupling: work is cut into one PR per product, so a FrameReader feature
made for ShotDetector and the ShotDetector change using it land as separate commits that each touch one product. On
2026-10-28 the image encoder for decoded frames (#562) and save-images through it (#567) were one feature and counted
as no crossing; the trigger had fired on such near-zero counts twice, and the user had answered "don't split" twice.

### Choices

1. **Count features, not commits**: at each FrameReader release, count the changes (PRs grouped by the issue or
   feature they serve) that needed both products in the window, including a FrameReader PR followed by the
   ShotDetector PR using it.
2. Stop counting: one repository by design.
3. Keep the commit count; record it and don't ask.

### Answer

**1** (the user, 2026-10-28). The trigger is now no FrameReader feature needing ShotDetector at two releases in a row,
over at least 3 FrameReader features (fewer: the windows join). Recounted that way, 2.13.0 and 2.14.0 each had 1 of 2.
