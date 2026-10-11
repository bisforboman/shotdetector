### Fixed

- FrameReader's `SpectralStats` places each window where ffmpeg's `aspectralstats` does when the stream's timestamps
  have a gap (Ogg Vorbis's first pages can): it counted samples from the start, so times after the gap were early
  (10 ms on a Commons recording). The real-world audio check now covers Vorbis too.
