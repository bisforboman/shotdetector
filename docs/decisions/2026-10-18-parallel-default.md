# Parallel detection as the default (2026-10-18)

### Question

Parallel detection (`DetectionOptions.Parallel`, `--parallel`, opt-in since 2026-10-16) gave byte-identical results in
every CI comparison so far (about 41 PR runs on the samples, 43 on three films, the nightly), but only on Linux x64
in-process; never on ARM, Windows or macOS, and the benchmark didn't measure it. Under parallel, `DetectStreamAsync`
delivers every shot at the end, which defeats streaming, and a `bool Parallel` can't tell "default" from "on". How
should it become the default, how does the CLI turn it off, and when?

### Choices

Scope: 1. Detect only, through a new `DetectionOptions.Mode` (Auto/Sequential/Parallel; Auto: parallel for `Detect`,
sequential for `DetectStreamAsync`), `Parallel` obsolete (recommended); 2. `Parallel` true by default everywhere;
3. hold.
CLI: 1. `--sequential`, `--parallel` kept as a hidden no-op (recommended); 2. `--no-parallel`; 3. `--chunks 1`.
Timing: 1. after the comparison passes on ARM and a Windows/macOS nightly (recommended); 2. right after the coverage PR.

### Answer

**Mode enum, Detect only; `--sequential`; after ARM and Windows/macOS pass.** First a PR extends the comparison
(ci.yml's arch job on ARM; parity-os.yml on Windows and macOS with our FFmpeg build) and puts `--parallel` in the
weekly benchmark; the flip follows once those have passed. A minor release (results are identical by design).
