"""One benchmark run's numbers as one JSON file, for the bench-data branch (benchmark.yml).

Combines bench.py's --json files (one per variant: all CPUs, --stats, 2 CPUs...), benchmarks/micro's output and
the run's context (commit, version, runner, tool versions) into the schema the bench-data README describes.

Usage: python tools/bench_record.py --rid linux-x64 --out run.json [--micro micro.txt]
           [--wasm wasm.json... --wasm-native native.json...] label=bench.json...
The labels name the variants (benchmark.yml: content, adaptive, content-stats, content-2cpu).
`--micro-only micro.txt` prints the parsed micro output, to check the parser.
`--wasm-report` (with --wasm and --wasm-native, no --out) prints the WebAssembly cases as a Markdown table and exits 1
when a case's CSVs differ from the native run's or a case is missing on either side (benchmarks/wasm)."""
import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = 1
ROOT = Path(__file__).resolve().parent.parent

# benchmarks/micro/Program.cs: $"{c.Set,-9} {c.Name,-48} {c.Best,9:F1}  {c.Hash[..16]}"
CASE = re.compile(r"^(real|synthetic)\s+(.+?)\s+(\d+\.\d+)\s+([0-9a-f]{16})$")
TOTAL = re.compile(r"^all (inputs|outputs):\s+([0-9a-f]{64})$")
ROUNDS = re.compile(r"^best of (\d+) rounds, (\d+) CPUs, (\w+);")


def parse_micro(text: str) -> dict:
    """The micro benchmark's cases (us per frame, output hash) and its hashes over all inputs and outputs."""
    out = {"rounds": None, "cpus": None, "arch": None, "inputs_sha256": None, "outputs_sha256": None, "cases": []}
    for line in text.splitlines():
        line = line.rstrip()
        if m := CASE.match(line):
            out["cases"].append({"set": m[1], "case": m[2], "us_per_frame": float(m[3]), "outputs_sha256_16": m[4]})
        elif m := TOTAL.match(line):
            out[f"{m[1]}_sha256"] = m[2]
        elif m := ROUNDS.match(line):
            out["rounds"], out["cpus"], out["arch"] = int(m[1]), int(m[2]), m[3]
    return out


def wasm_section(wasm_files: list[str], native_files: list[str]) -> dict:
    """benchmarks/wasm's output: the WebAssembly run's cases (one file per film, so each has its process's WebAssembly
    memory) with the native run's on the same frames, matched by film, detector and stats."""
    def load(files):
        runs = [json.loads(l) for f in files if Path(f).exists()
                for l in Path(f).read_text(encoding="utf-8").splitlines() if l.startswith("{")]
        cases = {}
        for r in runs:
            for c in r["cases"]:
                cases[(c["film"], c["detector"], c["stats"])] = {**c, "heap_bytes": r.get("heap_bytes")}
        return runs, cases

    wasm_runs, wasm = load(wasm_files)
    native_runs, native = load(native_files)
    out = {"runtime": wasm_runs[0]["runtime"] if wasm_runs else None,
           "native_runtime": native_runs[0]["runtime"] if native_runs else None,
           "node": sh("node", "--version").strip() or None, "aot": True,
           "runs": wasm_runs[0]["runs"] if wasm_runs else None, "cases": []}
    for key in list(wasm) + [k for k in native if k not in wasm]:
        w, n = wasm.get(key), native.get(key)
        c = w or n
        out["cases"].append({
            "film": c["film"], "detector": c["detector"], "stats": c["stats"], "width": c["width"], "height": c["height"],
            "frames": c["frames"],
            "wall_ms": w and w["wall_ms"], "read_ms": w and w["read_ms"], "detect_ms": w and w["detect_ms"],
            "ns_per_pixel": w and w["ns_per_pixel"], "gc_peak_mb": w and round(w["gc_peak_bytes"] / 2**20, 1),
            "wasm_heap_mb": w and w["heap_bytes"] and round(w["heap_bytes"] / 2**20, 1), "shots": c["shots"],
            "native_detect_ms": n and n["detect_ms"], "native_ns_per_pixel": n and n["ns_per_pixel"],
            "ratio": round(w["ns_per_pixel"] / n["ns_per_pixel"], 2) if w and n and n["ns_per_pixel"] else None,
            "csv_equal": bool(w and n and w["frames"] == n["frames"] and w["scene_csv_sha256"] == n["scene_csv_sha256"]
                              and w["stats_csv_sha256"] == n["stats_csv_sha256"]),
        })
    return out


