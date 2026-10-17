#!/usr/bin/env bash
# Runs the synthetic set's hashes (--hash-only --set synthetic) and compares them with expected-hash.txt: ci.yml runs it
# on x64 and ARM. The inputs line tells a different synthetic set from a different result.
set -euo pipefail
dir=$(dirname "$0")
out=$(dotnet run -c Release --project "$dir" -- --hash-only --set synthetic)
echo "$out"
fail=0
for what in inputs outputs; do
  expected=$(sed -n "s/^$what  *\([0-9a-f]*\).*/\1/p" "$dir/expected-hash.txt")
  actual=$(echo "$out" | sed -n "s/^all $what: *\([0-9a-f]*\).*/\1/p")
  if [ "$actual" != "$expected" ]; then
    echo "::error::benchmarks/micro: the synthetic $what hash is $actual, expected-hash.txt says $expected"
    fail=1
  fi
done
exit $fail
