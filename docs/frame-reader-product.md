# FrameReader as its own product

Status: done (2026-10-14): own versions, changelogs and releases (one PR), the native packages, CI by what a change touches, the docs. The user's choices are in `docs/decisions/` ("FrameReader as its
own product").

## Why

- Most new requests are FrameReader features with no shot detection in them (DASH and CENC packaging, Widevine,
  ffmpeg-exact audio conversion, ffprobe JSON, transcoding, test media, startup checks: issues #171-#244).
- One version for both moves each package's number for the other's changes: 1.10-1.13.1 were mostly FrameReader's,
  and 2.0.0 gave FrameReader a major version though its changes were all additive.
- The FFmpeg build FrameReader needs ships as `ShotDetector.Native`.
- Last 80 commits on main (2026-10-13): 13 FrameReader only, 20 ShotDetector only, 7 both, the rest docs/CI/release.

## What stays

One repository. Cross-cutting changes (FastYuv's resolution fix #202, the resize without the sampled copy #212)
stay one PR: ShotDetector's exactness rests on FrameReader's internals (BgrConverter's rows, the row deinterlacer,
FrameDecoder's timestamps), and splitting the repository would make each such change two PRs and a release.

### When to reconsider (2026-10-14)

Separate repositories would spare the costs one repository carries: tag prefixes, a release.yml for two products,
GitHub's single "Latest" release (FrameReader's are created with `--latest=false`), one issue tracker, and the
computed CI classification (tools/reach). Reconsider when changes stop crossing over: at each FrameReader release,
count the commits since the previous one that touch FrameReader's or ShotDetector's code, and how many touch both;
fewer than 1 in 20 touching both, at two releases in a row, is the trigger. Since framereader-v2.1.0 it was 3 of 9 (3 both, 6 one)
(FrameReader.Native, the clips made by FrameReader, and the Alpine fix).

```bash
for c in $(git rev-list framereader-vPREVIOUS..HEAD); do f=$(git show --name-only --format= $c)
  a=$(echo "$f" | grep -c '^src/FrameReader'); b=$(echo "$f" | grep -cE '^(src|tests)/ShotDetector')
  if [ $a -gt 0 ] && [ $b -gt 0 ]; then echo both; elif [ $a -gt 0 ] || [ $b -gt 0 ]; then echo one; fi; done | sort | uniq -c
```

## Steps

1. **Versions.** Each package its own `<Version>` and `ApiBaselineVersion` (Directory.Build.props today holds one of
   each). Both continue from 2.1.0. ShotDetector depends on FrameReader as `[2.1.0, 3.0.0)`, the lower bound raised
   when ShotDetector needs something newer. Package validation per package against its own baseline.
2. **Changelog.** `changes/framereader/*.md` and `changes/shotdetector/*.md` (a PR touching both adds one to each);
   `CHANGELOG.md` for ShotDetector (FastYuv, CLI, its Native aliases), `src/FrameReader/CHANGELOG.md` for FrameReader
   and FrameReader.Native. `tools/changelog.py --package framereader|shotdetector`.
3. **Releases.** release.yml releases each product whose CHANGELOG's newest version isn't tagged yet: ShotDetector as
   `vX.Y.Z` (as now), FrameReader as `framereader-vX.Y.Z`, each with its own GitHub release and notes. A release PR
   per product; both can merge in one PR when a change spans them. The baseline issue per product.
4. **Native packages.** `FrameReader.Native.<rid>` (and the `FrameReader.Native` meta-package) are the real ones;
   `ShotDetector.Native.<rid>` and `ShotDetector.Native` become aliases depending on them at the matching version,
   so existing references keep working. Released with FrameReader; the aliases with ShotDetector.
5. **CI by what a change touches**, not by package (the runners are the limit: GitHub's concurrent jobs count per
   account, 20 on the Free plan with 5 macOS, so a separate repository wouldn't add any; running less per PR does): a FrameReader change in code ShotDetector uses still runs the
   scenedetect comparison (it can move ShotDetector's results); FrameReader-only parts (the writers, Remux, Audio,
   MediaProbe's JSON, TestMedia, ...) run FrameReader's tests and builds only. The `changes` jobs' light list grows
   into that classification.
6. **Docs.** FrameReader's README as its package's front page (install, features, licence, its own changelog);
   the root README introduces both, with FrameReader as a library of its own. Issues get a `framereader` label.

## Open questions (ask when the step comes)

- ~~Whether the CLI bundles the FFmpeg libraries via ShotDetector.Native's alias or FrameReader.Native~~: neither, it
  bundles the same files from native/<rid>/ (step 4).
- Whether FrameReader's issues and discussions stay in this repository's tracker (assumed yes).
