### New

- `DetectionOptions.Warnings`: an `IProgress<string>` told each warning as soon as it is known (the https HLS playlist
  read by the ffmpeg executable, why `Mode = Parallel` read sequentially, a fallback from chunks), so
  `DetectStreamAsync`, which has no result, gets them too. Detect still returns them in `DetectionResult.Warnings`.
