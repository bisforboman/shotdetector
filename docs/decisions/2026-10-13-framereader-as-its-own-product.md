# FrameReader as its own product (2026-10-13)

The user, looking at the pipelines: FrameReader is starting to look like its own project. Most new requests are
FrameReader features, lockstep versions move each package's number for the other's changes, and the FFmpeg build
ships as ShotDetector.Native. Asked: **its own product in the same repository** (own versions, releases, native
packages, CI by what a change touches; cross-cutting changes stay one PR), over a separate repository or keeping
the lockstep; **after 2.1.0**; the native packages as **FrameReader.Native.<rid>** with ShotDetector.Native.<rid>
kept as aliases; changelog fragments in **a folder per package**. Plan: docs/frame-reader-product.md.
