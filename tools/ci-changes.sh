#!/bin/bash
# The `changes` jobs of ci.yml, realworld.yml and mutation.yml: what a pull request's commit touches, as their
# outputs (docs_only=true, light=true). Light: nothing that can move ShotDetector's results, so the heavy jobs don't
# start: docs, the benchmarks (with benchmark.yml's tools/bench*.py), workflows CI never runs, FrameReader's tests,
# and FrameReader source files that ShotDetector's detection never runs (tools/reach reads that from the built
# assemblies, so it can't go stale).
set -euo pipefail
files=$(git diff --name-only HEAD^1 HEAD)
[ -z "$files" ] && exit 0
if ! echo "$files" | grep -qvE '^docs/|\.md$'; then echo docs_only=true; fi

rest=$(echo "$files" | grep -vE '^docs/|\.md$|^\.claude/|^\.githooks/|^\.github/workflows/(release|benchmark|frame-reader-bench|upstream|dependabot-auto-merge|parity-os|codeql|compare)\.yml$|^\.github/dependabot\.yml$|^benchmarks/|^tools/bench(_[a-z]+)?\.py$|^tests/FrameReader\.Tests/' || true)
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
