### New

- FrameReader: DASH segment names with `$ContentType$` (`video`, `audio`, `text`) and folders, for Shaka Packager's
  `video/` and `audio/` layout: `Remux.Copy` creates the folders and puts the content type into the files' names and
  the MPD's templates, which FFmpeg's dash muxer can't (issue #191). The files are otherwise ffmpeg's, byte for byte.
