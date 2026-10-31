# VideoWriter's rare CI mismatch (2026-10-10)

About 1 CI test run in 10-20 a VideoWriter file differs slightly from ffmpeg's (same x264 settings, same input
frames, valid H.264; cause not found, see the backlog). Asked how the CI check should treat it: **keep failing
hard**, chosen over retry-then-warn (recommended) and comparing decoded frames: a failed run is re-run, and each
failure is a data point.

Since 2026-10-12 (docs/backlog-done.md) the cause is known: x264's AVX-512 code with .NET's tiered JIT, about 1 run in
250 on AVX-512 runners; CI's VideoWriter step sets `DOTNET_TieredCompilation=0`.
