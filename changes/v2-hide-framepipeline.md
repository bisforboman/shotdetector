### Breaking changes

- `FramePipeline` is internal, and `DetectionProgress.Pipeline` is gone: how frames reach the detectors is internal
  plumbing (the `shotdetector.pipeline` ActivitySource tag still records it). `DetectionProgress.DecodesInProcess` stays.
