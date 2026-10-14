# The public API for 1.0 (2026-10-08)

After 0.10.0 the user chose "1.0 preparation (Recommended)" over exact 10-bit colour, scenedetect CLI parity and more
stream checks, then answered the API review:

- Positional records (VideoInfo, DetectionResult, DetectionProgress): **init-only properties (recommended)** over
  keeping them positional, so new fields after 1.0 aren't breaking. Shot and Fps stay positional (complete).
- VideoReader: **a read-only result view (recommended)** (constructors and Frames() internal) over keeping it all public.
- Stats.Set: **internal (recommended)** over public.
- Breaking-change guard: **SDK package validation (recommended)** over PublicApiAnalyzers or both. The baseline is
  empty until 1.0 is on nuget.org, then the last stable version (`ApiBaselineVersion`).
