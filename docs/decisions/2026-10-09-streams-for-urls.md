# Streams for URLs (2026-10-09)

`Streams` and `HasAudio` were null for all streamed input, URLs included. Asked: **give URLs `Streams` through one
ffprobe call on first use, another request to the server only when it's read (recommended)**, not leaving them null.
A `Stream` stays null: it can't be read again.
