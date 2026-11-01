# The VideoRag sample moves to its own repository (2026-11-01)

The first slice of issue #635 (a Microsoft.Extensions.DataIngestion reader with a section per shot, #641) was built
as `examples/VideoRag` with `tests/VideoRag.Tests` (docs/decisions/2026-10-31-videorag-sample.md, choice 1). A day
later the owner reconsidered where it lives.

## Where the VideoRag sample lives

The rest of the idea (transcripts with Whisper.net, the StanzaSharp chunker of bisforboman/StanzaSharp#233) ties
ShotDetector, FrameReader and StanzaSharp together, and DataIngestion ships a preview every month. shotdetector-web was
considered and ruled out: a static browser page has no model endpoints, vector store or FFmpeg libraries for the stills.

### Choices

1. **Its own repository, bisforboman/shotdetector-videorag** (public, MIT), on ShotDetector from nuget.org (3.25.0;
   nothing the sample uses changed after that release), with its own CI and Dependabot. Issue #635 moves with it.
2. Keep it in `examples/VideoRag`, built against the source.

### Answer

**1** (the owner, 2026-11-01), with the name shotdetector-videorag and #635 transferred there. This repository drops
the two projects, their four package versions and their solution entries; README.md links the new repository.
