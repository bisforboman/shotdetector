# One repository, with a trigger to reconsider (2026-10-14)

GitHub's single "Latest" release (FrameReader 2.2.1 took it over) raised splitting FrameReader into its own
repository. Asked: **stay, with a trigger** (over writing up a split plan, or no change). Cross-cutting changes still
come often (3 of 9 code commits since framereader-v2.1.0 touched both), and a split would make each two PRs and a
release. Reconsider when fewer than 1 in 20 touch both at two FrameReader releases in a row
(docs/frame-reader-product.md, "When to reconsider").

The trigger was resized on 2026-10-17 ([2026-10-17-split-trigger-sample.md](2026-10-17-split-trigger-sample.md)),
counted in features since 2026-10-28 ([2026-10-28-split-count-features.md](2026-10-28-split-count-features.md)), and
since 2026-10-31 ([2026-10-31-split-trigger-criteria.md](2026-10-31-split-trigger-criteria.md)) the question comes back
on named conditions, not a count.
