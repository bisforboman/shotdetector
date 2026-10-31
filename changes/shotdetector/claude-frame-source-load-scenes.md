### New

- `ShotDetection.LoadScenes(YuvVideo, TextReader, column, options)`: load-scenes for frames from the caller, with the
  video's properties from `YuvVideo` and the scene list as text (a `StringReader` over a list edited in a browser), so
  a frame-source host can export again from an edited cut list. It gives `LoadScenes(path)`'s shots, cuts, positions
  and exports byte for byte, and needs no converter (issue #647, docs/decisions/2026-10-31-load-scenes-frame-source.md).
