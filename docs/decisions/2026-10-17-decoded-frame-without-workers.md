# DecodedFrame kept, conversion workers dropped (2026-10-17)

ShotDetector's conversion workers (docs/frame-references.md) measured no gain: decoding, not conversion, sets the pace
on HD. Asked what to do with FrameReader's DecodedFrame (#349), built for them: **merge it as a FrameReader feature and
drop the workers** (frames that outlive Next() are useful to library users), over closing both (no public API without
a use) or measuring on CI's 4-vCPU runners first.
