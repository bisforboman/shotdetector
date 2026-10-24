### New

- `ReferenceToneFinder` and `WaveformBuilder` find reference tone and make waveform points from samples you decode yourself, a chunk at a time (`Add`, then `Finish`), without loading FFmpeg: the same results `ReferenceTone.Find` and `WaveformData.Read` give, which now run through them.
