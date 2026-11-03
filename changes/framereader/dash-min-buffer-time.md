### New

- `Remux`: `RemuxOptions.MinBufferTime` sets the DASH MPD's `minBufferTime` (e.g. 2 s, as Shaka Packager writes),
  which FFmpeg's dash muxer derives as twice the last segment's duration and has no option for; null keeps today's
  output byte for byte. Refused with the muxer's `streaming`, `ldash`, `window_size` or `remove_at_exit` (issue #717).
