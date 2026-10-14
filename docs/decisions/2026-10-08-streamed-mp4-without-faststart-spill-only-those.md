# Streamed mp4 without faststart: spill only those (2026-10-08)

Question: streamed mp4s with their headers (moov) last can't be read until the whole stream has arrived; how should
`Detect(Stream)` / `DetectStreamAsync` handle them? Choices: spill only those to a temporary file (recommended), spill
every stream (as fast as a file path, but no shots until the stream is in, always disk), or an opt-in option.
Answer: **spill only those**. Detected from the first bytes (an mdat box before the moov); other streams keep streaming.
