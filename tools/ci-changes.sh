#!/bin/bash
# ci.yml's `changes` job: what a pull request's commit touches, as its outputs (docs_only=true, light=true,
# wasm=true). Light: nothing that can move ShotDetector's results, so the heavy jobs don't start: docs, an API baseline
# bump in Directory.Build.props, the benchmarks (with benchmark.yml's tools/bench*.py, and tools/bench-cpus.ps1, run by
# hand), and workflows CI never runs. A FrameReader version bump (Directory.Packages.props) runs everything: it can move
# results. wasm: what ci.yml's wasm-trim job checks could change (the libraries the frame-source path is built from, the
# packages' versions, benchmarks/wasm, the check itself), light or not.
set -euo pipefail
files=$(git diff --name-only HEAD^1 HEAD)
[ -z "$files" ] && exit 0
# A Directory.Build.props change that only moves the API baseline (ApiBaselineVersion, after a stable release) is
# light: no results can move, and build still packs against the new baseline.
if echo "$files" | grep -qx 'Directory.Build.props' \
  && ! git diff -U0 HEAD^1 HEAD -- Directory.Build.props | grep -E '^[-+]' | grep -vE '^(\+\+\+|---) ' \
    | grep -qvE '^[-+][[:space:]]*<ApiBaselineVersion>[^<]*</ApiBaselineVersion>[[:space:]]*$'; then
  files=$(echo "$files" | grep -vx 'Directory.Build.props' || true)
  [ -z "$files" ] && { echo light=true; exit 0; }
fi
if ! echo "$files" | grep -qvE '^docs/|\.md$'; then echo docs_only=true; fi
if echo "$files" | grep -vE '\.md$' | grep -qE '^src/(ShotDetector|ShotDetector\.FastYuv)/|^benchmarks/wasm/|^Directory\.(Build|Packages)\.(props|targets)$|^\.github/workflows/ci\.yml$|^tools/(trim_path\.py|ci-changes\.sh)$'; then
  echo wasm=true
fi

rest=$(echo "$files" | grep -vE '^docs/|\.md$|^\.claude/|^\.githooks/|^\.github/workflows/(release|benchmark|upstream|dependabot-auto-merge|parity-os|codeql|compare|vulnerable-packages)\.yml$|^tools/vulnerable-packages\.py$|^\.github/dependabot\.yml$|^benchmarks/|^tools/bench(_[a-z]+)?\.py$|^tools/bench-cpus\.ps1$' || true)
if [ -z "$rest" ]; then echo light=true; fi
