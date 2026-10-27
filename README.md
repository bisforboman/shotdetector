# bench-data

The history of ShotDetector's weekly benchmark (`.github/workflows/benchmark.yml` on
[bisforboman/shotdetector](https://github.com/bisforboman/shotdetector)), as machine-readable JSON, so a page can
chart the trend. This branch is an orphan: it shares no history with `main` and holds nothing else. Only the workflow
writes to it (runs on `main`: the weekly schedule and dispatches there), adding commits and never rewriting them;
its pushes start no workflows. This README is copied from `benchmarks/bench-data.md` on `main` at every run.

## Files

- `runs/<date>-<run id>-<rid>.json`: one file per run and runner (`linux-x64`, `linux-arm64`); `<date>` is the UTC
  day the runner recorded it. A re-run of the same workflow run replaces its files (`run_attempt` says which).
- `runs/index.json`: every run file's name, sorted (raw.githubusercontent.com has no directory listing).

Each run's summary also draws these numbers as Mermaid charts (`tools/bench_chart.py`): every runner's ratio to
scenedetect per film above its tables, and, in the publish job, the trend over the last runs on this branch.

## Schema (version 1)

```text
schema          1; raised when a field changes meaning or goes away (new fields don't raise it)
run_id          the workflow run's id (github.com/bisforboman/shotdetector/actions/runs/<run_id>)
run_attempt     the run's attempt
event           schedule | workflow_dispatch
ref             the branch (main)
date            when the runner recorded the file, UTC, ISO 8601 (2026-10-25T03:58:12Z)
commit          the commit measured (full sha)
version         ShotDetector's newest version in that commit's CHANGELOG.md (may be unreleased work on top of it)
runner          { rid, nproc, cpu (lscpu's model name), memory_gb }
tools           { ffmpeg (`ffmpeg -version`'s first line; the CLI decodes with its bundled FFmpeg libraries),
                  dotnet (SDK version), scenedetect (the installed version bench.py ran) }
variants[]      one per tools/bench.py run:
                { variant: content | adaptive | content-stats | content-2cpu,
                  detector, stats (both tools wrote per-frame stats), runs (best of N by wall time),
                  threads (null: shotdetect's default), fast_yuv, cpus (CPUs the run could use: 2 under taskset),
                  scenedetect }
rows[]          one per film x tool x variant, bench.py's table:
                { variant, detector, stats, cpus,
                  video (file name), frames,
                  tool ("scenedetect 0.7.2", "shotdetect", "shotdetect --parallel", "shotdetect --sequential",
                        "shotdetect --decoder process"; without the table's " --stats" suffix: see stats),
                  in_process (shotdetect said it decoded in-process), chunks (how many, null: one pass),
                  wall_s (best run), fps (frames / wall_s), cpu_s (user + system of the process tree, that run),
                  peak_mb (peak RSS of the process tree, highest over the runs),
                  scenedetect_wall_s (the film's scenedetect wall_s in the same variant),
                  ratio (wall_s / scenedetect_wall_s: below 1 is faster than scenedetect) }
micro           benchmarks/micro on the films and the synthetic set, or null when it didn't run:
                { rounds, cpus, arch, inputs_sha256, outputs_sha256,
                  cases[]: { set (real | synthetic), case, us_per_frame (best round), outputs_sha256_16 } }
wasm            benchmarks/wasm (x64 only; null or missing elsewhere and in older files): ShotDetection.Detect on
                decoded frames (FastYuv's converter) on browser-wasm, AOT-compiled, under Node, and natively on the same
                frames (the first `frames` of the film, pre-decoded to raw yuv420p and read from a file):
                { runtime, native_runtime (.NET's description), node (`node --version`), aot (true), runs (best of N),
                  cases[]: { film (file name), detector (content | adaptive), stats, width, height, frames,
                             wall_ms (best run), read_ms (reading the frames from the file, in that run),
                             detect_ms (wall_ms - read_ms), ns_per_pixel (detect_ms per pixel of all frames),
                             gc_peak_mb (managed heap's peak, GC.GetTotalMemory, over the runs),
                             wasm_heap_mb (the WebAssembly memory at the end of that film's process: its peak),
                             shots, native_detect_ms, native_ns_per_pixel (the native run's, same frames),
                             ratio (ns_per_pixel / native_ns_per_pixel),
                             csv_equal (the scene and stats CSVs equal the native run's, byte for byte) } }
```

A variant, row, micro or wasm section is missing when its step failed in that run. Numbers belong to the runner they were
taken on: GitHub's hosted runners are a mix of CPUs, so compare like with like (`runner.cpu`).
