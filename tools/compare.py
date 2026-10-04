"""Run PySceneDetect and ShotDetector on the same video and report cuts that differ.

Usage: python tools/compare.py <video>... [--detector adaptive|content|threshold|both|all] [--tolerance 2]
                               [--ffmpeg-resize] [--report summary.md]
"""

import argparse
import csv
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def cuts_from_csv(path: Path) -> list[int]:
    """Cut frames (0-based first frame of each shot after the first) from a scene list CSV."""
    with open(path, newline="") as f:
        lines = f.read().splitlines()
    if lines and lines[0].startswith("Timecode List"):  # PySceneDetect's extra first row
        lines = lines[1:]
    return [int(row["Start Frame"]) - 1 for row in csv.DictReader(lines)][1:]


def match(ref: list[int], ours: list[int], tol: int):
    """Greedily pair each reference cut with the nearest unused cut of ours within `tol` frames."""
    unused = list(ours)
    pairs, missing = [], []
    for r in ref:
        best = min(unused, key=lambda o: abs(o - r), default=None)
        if best is not None and abs(best - r) <= tol:
            unused.remove(best)
            pairs.append((r, best))
        else:
            missing.append(r)
    return pairs, missing, unused


EXE = ROOT / "src" / "ShotDetector" / "bin" / "Release" / "net10.0" / (
    "ShotDetector.exe" if sys.platform == "win32" else "ShotDetector")


def timed(cmd: list[str]) -> float:
    start = time.perf_counter()
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{cmd[0]} failed:\n{result.stderr}")
    return time.perf_counter() - start


def compare(video: str, detector: str, tol: int, extra_args: list[str]) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        ref_csv, our_csv = Path(tmp, "ref.csv"), Path(tmp, "ours.csv")
        ref_secs = timed(
            [sys.executable, "-m", "scenedetect", "-q", "-i", video, "-o", tmp, f"detect-{detector}",
             "list-scenes", "-f", ref_csv.name])
        our_secs = timed([str(EXE), "-i", video, "-d", detector, "--csv", str(our_csv), *extra_args])
        ref, ours = cuts_from_csv(ref_csv), cuts_from_csv(our_csv)

    pairs, missing, extra = match(ref, ours, tol)
    print(f"{Path(video).name} [{detector}] scenedetect: {len(ref)} cuts ({ref_secs:.1f}s), "
          f"ShotDetector: {len(ours)} cuts ({our_secs:.1f}s), tolerance ±{tol} frames")
    exact = sum(1 for r, o in pairs if o == r)
    print(f"  matched: {len(pairs)} ({exact} exact)")
    for r, o in pairs:
        if o != r:
            print(f"  off by {o - r:+d}: scenedetect {r}, ours {o}")
    for r in missing:
        print(f"  missing (only scenedetect): {r}")
    for o in extra:
        print(f"  extra (only ShotDetector): {o}")
    return dict(video=Path(video).name, detector=detector, ref=len(ref), ours=len(ours), exact=exact,
                off=len(pairs) - exact, missing=missing, extra=extra, ref_secs=ref_secs, our_secs=our_secs)


def report(rows: list[dict], tol: int) -> str:
    lines = [f"| Video | Detector | scenedetect cuts | ShotDetector cuts | Exact | Off by ≤{tol} | Missing | Extra "
             "| scenedetect s | ShotDetector s |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for r in rows:
        lines.append(f"| {r['video']} | {r['detector']} | {r['ref']} | {r['ours']} | {r['exact']} | {r['off']} "
                     f"| {', '.join(map(str, r['missing'])) or '–'} | {', '.join(map(str, r['extra'])) or '–'} "
                     f"| {r['ref_secs']:.1f} | {r['our_secs']:.1f} |")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("videos", nargs="+")
    ap.add_argument("--detector", choices=["adaptive", "content", "threshold", "both", "all"], default="adaptive")
    ap.add_argument("--tolerance", type=int, default=2)
    ap.add_argument("--ffmpeg-resize", action="store_true", help="pass --ffmpeg-resize to ShotDetector")
    ap.add_argument("--report", help="also write a Markdown summary table to this file")
    a = ap.parse_args()

    subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", str(ROOT / "src" / "ShotDetector")],
                   check=True, stdout=subprocess.DEVNULL)
    detectors = {"both": ["adaptive", "content"], "all": ["adaptive", "content", "threshold"]}.get(
        a.detector, [a.detector])
    extra_args = ["--ffmpeg-resize"] if a.ffmpeg_resize else []
    rows = [compare(v, d, a.tolerance, extra_args) for v in a.videos for d in detectors]

    if len(rows) > 1:
        print()
        print(report(rows, a.tolerance))
    if a.report:
        Path(a.report).write_text(report(rows, a.tolerance), encoding="utf-8")
    return 0 if all(not r["missing"] and not r["extra"] for r in rows) else 1


if __name__ == "__main__":
    sys.exit(main())
