# Loudness verdict: `shotdetect --loudness` (2026-10-31)

Issue #636 (delivery QC), its first slice as docs/ideas/delivery-qc.md proposes: integrated loudness, loudness range,
true and sample peak from FFmpeg's `ebur128` (`peak=true+sample`) through FrameReader's `AudioFilter`, against an
EBU R128 target, with an exit code for pipelines. The note left four questions for this slice open.

## Questions, choices and answers

1. **Where the figures come from.** The note proposed the last chunk's `lavfi.r128.*` metadata. Reading
   `f_ebur128.c` (release/8.1) showed the metadata is set only when a 100 ms block completes, so the samples after the
   last whole block (a file's final partial 100 ms) update the filter's peaks but no metadata: a clip at 3.42 s in a
   3.45 s file is in ffmpeg's Summary (true peak 0.0 dBFS) and not in the last metadata (0.100 linear). The metadata
   also gives peaks only as linear values to three decimals.
   - **The filter's own results** (a small FrameReader API, `AudioFilter.Result(filter, option)`, reading the filter's
     read-only `integrated`, `range`, `lra_low`, `lra_high`, `sample_peak`, `true_peak` at the end: full precision,
     the whole file, what the Summary prints). *Chosen.*
   - The last metadata frame (no FrameReader change; misses the tail's peaks).
2. **Where the code lives.**
   - **A public FrameReader type** (`Loudness.Measure(AudioReader)` giving a `LoudnessResult`, like `ReferenceTone`),
     the verdict and output in the CLI. *Chosen.*
   - The CLI only.
   - A `ShotDetection` wrapper as well.
3. **Which limits are judged.**
   - **Integrated loudness and true peak**: `--loudness-target -23` (LUFS), `--loudness-tolerance 0.5` (LU),
     `--true-peak-max -1` (dBTP), EBU R128's; LRA and sample peak reported, not judged. *Chosen.*
   - Integrated loudness only.
4. **Output and exit code.**
   - **Text on standard output, JSON with `--loudness-json`, exit 3 when out of range** (1 stays errors, 2 usage).
     *Chosen.*
   - The same with exit 1.
   - Text only.

All four: the owner, 2026-10-31.

## How it's checked

`ebur128`'s results are FFmpeg's by construction (our native build runs the filter itself). Tests run the same graph
through the ffmpeg executable on generated clips: integrated loudness and the range equal to the metadata's three
decimals (no block completes after the last metadata, so the filter's final values are the last metadata's), peaks
equal to the Summary's one decimal and, on clips of whole 100 ms blocks, to the metadata's three decimals.
