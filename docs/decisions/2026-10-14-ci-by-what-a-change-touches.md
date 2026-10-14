# CI by what a change touches: computed from the built assemblies (2026-10-14)

Plan step 5 (docs/frame-reader-product.md): a pull request that changes only FrameReader code ShotDetector's detection
never runs skips the heavy jobs (AOT, ARM, Alpine, parity, real-world, mutation). Asked how to know which code that
is: **computed from the built DLLs** (tools/reach: ShotDetector.dll's and FastYuv's references into FrameReader, then
FrameReader's IL, fields and signatures, mapped to source files), over a hand-kept list in one script (which rots when
ShotDetector starts using something) or keeping today's list. The CLI's own uses (its audio commands) don't count:
they can't move ShotDetector's results. The computed list found the hand-kept one wrong: AudioReader runs for the
reference tone.
