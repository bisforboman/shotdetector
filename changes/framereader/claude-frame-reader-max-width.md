### New

- `VideoFrameReaderOptions.MaxWidth`: cap the width and keep the aspect ratio, as ffmpeg's `scale='min(N,iw)':-2`
  (never upscaled, the height rounded to an even number, measured after autorotate), so a capped size no longer needs
  a probe first; the bytes are ffmpeg's for that filter (issue #542).
