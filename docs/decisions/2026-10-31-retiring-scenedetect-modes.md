# When an older scenedetect mode goes (2026-10-31)

### Question

README's versioning promises that following a new scenedetect release is a minor version and that "the previous
release stays available as a `PySceneDetectVersion` value", but not what happens to the one before it. Since 1.0,
removing an enum value is a breaking change (package validation fails the pack), and keeping every mode forever goes
against [2026-10-02-versions-to-support.md](2026-10-02-versions-to-support.md) ("we don't have to manage multiple
versions"). Today: `V0_7_2` (default) and `V0_7_1`. Found in a review of every decision. What happens to `V0_7_1`
when 0.7.3 or 0.8 comes?

### Choices

1. In the minor release that makes a new scenedetect the default, the mode two releases back becomes `[Obsolete]`;
   it is removed at the next major. CI compares the default and the previous release (recommended).
2. Keep every mode; the list, and CI's matrix or its untested modes, grows.
3. Decide when it happens.

### Answer

**1** (the owner, 2026-10-31). With 0.7.3 as the default, `V0_7_2` is the previous release (CI's one 0.7.1 job moves
to it, per [2026-10-12-the-previous-scenedetect-release-in-ci.md](2026-10-12-the-previous-scenedetect-release-in-ci.md)),
and `V0_7_1` is `[Obsolete]`, untested in CI, until the next major removes it. README's "Versions" says so, and
upstream.yml's "scenedetect X released" issue lists it as a step.
