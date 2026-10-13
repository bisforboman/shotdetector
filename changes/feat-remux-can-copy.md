### New

- FrameReader: `Remux.CanCopy(input, output, options)`, whether `Remux.Copy` can stream-copy the selected streams into
  that container (DASH included) and why not, per stream: FFmpeg's own answer from the muxer, nothing written, instead
  of a list of rules that drifts from FFmpeg (issue #241).
