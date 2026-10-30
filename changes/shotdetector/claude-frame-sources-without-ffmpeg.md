### Fixed

- Frame-source detection (`Detect` and `DetectAsync` on `YuvFrame`s, `ScoreRange`, `ScoreRangeAsync`, `Merge`) no longer reaches FFmpeg's code, so a trimmed app on it alone, such as a browser WebAssembly page, ships without FFmpeg.AutoGen and the FFmpeg paths' code: benchmarks/wasm's AOT bundle is 28% smaller (10.4 MB, from 14.4), 23% gzipped. CI checks it (issue #597).
