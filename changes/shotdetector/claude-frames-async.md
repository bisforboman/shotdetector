### New

- `ShotDetection.DetectAsync(YuvVideo, IAsyncEnumerable<YuvFrame>, ...)`: detection on frames from an asynchronous source, run on the caller's own awaits (no other thread), so a single-threaded host such as browser WebAssembly stays responsive and can cancel between frames; the result is the synchronous `Detect`'s for the same frames (issue #492).
