### New

- `AudioAnalysis.Run` / `RunAsync`: reference tone, waveform, loudness and silences over one decode of an
  `AudioReader` (each turned on by its options in `AudioAnalysisOptions`, plus a `Chunks` callback for your own
  analysis), each result the one its own call gives on a reader opened with the same options, to the bit. An ingest
  pipeline that ran four analyses decodes the audio once instead of four times (issue #718).
- `AudioReader.DecodesFloat` and `AudioReader.Duration` are public, for building a `WaveformBuilder` as
  `WaveformData.Read` does.
