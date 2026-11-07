# FrameReader moves to its own repository (2026-11-07)

### Question

The owner raised it: "I think it's time to split FrameReader from ShotDetector." Since 2026-10-30
([2026-10-30-framereader-stays-by-features.md](2026-10-30-framereader-stays-by-features.md)) FrameReader has had 13
features over 6 releases, and 3 of them needed ShotDetector (black spans, freeze spans, yadif above 8 bits). None of
the named conditions of [2026-10-31-split-trigger-criteria.md](2026-10-31-split-trigger-criteria.md) happened. Four
things were asked:

1. What changed? Choices: its own release pace; outside users; a simpler repository and CI; a separate identity.
2. What kind of split? Choices: its own repository (recommended); a package split inside this repository; a plan only.
3. How does ShotDetector get FrameReader? Choices: the NuGet package (recommended); a git submodule; the package
   with a local-checkout override.
4. What happens to the history and the issues? Choices: keep both (recommended); the history but not the issues; a
   fresh start.

### Answer

The owner, 2026-11-07:

1. **A simpler repository and CI.** The costs of one repository (tag prefixes, a release.yml for two products,
   GitHub's single "Latest" release, tools/reach's classification, the feature count) now cost more than the
   crossings save. This is a reason the 2026-10-24 list didn't name: the owner's own judgement of the costs, not an
   outside event.
2. **Its own repository**: `bisforboman/framereader`, holding src/FrameReader, src/FrameReader.Native, their tests,
   the native build and FrameReader's release.
3. **The NuGet package.** ShotDetector takes FrameReader through a PackageReference. A FrameReader change then reaches
   ShotDetector's scenedetect comparison only once it is released. The FrameReader repository makes up for that with
   a check on its pull requests that builds ShotDetector's main against the FrameReader being changed, through a local
   package feed (docs/framereader-split.md, step 4).
4. **Keep both**: `git filter-repo` carries FrameReader's history and its `framereader-v*` tags over, and the open
   issues labelled `framereader` are transferred.

This answer accepts what [2026-10-24-framereader-stays.md](2026-10-24-framereader-stays.md) listed against a split.
The scenedetect comparison moves after a release; the parity check in step 4 narrows that gap. FrameReader's
internals that ShotDetector's exactness rests on become public API across a release boundary, but ShotDetector
already uses only FrameReader's public API (no InternalsVisibleTo).

The steps are in [docs/framereader-split.md](../framereader-split.md).
