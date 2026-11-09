### New

- `ShotDetection.ToneStart(YuvVideo, DetectionOptions?)`: the frame a frame-source run with `TrimReferenceTone` starts
  at, after the leading tone in `YuvVideo.ReferenceTone`, with no frame read. A run stopped before it has no frames of
  its own, which `Merge` refuses as `Detect` does, so a host that stops runs (a Stop button, workers that stop) can
  hold the stop until that frame. Before this, hosts had to estimate it.
