# FrameReader's own versions: steps 1-3 together, from 2.1.0 (2026-10-14)

Separate versions can't ship alone: release.yml passed one -p:Version, which reaches FrameReader through
ShotDetector's reference, so versions, changelogs and releases change together. Asked: **steps 1-3 in one PR**
(every intermediate state releases correctly) over three PRs with releases frozen meanwhile; FrameReader **continues
at 2.1.0** over a reset to 3.0.0 (a major without breaking changes misleads semver users). framereader-v2.1.0 was
tagged on 2.1.0's release commit so the new flow doesn't release it again.
