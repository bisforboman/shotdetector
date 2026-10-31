# Streamed input (2026-10-06)

### Question

Users stream shots out; can the video come in as a stream too, and should URLs and live feeds be supported?

### Choices

1. Stream in → shots out (`Detect(Stream)`, `DetectStreamAsync(Stream)`, CLI `-i -`).
2. URLs and live feeds (ffmpeg reads them; one pass, timestamps from `showinfo`, no seeking).
3. Only files.

### Answer

**1 and 2.** Container headers must come first (mkv/webm/ts/mov, faststart mp4); a plain mp4 gets a clear error;
`StartTime` is refused when streaming; exports need a path.

Since 2026-10-08
([2026-10-08-streamed-mp4-without-faststart-spill-only-those.md](2026-10-08-streamed-mp4-without-faststart-spill-only-those.md))
a streamed mp4 whose headers come last is spilled to a temp file instead of giving an error.
