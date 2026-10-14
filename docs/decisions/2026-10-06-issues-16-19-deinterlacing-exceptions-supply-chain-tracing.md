# Issues #16-#19: deinterlacing, exceptions, supply chain, tracing (2026-10-06)

### Question

Four requests from a production user: deinterlace interlaced sources before analysis (#16), exception types that
tell a missing ffmpeg from bad input (#17), supply-chain signals (#18: signing, SourceLink, 1.0, a second
maintainer), and an OpenTelemetry `ActivitySource` (#19). Which to do, in what shape, and how to sign?

### Choices

- Which: #17, #19, #18 (SourceLink and symbols), #16.
- Deinterlacing: a `Deinterlace` option (yadif); a free-form ffmpeg filter string; both.
- Signing: GitHub build attestations (free, `gh attestation verify`); author-signed NuGet packages (a paid
  certificate); none for now.
- Issue replies: drafted for approval; posted when merged; none.

### Answer

All four issues. **A `Deinterlace` option** (narrow and testable; a free-form filter could change frame counts
and timing). **Build attestations.** **Replies posted when the work merges.** The 1.0 date and a second
maintainer stay with the user.
