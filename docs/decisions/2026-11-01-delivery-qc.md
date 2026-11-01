# Delivery QC: `shotdetect qc` (2026-11-01)

Issue #636. docs/ideas/delivery-qc.md scoped one command that reports what a broadcaster or post house checks before
accepting a file: black and frozen spans, bars and tone, pixels outside broadcast range, and loudness. It left seven
questions for the owner (its section 5). The first slice, `shotdetect --loudness`, was built first
([2026-10-31-loudness-verdict.md](2026-10-31-loudness-verdict.md)).

## Settled by #646

Questions 2, 3 and 5 were answered while moving shotdetector-web's managed measures into FrameReader
([2026-10-31-frame-reader-measures.md](2026-10-31-frame-reader-measures.md),
[2026-10-31-frame-reader-measures-widening.md](2026-10-31-frame-reader-measures-widening.md)):

- **2, where the picture measures live:** in FrameReader (`PictureMeter`, `PictureMeasures`), which ShotDetector and
  shotdetector-web both use.
- **3, which loudness:** both exist. FFmpeg's `ebur128` through `Loudness.Measure` gives FFmpeg's figures by
  construction, and that is what `--loudness`, and so `qc`, judges. The managed `LoudnessMeter` (I and LRA exact, true
  peak per BS.1770-4 Annex 2) serves where FFmpeg can't run.
- **5, 10-bit and 4:2:2:** in scope. `PictureMeter` takes 8 to 16 bits and 4:2:0, 4:2:2 and 4:4:4.

## Questions, choices and answers (the owner, 2026-11-01)

1. **Shape** (the note's question 1).
   - **A `qc` subcommand of `shotdetect`**: one public command for black spans, bars and tone, broadcast range
     (signalstats' BRNG and YMIN/YMAX), loudness and freeze. The open counterpart of the owner's private media-qc, built
     on the same FrameReader measures. *Chosen.*
   - Options on the existing command (`--loudness`, `--picture`).
   - A separate tool or package.
2. **Freeze detection** (question 4).
   - **In scope from the start**, mirroring FFmpeg's `freezedetect`. *Chosen.*
   - Later.
3. **Presets and the exit code** (question 6).
   - **Presets EBU R128 (the default), ATSC A/85, and streaming at -14 and -16 LUFS; one exit code**: 0 when every check
     passes, 3 when any is out of range (as `--loudness` today; 1 stays errors, 2 usage). The JSON names every failing
     check. *Chosen.*
   - An exit code per check.
4. **Priority** (question 7).
   - **Next**: built now, slice by slice. *Chosen.*
   - Maybe.
5. **Where black and freeze spans come from.**
   - **The detection pass, through a new ShotDetector API**: `DetectionOptions.BlackDetect` and
     `DetectionResult.BlackSpans`, exact against ffmpeg's `blackdetect`, then freeze spans the same way. The #629/#630
     session builds the API; `qc`'s black and freeze checks are built on it. *Chosen.*
   - A second pass in `qc` through `PictureMeter`.
6. **The presets' tolerances and true-peak limits.**
   - **EBU R128: -23 LUFS ±0.5 LU, -1 dBTP (as `--loudness` today). ATSC A/85: -24 LKFS ±2 LU, -2 dBTP. Streaming -14
     and -16: ±1 LU, -1 dBTP.** Each can be overridden (`--loudness-tolerance`, `--true-peak-max`). *Chosen.*
   - ±1 LU for every preset but EBU's.
   - Streaming presets judge only "not louder than the target".
7. **`--loudness` once `qc` exists.**
   - **An alias, kept**: the loudness-only form of `qc`, with the same output and exit codes, documented as such.
     Nothing breaks and nothing has to be removed later. *Chosen.*
   - Deprecated with a warning, removed at the next major.
8. **Bars and tone.**
   - **Reported, with an optional requirement**: bars and tone spans (with the tone's level) are always listed and
     never fail by default. `--require-bars-tone` fails a file whose first minute doesn't have bars and tone
     overlapping, for deliveries that must carry line-up. *Chosen.*
   - Fail when bars or tone appear outside the first and last minute.
   - Report only.
9. **The report.**
   - **Text and JSON**: a text table on standard output (one row per check: measured, limit, PASS or FAIL, spans) and
     `--json <file>` with every check (name, status, measured, limit, spans), failing ones named. HTML later, as a
     slice of its own. *Chosen.*
   - JSON only.
   - Text, JSON and HTML at once.

## The slices

1. `qc` with loudness and the presets; `--loudness` as its alias.
2. Broadcast range and bars and tone, through `PictureMeter` and `ReferenceTone`.
3. Black spans, once `DetectionResult.BlackSpans` is in.
4. Freeze spans, once the detection pass gives them.
5. The HTML report.

Each check is exact against the FFmpeg filter it mirrors, and tested against it.
