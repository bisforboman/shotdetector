# The HLS warning for http playlists too (2026-10-19)

### Question

The backlog's Maybe row "Warn for a local playlist whose segments are https" (left open by
[2026-10-18-library-warnings.md](2026-10-18-library-warnings.md)) turned out not to happen: a local .m3u8 opens with
`-protocol_whitelist file,crypto`, so the ffmpeg executable refuses https segments outright (ffprobe 8.1: "Protocol
'https' not on whitelist 'file,crypto'!"). The real gap is an **http://** playlist: its input gets no `-tls_verify 1`
(ffmpeg refuses an option nothing took on plain http), and the HLS demuxer doesn't pass tls_verify on anyway, so its
https segments, keys and variants are fetched with no certificate check, and no warning says so. In-process,
FrameReader's io_open wrapper checks them. What should happen?

### Choices

1. **Warn for http playlists too**: the executable's HLS warning fires for an http or an https playlist URL (the
   playlist isn't read to see whether it names https entries).
2. Close the row only, as it can't happen.
3. Pick another item.

### Answer

**1. Warn for http playlists too.** `VideoReader.ExecutableHlsWarning` fires for both schemes, with a sentence true for
both: "decoded by ffmpeg, which fetches the playlist's https segments, keys and variants without checking their
certificates; in-process decoding (the FrameReader.Native packages) checks them." The backlog row is closed as done.
