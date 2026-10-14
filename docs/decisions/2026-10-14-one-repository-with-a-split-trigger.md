# One repository, with a trigger to reconsider (2026-10-14)

GitHub's single "Latest" release (FrameReader 2.2.1 took it over) raised splitting FrameReader into its own
repository. Asked: **stay, with a trigger** (over writing up a split plan, or no change). Cross-cutting changes still
come often (3 of 9 code commits since framereader-v2.1.0 touched both), and a split would make each two PRs and a
release. Reconsider when fewer than 1 in 20 touch both at two FrameReader releases in a row
(docs/frame-reader-product.md, "When to reconsider").
