### New

- FrameReader: progress for the whole-file jobs, through `AudioConvertOptions`, `RemuxOptions`, `WaveformOptions`
  and `ReferenceToneOptions`'s new `Progress`: one `MediaProgress` (position, duration, fraction) for all, about 10
  times a second and once at the end (issue #242).
- FrameReader: async overloads `Audio.ConvertAsync`, `Remux.CopyAsync`, `WaveformData.ReadAsync`,
  `ReferenceTone.FindAsync` and `MediaProbe.ProbeJsonAsync`, off the calling thread and cancellable (issue #243).
