### Fixed

- `Audio.Convert`, `Video.Transcode` and `MediaProbe.ProbeJsonAsync`: cancelling now also stops them while FFmpeg waits to open or read the input (a URL that stops sending), instead of only between frames, and a cancelled open throws `OperationCanceledException` rather than returning what it read so far (issue #522).
