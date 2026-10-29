# A seek the container refuses reads from the start (2026-10-29)

Near the start of some files (AVI with B-frames, FLV from x264) ffmpeg's `-ss` seek point falls before the first index
entry. The container refuses the seek; ffmpeg's command line warns ("could not seek to position") and reads on from
where libavformat's generic seek left it, so its first frame can be one decoded without its references, a frame
several seconds in (5.28 s on an x264 FLV with `-g 12`), or none at all. #579 had a fresh `VideoFrameReader` give
those same frames (and a reader that had already read re-seek to the start). Building #583 (raw streams, MPEG-4 in
MP4, FLV), the session chose to read from the start in a fresh reader too, and asked.

### Choices

1. **Keep: read from the start**: always the full decode's frame at the time, fresh or reused reader; documented as a
   deliberate difference from ffmpeg's `-ss` there.
2. Back to ffmpeg's leftovers: a fresh reader gives what `ffmpeg -ss` gives, even no frame or a frame decoded without its
   references.
3. Leftovers unless none: ffmpeg's leftovers, but from the start when they'd give no frame.

### Answer

**1** (the owner, 2026-10-29), as merged in #583. The README's seek section names the ffmpeg behaviours that differ.
