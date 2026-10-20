### Fixed

- A video file with a `%` in its name that isn't an image sequence's number (a URL-encoded name such as
  `My%20Video.mp4`) failed with `--frame-rate` / `FrameRate` in 3.10.0 ("No demuxer or protocol option "framerate""),
  and always failed through the ffmpeg executable ("Option framerate not found"). Only a pattern FFmpeg reads as an
  image sequence (`frames/%04d.png`: one `%d`, with optional digits; `%%` is a percent sign) is now read as one.
- `Decoder = Auto` (the default) no longer hands an http or https URL that FFmpeg's libraries failed to open to the
  ffmpeg executable: it now refuses it, with the libraries' error and why. The executable fetches the https segments,
  keys and variants an HLS playlist names without checking their certificates, so a playlist whose segments the
  libraries refused (a certificate nobody vouches for) was decoded anyway. `--decoder process`
  (`Decoder = VideoDecoder.FfmpegProcess`) still uses the executable, with its warning. This also applies to DASH over
  http(s) with the FrameReader.Native libraries, which have no DASH demuxer: use `--decoder process` for it. Files and
  other URLs fall back to the executable as before.
- The warning for an http or https HLS playlist decoded by the ffmpeg executable now says it fetches "any https
  segments, keys and variants" without checking their certificates (it is given without reading the playlist).
