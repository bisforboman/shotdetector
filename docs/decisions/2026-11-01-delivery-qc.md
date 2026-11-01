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

10. **Broadcast range: what fails** (asked when building slice 2).
    - **Any frame's BRNG above `--brng-max`, 1% by default**: signalstats' own measure, so exact by construction; the
      worst frame's time and the file's YMIN and YMAX reported. A few stray pixels pass, a lifted or crushed picture
      fails. *Chosen.*
    - EBU R103's luma limits on YMIN and YMAX (5 and 246 at 8 bits): one pixel can fail a file.
    - Report only.
11. **Colour bars in the range check.** Building slice 2 showed ffmpeg's own `smptebars` at 1.25% BRNG (the PLUGE's
    below-black strip is deliberate), so every file with SMPTE line-up would fail the 1% rule.
    - **Leave the frames `PictureMeter` finds as colour bars out of the range check**, and count them in the report.
      *Chosen.*
    - Count them (files with bars fail unless `--brng-max` is raised).
    - Raise the default to 2%.
12. **Black spans: what fails** (asked before slice 3).
    - **Mid-programme only**: a black span of `--black-min` (2 s) or longer that doesn't touch the first or last
      `--edge` seconds (10 s) fails; black at the head and tail (slates, fades) is listed, not judged. *Chosen.*
    - Any black span, head and tail included.
    - Report only.
13. **Frozen spans: what fails.**
    - **As black**: a freeze of `--freeze-min` (2 s) or longer away from the first and last `--edge` seconds fails; a
      freeze inside a black span or colour bars isn't judged, since line-up and black are static by design. *Chosen.*
    - Any freeze.
    - Report only.

## The slices

1. `qc` with loudness and the presets; `--loudness` as its alias.
2. Broadcast range and bars and tone, through `PictureMeter` and `ReferenceTone`.
3. Black spans, once `DetectionResult.BlackSpans` is in.
4. Freeze spans, once the detection pass gives them.
5. The HTML report.

Each check is exact against the FFmpeg filter it mirrors, and tested against it.
