### Fixed

- Progress: the first report's `Position` could be a tick below zero (an MP3's start after the encoder delay); it starts
  at zero (issue #274).
- `ReferenceTone.Find`: progress on an input shorter than `SearchDuration` reports the input's duration, so `Fraction`
  moves at the real pace and the last report is at the end of the input (issue #273).
