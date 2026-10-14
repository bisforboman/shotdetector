# CLI and licensing (2026-10-01 – 2026-10-03)

### Question

The swscale port (yuv420p→BGR, exact) is LGPL; the rest is MIT. How to ship?

### Choices

1. Everything LGPL.
2. MIT core `ShotDetector` + separate LGPL `ShotDetector.FastYuv` package; the CLI skipped for now.
3. No fast path at all.

### Answer

**2** ("Let's do 2 then"); the CLI was shipped later (2026-10-03) as a dotnet tool and AOT binaries built
without the LGPL package (a dev-only `WithFastYuv` build exists for the compare script).
