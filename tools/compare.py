"""Run PySceneDetect and ShotDetector on the same video and report cuts that differ.

Usage: python tools/compare.py <video> [--detector adaptive|content] [--tolerance 2] [--ffmpeg-resize]
"""

import argparse
import csv
import subprocess
import sys
import tempfile
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


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("video")
    ap.add_argument("--detector", choices=["adaptive", "content"], default="adaptive")
    ap.add_argument("--tolerance", type=int, default=2)
    ap.add_argument("--ffmpeg-resize", action="store_true", help="pass --ffmpeg-resize to ShotDetector")
    a = ap.parse_args()

    with tempfile.TemporaryDirectory() as tmp:
        ref_csv, our_csv = Path(tmp, "ref.csv"), Path(tmp, "ours.csv")
        subprocess.run(
            [sys.executable, "-m", "scenedetect", "-q", "-i", a.video, "-o", tmp, f"detect-{a.detector}",
             "list-scenes", "-f", ref_csv.name],
            check=True)
        subprocess.run(
            ["dotnet", "run", "-c", "Release", "--project", str(ROOT / "src" / "ShotDetector"), "--",
             "-i", a.video, "-d", a.detector, "--csv", str(our_csv),
             *(["--ffmpeg-resize"] if a.ffmpeg_resize else [])],
            check=True, stdout=subprocess.DEVNULL)
        ref, ours = cuts_from_csv(ref_csv), cuts_from_csv(our_csv)

    pairs, missing, extra = match(ref, ours, a.tolerance)
    offset = [o - r for r, o in pairs if o != r]
    print(f"scenedetect: {len(ref)} cuts, ShotDetector: {len(ours)} cuts, tolerance ±{a.tolerance} frames")
    print(f"  matched: {len(pairs)} ({len(pairs) - len(offset)} exact)")
    for r, o in pairs:
        if o != r:
            print(f"  off by {o - r:+d}: scenedetect {r}, ours {o}")
    for r in missing:
        print(f"  missing (only scenedetect): {r}")
    for o in extra:
        print(f"  extra (only ShotDetector): {o}")
    return 0 if not missing and not extra else 1


if __name__ == "__main__":
    sys.exit(main())
