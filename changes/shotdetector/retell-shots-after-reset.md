### Fixed

- `DetectionOptions.OnShot` and `DetectStreamAsync` on a file whose timestamps start over (two encodes joined): they told
  shots as cuts arrived, and the later encode's cuts, sorted into the result, split shots already told, so a listener's
  list differed from `DetectionResult.Shots`. Such a file shows it before the first frame (its packets), so its shots are
  now all told at the end, exactly the result's; MPEG-PS decoded in-process too, as its timestamps arrive with the frames.
  A Stream decoded in-process still can't know it ahead (OnShot's documentation says so).
