### New

- `DetectionOptions.OnShot` is told each shot as soon as it is known, on the thread that runs the detection, by every `Detect` overload (a file, a Stream, your own frames): with one detector while the video is still being read, with several at the end.