def wasm_report(section: dict) -> tuple[str, bool]:
    """The step summary's table, and whether every case's CSVs equal the native run's."""
    lines = ["| film | detector | frames | wasm ns/px | native ns/px | wasm / native | read ms | wasm memory MB | CSVs equal |",
             "|---|---|---:|---:|---:|---:|---:|---:|---|"]
    for c in section["cases"]:
        lines.append(f"| {c['film']} | {c['detector']}{' --stats' if c['stats'] else ''} | {c['frames']} | "
                     f"{c['ns_per_pixel']} | {c['native_ns_per_pixel']} | {c['ratio']} | {c['read_ms']} | "
                     f"{c['wasm_heap_mb']} | {'yes' if c['csv_equal'] else '**no**'} |")
    ok = bool(section["cases"]) and all(c["csv_equal"] for c in section["cases"])
    return "\n".join(lines) + "\n", ok


def sh(*cmd: str) -> str:
    try:
        return subprocess.run(cmd, capture_output=True, text=True, check=False).stdout
    except OSError:
        return ""


def version() -> str | None:
    """ShotDetector's version: CHANGELOG.md's newest "## X.Y.Z" (Directory.Build.props holds a placeholder; release.yml
    reads the CHANGELOG too)."""
    for line in (ROOT / "CHANGELOG.md").read_text(encoding="utf-8").splitlines():
        if m := re.match(r"^## (\d+\.\d+\.\d+\S*)", line):
            return m[1]
    return None


def runner(rid: str) -> dict:
    cpu = next((l.split(":", 1)[1].strip() for l in sh("lscpu").splitlines() if l.startswith("Model name:")), None)
    mem = next((int(l.split()[1]) for l in Path("/proc/meminfo").read_text().splitlines() if l.startswith("MemTotal:")), None) \
        if Path("/proc/meminfo").exists() else None
    return {"rid": rid, "nproc": os.cpu_count(), "cpu": cpu, "memory_gb": round(mem / 2**20, 1) if mem else None}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("variants", nargs="*", help="label=bench.json")
    ap.add_argument("--rid")
    ap.add_argument("--out")
    ap.add_argument("--micro")
    ap.add_argument("--micro-only")
    ap.add_argument("--wasm", action="append", default=[])
    ap.add_argument("--wasm-native", action="append", default=[])
    ap.add_argument("--wasm-report", action="store_true")
    a = ap.parse_args()
    if a.micro_only:
        print(json.dumps(parse_micro(Path(a.micro_only).read_text(encoding="utf-8")), indent=1))
        return
    if a.wasm_report:
        table, ok = wasm_report(wasm_section(a.wasm, a.wasm_native))
        print(table, end="")
        sys.exit(0 if ok else 1)
    if not a.rid or not a.out:
        sys.exit("--rid and --out are needed")
    env = os.environ.get
    ffmpeg = sh("ffmpeg", "-version").splitlines()
    record = {
        "schema": SCHEMA,
        "run_id": int(env("GITHUB_RUN_ID", "0")),
        "run_attempt": int(env("GITHUB_RUN_ATTEMPT", "1")),
        "event": env("GITHUB_EVENT_NAME"),
        "ref": env("GITHUB_REF_NAME"),
        "date": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "commit": env("GITHUB_SHA") or sh("git", "-C", str(ROOT), "rev-parse", "HEAD").strip(),
        "version": version(),
        "runner": runner(a.rid),
        "tools": {"ffmpeg": ffmpeg[0] if ffmpeg else None, "dotnet": sh("dotnet", "--version").strip() or None,
                  "scenedetect": None},
        "variants": [],
        "rows": [],
        "micro": None,
        "wasm": None,
    }
    for v in a.variants:
        label, _, path = v.partition("=")
        if not Path(path).exists():
            print(f"{path}: missing, variant {label} left out", file=sys.stderr)
            continue
        bench = json.loads(Path(path).read_text(encoding="utf-8"))
        rows = bench.pop("rows")
        record["tools"]["scenedetect"] = record["tools"]["scenedetect"] or bench.get("scenedetect")
        record["variants"].append({"variant": label, **bench})
        record["rows"] += [{"variant": label, "detector": bench.get("detector"), "stats": bench.get("stats"),
                           "cpus": bench.get("cpus"), **r} for r in rows]
    if a.micro and Path(a.micro).exists():
        record["micro"] = parse_micro(Path(a.micro).read_text(encoding="utf-8"))
    if any(Path(f).exists() for f in a.wasm + a.wasm_native):
        record["wasm"] = wasm_section(a.wasm, a.wasm_native)
    Path(a.out).write_text(json.dumps(record, indent=1) + "\n", encoding="utf-8")
    print(f"{a.out}: {len(record['rows'])} rows in {len(record['variants'])} variants, "
          f"{len(record['micro']['cases']) if record['micro'] else 0} micro cases, "
          f"{len(record['wasm']['cases']) if record['wasm'] else 0} wasm cases")


if __name__ == "__main__":
    main()
