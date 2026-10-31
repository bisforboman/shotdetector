# Decoded frames that outlive the next one (2026-10-17)

To take BGR conversion off ShotDetector's decoding thread, FrameReader gets a reference to a decoded frame
(docs/frame-references.md). Asked: the type's name, **DecodedFrame** (over FrameRef, VideoFrame); its scope, **only
FrameDecoder.TakeFrame and BgrConverter overloads now** (over VideoFrameReader too in the same release); and ShotDetector's
conversion workers, **on by default with a threshold**: one worker on small frames, more on HD only where measured
faster (over opt-in first, as parallel detection started).

Since the same day ([2026-10-17-decoded-frame-without-workers.md](2026-10-17-decoded-frame-without-workers.md)) the
conversion workers are dropped (no gain measured); one conversion thread came back on 2026-10-22
([2026-10-22-decode-split.md](2026-10-22-decode-split.md)).
