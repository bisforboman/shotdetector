### New

- FrameReader: `RemuxOptions.MuxerOptions`, FFmpeg muxer options as ffmpeg's output options (e.g.
  `movflags=+faststart`). With `Container = "dash"`: MPEG-DASH output, fMP4 segments and a static MPD, the files
  `ffmpeg -c copy -f dash` writes with the same options (issue #171). ShotDetector.Native has the dash muxer now.

### Fixed

- `Remux.Copy` to an .mpd on Windows wrote the segments into the working directory when the path used `\`
  (FFmpeg's dash muxer looks for `/`); they now go next to the manifest.
