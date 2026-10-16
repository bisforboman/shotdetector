# Security review fixes (2026-10-16)

A security review of the code, the native build and the workflows found nine issues (an HLS-segment certificate gap
fixed in its own PR, and eight here). Asked how to fix them: **two PRs, the code now and the release job separately**,
over one PR for everything (the release workflow change mixed into code fixes) or only the medium ones. For the
release job, which runs the tests with BtbN's ffmpeg (a third-party build) while it holds the NuGet publishing token
and write access: **the tests in a job of their own**, with read-only permissions, over pinning BtbN by hash (smaller,
but third-party code still runs beside the token).
