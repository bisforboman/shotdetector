# FrameReader stays in this repository (2026-10-24)

At FrameReader 2.11.0 the repository-split trigger (docs/frame-reader-product.md, "When to reconsider") was met with
enough commits for the first time:
- 2.11.0's window: 1 of 25 counted commits touches both products, and that one only adds the GitHub Actions test
  logger to both test projects.
- 2.8.0's window: 1 of 21.
- Since 2.8.0: 1 of 35.

Nothing in that window touches FrameReader alone except the sample-fed reference tone and waveform (#500). Asked:
split FrameReader into its own repository?

### Choices

1. **Don't split**: record it, and keep counting at each FrameReader release.
2. Split: plan the move to its own repository (history, native build, release, issues) after this release.
3. Don't split and stop counting: one repository by design.

### Answer

**1** (the owner, 2026-10-24). The owner asked the same question earlier that day and chose to wait for this count.
Why not split, from that conversation:
- The few crossings are a sign FrameReader is quiet, not that the products have separated.
- In two repositories, a FrameReader change that reaches ShotDetector's decoding would run the scenedetect comparison
  only after its release, not before it merges.
- ShotDetector's package carries FrameReader.dll, so the internals its exactness rests on would become public API
  across a release boundary.
- The costs of one repository (tag prefixes, a release.yml for two products, CI by what a change touches) are already
  automated, and GitHub's concurrent jobs count per account, so a second repository adds no runners.
- No outside users or contributors drive FrameReader.

What would change the answer:
- FrameReader issues or pull requests from outside.
- FrameReader needing its own release pace.
- An FFmpeg-free FrameReader surface that a browser consumer wants on its own. That is a package split inside this
  repository, a different question.

Since 2026-10-28 ([2026-10-28-split-count-features.md](2026-10-28-split-count-features.md)) the count is of features,
and since 2026-10-31 ([2026-10-31-split-trigger-criteria.md](2026-10-31-split-trigger-criteria.md)) this file's "what
would change the answer" is the trigger.
