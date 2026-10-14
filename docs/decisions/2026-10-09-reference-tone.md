# Reference tone (2026-10-09)

The 1 kHz line-up tone ([reference-tone.md](reference-tone.md)). Asked:

- Scope: **detection and trimming now (recommended)**, not detection first with trimming later. Trimming moves
  detection's start past a leading tone and its end before a trailing one.
- Input: **video files' audio and audio-only files (recommended)**, not only video's audio track.
- API: **FrameReader `ReferenceTone.Find(AudioReader)`, a ShotDetector option and result, CLI `--find-tone` and
  `--trim-tone` (recommended)**; not FrameReader only, nor ShotDetector only.
- Colour bars: **tone only (recommended)**: the audio decides, and trimming the tone trims the bars with it. Not
  extending the segment over a static frame, nor detecting bars on their own.
- Where: **anywhere in the file, trimming only a leading or trailing segment (recommended)**, not only near the ends.
- Minimum length: **5 s, configurable (recommended)**; not 10 s or 20 s.
