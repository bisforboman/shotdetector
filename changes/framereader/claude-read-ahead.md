### New

- `VideoFrameReaderOptions.ReadAhead`: decodes up to N frames ahead on a thread of the reader's own while the caller
  works on the frame it has (encoding, writing, analysis), so a loop like `TryReadForwardTo` + `ImageEncoder.Encode` +
  `File.WriteAllBytes` overlaps its per-frame work with decoding as ffmpeg's command line does (issue #566). The frames,
  tags and errors are those without it, and a frame stays valid until the next read; 0 (the default) decodes in the
  read as before. 4 is a good start where decoding leaves cores free; each frame ahead holds a decoded frame's memory.
