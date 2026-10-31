# Warnings in the library's result (2026-10-18)

### Question

Since #386, `shotdetect` warns on stderr when an https HLS playlist was decoded by the ffmpeg executable: FFmpeg's HLS
demuxer opens the segments, keys and variants without passing tls_verify on, so they're fetched with no certificate
check (in-process, FrameReader's io_open wrapper checks them). Library users don't see it. Should `DetectionResult`
carry warnings (new public API; the library had none) so they do?

### Choices

1. `DetectionResult.Warnings`: a list of sentences, empty by default; the CLI prints each as "Warning: ..." (chosen).
2. CLI only, as #386.

### Answer

**1**. The library fills it (the same check, now in VideoReader), the CLI prints it. Scoped out: a local playlist
whose segments are https (not decided), and DetectStreamAsync, which has no result object (its callers don't get
the warnings; not widened).

Both scope-outs were settled the next day: DetectStreamAsync gets `DetectionOptions.Warnings`
([2026-10-19-warnings-as-they-happen.md](2026-10-19-warnings-as-they-happen.md)), and playlists:
[2026-10-19-hls-warning-http-playlists.md](2026-10-19-hls-warning-http-playlists.md).
