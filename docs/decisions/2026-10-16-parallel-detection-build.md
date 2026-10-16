# Parallel detection: build it, `Parallel` and `--parallel`, half the CPUs up to 4 (2026-10-16)

After the prototype (docs/parallel-detection.md, "Prototype"), asked **whether to build it properly**: build it, opt-in,
for 8 or more cores (over any core count, or shelving it), and **measure on a GitHub runner first** (over trusting the
desktop numbers). The runner (4 vCPU) then showed 2 chunks paying there too, so two more questions:

- **How it's switched on:** one bool, `DetectionOptions.Parallel`, and `--parallel`, with `DetectedVideo.Chunks`
  reporting what ran (over an `int? Chunks` / `--chunks <n|auto>`, more surface under semver; or a `ParallelMode` enum
  with room for an on-by-default value).
- **The automatic count:** half the CPUs, at most 4, from 4 CPUs on (over sequential below 8 cores and
  min(4, cores / 4) above, as first agreed): the runners gain 1.4-1.6x with 2 chunks on light-decode video, and no
  machine measured gained past 4 chunks.
