# DetectionProgress's diagnostics in 2.0 (2026-10-13)

Hiding FramePipeline was planned with both of DetectionProgress's diagnostic fields going, but issue #42 had asked for
the decoder in progress reports (streaming callers see it before any frame). Asked: **keep DecodesInProcess, drop
Pipeline** (the ActivitySource tag keeps the pipeline for tracing), over dropping both.
