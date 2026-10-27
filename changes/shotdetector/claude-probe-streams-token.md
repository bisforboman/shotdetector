### Fixed

- `ShotDetection.Probe` and `ProbeAsync`'s cancellation token now also stops reading the list of streams (`VideoInfo.Streams`, `HasAudio`) from a URL that stalls: that second read used to ignore it, in-process and through ffprobe.
