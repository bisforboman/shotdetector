#!/bin/bash
# ci.yml's `changes` job: what a pull request's commit touches, as its outputs (docs_only=true, light=true,
# wasm=true). Light: nothing that can move ShotDetector's results, so the heavy jobs don't start: docs, API baseline
# bumps in Directory.Build.props, the benchmarks (with benchmark.yml's tools/bench*.py, and tools/bench-cpus.ps1, run by
# hand), workflows CI never runs, FrameReader's tests, and FrameReader source files that ShotDetector's detection never runs (tools/reach
# reads that from the built assemblies, so it can't go stale). wasm: what ci.yml's wasm-trim job checks could change (the
# libraries the frame-source path is built from, benchmarks/wasm, the check itself), light or not.
set -euo pipefail
files=$(git diff --name-only HEAD^1 HEAD)
[ -z "$files" ] && exit 0
# A Directory.Build.props change that only moves the API baselines (ApiBaselineVersion, FrameReaderApiBaselineVersion,
# after a stable release) is light: no results can move, and build still packs against the new baseline.
if echo "$files" | grep -qx 'Directory.Build.props' \
  && ! git diff -U0 HEAD^1 HEAD -- Directory.Build.props | grep -E '^[-+]' | grep -vE '^(\+\+\+|---) ' \
    | grep -qvE '^[-+][[:space:]]*<(FrameReader)?ApiBaselineVersion>[^<]*</(FrameReader)?ApiBaselineVersion>[[:space:]]*$'; then
  files=$(echo "$files" | grep -vx 'Directory.Build.props' || true)
  [ -z "$files" ] && { echo light=true; exit 0; }
fi
if ! echo "$files" | grep -qvE '^docs/|\.md$'; then echo docs_only=true; fi
if echo "$files" | grep -vE '\.md$' | grep -qE '^src/(ShotDetector|ShotDetector\.FastYuv|FrameReader)/|^benchmarks/wasm/|^Directory\.(Build|Packages)\.(props|targets)$|^\.github/workflows/ci\.yml$|^tools/(trim_path\.py|ci-changes\.sh)$'; then
  echo wasm=true
fi

rest=$(echo "$files" | grep -vE '^docs/|\.md$|^\.claude/|^\.githooks/|^\.github/workflows/(release|benchmark|frame-reader-bench|upstream|dependabot-auto-merge|parity-os|codeql|compare)\.yml$|^\.github/dependabot\.yml$|^benchmarks/|^tools/bench(_[a-z]+)?\.py$|^tools/bench-cpus\.ps1$|^tests/FrameReader\.Tests/' || true)
if [ -z "$rest" ]; then echo light=true; exit 0; fi
# Anything else but FrameReader's source runs everything.
if echo "$rest" | grep -qvE '^src/FrameReader/[^/]*\.cs$'; then exit 0; fi

out=${RUNNER_TEMP:-/tmp}/reach
dotnet build src/ShotDetector.FastYuv -c Release -v q -o "$out/sd" >&2
dotnet build tools/reach -c Release -v q -o "$out/tool" >&2
dotnet "$out/tool/Reach.dll" "$out/sd/FrameReader.dll" src/FrameReader "$out/sd/ShotDetector.dll" "$out/sd/ShotDetector.FastYuv.dll" > "$out/reached.txt"
echo "ShotDetector runs these FrameReader files:" >&2
cat "$out/reached.txt" >&2
if ! echo "$rest" | grep -qxFf "$out/reached.txt"; then echo light=true; fi
