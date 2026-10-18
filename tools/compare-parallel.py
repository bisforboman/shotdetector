"""Parallel detection against a sequential run: the scene list and stats CSVs must be byte-identical.

Usage: python tools/compare-parallel.py <video>... [--chunks N] [--require-chunks] [--report summary.md] [--fast-yuv]

Each video runs with all five detectors at once and --stats, in 0.7.2 and 0.7.1 mode, once with --sequential and once
with --chunks N (default: --parallel, chunked from 4 CPUs on with the automatic count; the CLI's default chunks only
from 8 CPUs on). A run that fell back to sequential (the CLI's summary doesn't
say "in N chunks") is reported; with --require-chunks it fails. Extra shotdetect options: SHOTDETECTOR_OURS_ARGS.
"""

import argparse
import hashlib
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE_NAME = "shotdetect.exe" if sys.platform == "win32" else "shotdetect"
DETECTORS = ["-d", "content", "-d", "adaptive", "-d", "hist", "-d", "hash", "-d", "threshold"]


def run(exe: Path, video: str, args: list[str], out: Path) -> tuple[str, str]:
    """The CSVs' digest and the CLI's summary line."""
    csv, stats = out.with_suffix(".csv"), out.with_suffix(".stats.csv")
    p = subprocess.run([str(exe), "-i", video, *DETECTORS, *args, "-q", "--csv", str(csv), "--stats", str(stats),
                        *os.environ.get("SHOTDETECTOR_OURS_ARGS", "").split()], capture_output=True, text=True)
    if p.returncode:
        sys.exit(f"{video}: shotdetect {' '.join(args)} failed: {p.stderr.strip()}")
    return hashlib.sha256(csv.read_bytes() + b"\0" + stats.read_bytes()).hexdigest(), p.stderr.strip().splitlines()[-1]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("videos", nargs="+")
    ap.add_argument("--chunks", type=int, help="this many chunks (the CLI's hidden --chunks); default: --parallel's automatic count")
    ap.add_argument("--require-chunks", action="store_true", help="fail when a run fell back to sequential")
    ap.add_argument("--fast-yuv", action="store_true", help="use the ShotDetector.FastYuv path")
    ap.add_argument("--report", help="also write a Markdown table to this file")
    a = ap.parse_args()

    build = ["dotnet", "build", "-c", "Release", "-v", "q", str(ROOT / "src" / "ShotDetector.Cli")]
    exe = ROOT / "src" / "ShotDetector.Cli" / "bin" / "Release" / "net10.0" / EXE_NAME
    if a.fast_yuv:
        exe = ROOT / "artifacts" / "cli-fastyuv" / EXE_NAME  # compare.py's
        build += ["-p:WithFastYuv=true", "-o", str(exe.parent)]
    subprocess.run(build, check=True, stdout=subprocess.DEVNULL)
    parallel = ["--chunks", str(a.chunks)] if a.chunks else ["--parallel"]

    rows, failed = [], False
    with tempfile.TemporaryDirectory() as tmp:
        for video in a.videos:
            for compat in ("0.7.2", "0.7.1"):
                mode = ["--compat", compat]
                ref, _ = run(exe, video, mode + ["--sequential"], Path(tmp) / "seq")
                got, summary = run(exe, video, mode + parallel, Path(tmp) / "par")
                chunks = int(m.group(1)) if (m := re.search(r" in (\d+) chunks$", summary)) else 1
                same = got == ref
                failed |= not same or (a.require_chunks and chunks == 1)
                rows.append((Path(video).name, compat, chunks, same))
                print(f"{'identical' if same else 'DIFFERS'}  {Path(video).name} {compat}: {chunks} chunk(s)", flush=True)

    table = "| Video | Mode | Chunks | Scene list and stats |\n|---|---|---|---|\n" + "".join(
        f"| {v} | {c} | {n if n > 1 else '1 (sequential)'} | {'identical' if s else '**differs**'} |\n" for v, c, n, s in rows)
    if a.report:
        Path(a.report).write_text(table, encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
