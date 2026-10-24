"""Run PySceneDetect and ShotDetector on the same video and report cuts that differ.

Usage: python tools/compare.py <video>... [--detector adaptive|content|threshold|both|all] [--tolerance 2]
                               [--ffmpeg-resize] [--fast-yuv] [--report summary.md] [--stats]
                               [--weights "1 1 1 1"] [--kernel-size 5]
                               [--compat 0.7.1 --scenedetect-python path/to/python]
                               [--informational] [--label realworld.py]

Under GitHub Actions it also writes workflow commands (tools/annotate.py): an error per sample that differs (at most
ANNOTATION_CAP, the rest in one more) and a notice with the counts. --informational: only the notice, which names the
samples that differ, so a step whose differences are expected doesn't use up the step's 10 errors.
"""

import argparse
import csv
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from annotate import emit

ROOT = Path(__file__).resolve().parent.parent
LABEL = "compare.py"  # --label: the annotations' titles start with it


def cuts_from_csv(path: Path) -> list[int]:
    """Cut frames (0-based first frame of each shot after the first) from a scene list CSV."""
    return [int(row["Start Frame"]) - 1 for row in csv.DictReader(_scene_rows(path))][1:]


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


EXE_NAME = "shotdetect.exe" if sys.platform == "win32" else "shotdetect"
EXE = ROOT / "src" / "ShotDetector.Cli" / "bin" / "Release" / "net10.0" / EXE_NAME
# A separate build with the LGPL fast path (-p:WithFastYuv=true), for --fast-yuv: FastYuv's converter and, when
# deinterlacing in-process, its row yadif.
FAST_DIR = ROOT / "artifacts" / "cli-fastyuv"
SCENEDETECT_PYTHON = sys.executable


def timed(cmd: list[str]) -> float:
    start = time.perf_counter()
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        fail(f"{Path(cmd[0]).name} failed:\n{result.stderr}", f"{' '.join(cmd)}\n{result.stderr.strip()[-1500:]}")
    return time.perf_counter() - start


def fail(message: str, annotation: str | None = None):
    """Exit with `message`, also as an error annotation (`annotation`: what it says instead, e.g. the command)."""
    emit("error", annotation or message, f"{LABEL}: failed")
    sys.exit(message)


def diff_scene_csv(ref_path: Path, our_path: Path) -> tuple[int, str | None]:
    """Number of cells that differ between two scene list CSVs (every row, including the cut list; as text), and
    the first of them in words."""
    ref, ours = (list(csv.reader(open(p, newline="").read().splitlines())) for p in (ref_path, our_path))
    cells = sum(x != y for a, b in zip(ref, ours) for x, y in zip(a, b))
    cells += sum(len(r) for r in (ref[len(ours):] + ours[len(ref):]))
    return cells, first_csv_difference(ref, ours)


def first_csv_difference(ref: list[list[str]], ours: list[list[str]]) -> str | None:
    """The first differing cell of two scene lists: its line, its column's name (from the header row) and both values."""
    header: list[str] = []
    for line in range(max(len(ref), len(ours))):
        a = ref[line] if line < len(ref) else []
        b = ours[line] if line < len(ours) else []
        if a and a[0] == "Scene Number":
            header = a
        for i in range(max(len(a), len(b))):
            x, y = (a[i] if i < len(a) else None), (b[i] if i < len(b) else None)
            if x != y:
                column = header[i] if i < len(header) else f"column {i + 1}"
                return f"scene list line {line + 1} {column}: scenedetect {x}, ours {y}"
    return None


def _scene_rows(path: Path) -> list[str]:
    with open(path, newline="") as f:
        lines = f.read().splitlines()
    # scenedetect's first row is "Timecode List:,..." (an empty line when there are no cuts).
    return lines[1:] if lines and (lines[0].startswith("Timecode List") or not lines[0].strip()) else lines


def diff_stats(ref_path: Path, our_path: Path) -> tuple[dict[str, int], list[str]]:
    """Mismatching cells per column shared by both stats files (compared as exact strings), and the first three.
    Rows only one side has count as mismatches of every shared column."""
    with open(ref_path, newline="") as f:
        ref = {row["Frame Number"]: row for row in csv.DictReader(f)}
    with open(our_path, newline="") as f:
        ours = {row["Frame Number"]: row for row in csv.DictReader(f)}
    columns = [c for c in next(iter(ref.values()), {}) if c != "Frame Number" and c in next(iter(ours.values()), {})]
    diffs = {c: 0 for c in columns}
    examples = []
    for frame in sorted(ref.keys() | ours.keys(), key=int):
        for c in columns:
            r, o = ref.get(frame, {}).get(c), ours.get(frame, {}).get(c)
            if r != o:
                diffs[c] += 1
                if len(examples) < 3:
                    examples.append(f"frame {frame} {c}: scenedetect {r}, ours {o}")
    return diffs, examples


REF_EXTRA: list[str] = []
REF_SUFFIX = ""  # --scenedetect-on
OUR_EXTRA: list[str] = []


def compare(video: str, detector: str, tol: int, extra_args: list[str], stats: bool, shared: list[str]) -> dict:
    """`shared` options are passed to both tools (same spelling in both CLIs), except to detect-threshold."""
    shared = shared if detector in ("adaptive", "content") else []
    with tempfile.TemporaryDirectory() as tmp:
        ref_csv, our_csv = Path(tmp, "ref.csv"), Path(tmp, "ours.csv")
        ref_stats, our_stats = Path(tmp, "ref_stats.csv"), Path(tmp, "our_stats.csv")
        ref_secs = timed(
            [SCENEDETECT_PYTHON, "-m", "scenedetect", "-q", "-i", video + REF_SUFFIX, "-o", tmp,
             *(["-s", str(ref_stats)] if stats else []), *REF_EXTRA, f"detect-{detector}", *shared, "list-scenes", "-f", ref_csv.name])
        our_secs = timed([str(EXE), "-i", video, "-d", detector, "--csv", str(our_csv), *extra_args, *shared, *OUR_EXTRA,
                          *(["--stats", str(our_stats)] if stats else [])])
        if not ref_csv.exists():
            fail(f"{Path(video).name}: scenedetect wrote no scene list (could its OpenCV decode the video?)")
        ref, ours = cuts_from_csv(ref_csv), cuts_from_csv(our_csv)
        csv_cells, csv_first = diff_scene_csv(ref_csv, our_csv)
        stat_diffs, stat_examples = diff_stats(ref_stats, our_stats) if stats else (None, [])
    for e in stat_examples:
        print("    " + e)

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
    print(f"  scene list CSV cells that differ: {csv_cells}")
    if stat_diffs is not None:
        print("  stats cells that differ: " + ", ".join(f"{c} {n}" for c, n in stat_diffs.items()))
    first = (f"cut {missing[0]} only in scenedetect" if missing else f"cut {extra[0]} only in ours" if extra
             else stat_examples[0] if stat_examples else csv_first if csv_cells else None)
    return dict(video=Path(video).name, detector=detector, ref=len(ref), ours=len(ours), exact=exact,
                off=len(pairs) - exact, missing=missing, extra=extra, ref_secs=ref_secs, our_secs=our_secs,
                csv_cells=csv_cells, stat_diffs=stat_diffs, first=first)


def differs(row: dict) -> bool:
    """A comparison that fails: a cut, a scene list cell or a stats cell differs (cuts off within the tolerance
    count through the scene list)."""
    return bool(row["missing"] or row["extra"] or row["csv_cells"] or any((row["stat_diffs"] or {}).values()))


ANNOTATION_CAP = 9  # errors per run, plus one naming the rest: GitHub keeps 10 per step


def annotations(rows: list[dict], label: str, informational: bool, variant: str) -> list[tuple[str, str, str]]:
    """(kind, title, message) workflow commands for a run: an error per sample that differs, naming its detectors
    and the first difference, then a notice with the counts. Informational: the notice alone, naming the samples.
    `variant`: the run's options that set it apart (the same sample is often compared several ways in one step)."""
    by_video: dict[str, list[dict]] = {}
    for r in rows:
        by_video.setdefault(r["video"], []).append(r)
    bad = {v: [r for r in rs if differs(r)] for v, rs in by_video.items()}
    bad = {v: rs for v, rs in bad.items() if rs}
    suffix = f" ({variant})" if variant else ""
    out = []
    if not informational:
        for video, rs in list(bad.items())[:ANNOTATION_CAP]:
            what = "; ".join(f"{r['detector']}: {summary(r)}" for r in rs)
            out.append(("error", f"{label}: {video} differs",
                        f"{video}{suffix}: {what}. First ({rs[0]['detector']}): {rs[0]['first']}"))
        rest = list(bad)[ANNOTATION_CAP:]
        if rest:
            out.append(("error", f"{label}: {len(rest)} more samples differ", ", ".join(rest) + suffix))
    matched = sum(not differs(r) for r in rows)
    note = f"{matched} of {len(rows)} comparisons matched ({len(by_video)} samples){suffix}"
    if bad:
        note += (". Differ: " + ", ".join(f"{v} ({', '.join(r['detector'] for r in rs)})" for v, rs in bad.items())
                 if informational else f"; {len(bad)} samples differ")
    out.append(("notice", f"{label}{' (informational)' if informational else ''}: {matched} of {len(rows)} matched", note))
    return out


def summary(row: dict) -> str:
    parts = ([f"{len(row['missing'])} cuts only in scenedetect"] if row["missing"] else []) + (
        [f"{len(row['extra'])} cuts only in ours"] if row["extra"] else []) + (
        [f"{row['csv_cells']} scene list cells"] if row["csv_cells"] else []) + (
        [f"stats {', '.join(f'{c} {n}' for c, n in row['stat_diffs'].items() if n)}"]
        if any((row["stat_diffs"] or {}).values()) else [])
    return ", ".join(parts)


def report(rows: list[dict], tol: int) -> str:
    # With stats on, scenedetect also computes edges, so timings would not be comparable.
    stats = rows[0]["stat_diffs"] is not None
    lines = [f"| Video | Detector | scenedetect cuts | ShotDetector cuts | Exact | Off by <={tol} | Missing | Extra "
             "| CSV cells that differ |" + (" Stats cells that differ |" if stats else " scenedetect s | ShotDetector s |"),
             "|---|---|---|---|---|---|---|---|---|" + ("---|" if stats else "---|---|")]
    for r in rows:
        tail = (" " + (", ".join(f"{c}: {n}" for c, n in r["stat_diffs"].items() if n) or "none") + " |"
                if stats else f" {r['ref_secs']:.1f} | {r['our_secs']:.1f} |")
        lines.append(f"| {r['video']} | {r['detector']} | {r['ref']} | {r['ours']} | {r['exact']} | {r['off']} "
                     f"| {', '.join(map(str, r['missing'])) or '–'} | {', '.join(map(str, r['extra'])) or '–'} "
                     f"| {r['csv_cells']} |" + tail)
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("videos", nargs="+")
    ap.add_argument("--detector", choices=["adaptive", "content", "threshold", "hist", "hash", "both", "all"], default="adaptive")
    ap.add_argument("--tolerance", type=int, default=2)
    ap.add_argument("--ffmpeg-resize", action="store_true", help="pass --ffmpeg-resize to ShotDetector")
    ap.add_argument("--fast-yuv", action="store_true", help="use the ShotDetector.FastYuv path (its converter, and its row yadif when deinterlacing in-process)")
    ap.add_argument("--compat", help="PySceneDetect release ShotDetector reproduces (default: the newest, 0.7.2; e.g. 0.7.1)")
    ap.add_argument("--scenedetect-python", default=sys.executable,
                    help="Python interpreter whose scenedetect is the reference (default: this one)")
    ap.add_argument("--report", help="also write a Markdown summary table to this file")
    ap.add_argument("--weights", help='content/adaptive weights for both tools, e.g. "1 1 1 1"')
    ap.add_argument("--kernel-size", help="edge kernel size for both tools")
    ap.add_argument("--scenedetect-args", default="", help='extra scenedetect options/commands, e.g. "-fs 2 -c 0 0 400 300 time -s 10s -e 20s"')
    ap.add_argument("--ours-args", default="",
                    help='extra shotdetect options, e.g. "--frame-skip 2 --crop 0 0 400 300 -s 10s -e 20s"; '
                         'SHOTDETECTOR_OURS_ARGS adds to them (CI: the decoder)')
    ap.add_argument("--scenedetect-on", default="",
                    help='give scenedetect <video><suffix> instead of the video, e.g. ".yadif.mkv" for a deinterlaced copy')
    ap.add_argument("--stats", action="store_true",
                    help="also diff per-frame stats files (slows scenedetect: it computes edges then)")
    ap.add_argument("--informational", action="store_true",
                    help="differences are expected: under GitHub Actions only a notice, no error per sample")
    ap.add_argument("--label", default="compare.py", help="the start of the annotations' titles (default: compare.py)")
    a = ap.parse_args()

    global EXE, SCENEDETECT_PYTHON, REF_EXTRA, OUR_EXTRA, REF_SUFFIX, LABEL
    LABEL = a.label
    SCENEDETECT_PYTHON = a.scenedetect_python
    REF_SUFFIX = a.scenedetect_on
    REF_EXTRA, OUR_EXTRA = a.scenedetect_args.split(), a.ours_args.split() + os.environ.get("SHOTDETECTOR_OURS_ARGS", "").split()
    if a.fast_yuv:
        subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", "-p:WithFastYuv=true", "-o", str(FAST_DIR),
                        str(ROOT / "src" / "ShotDetector.Cli")], check=True, stdout=subprocess.DEVNULL)
        EXE = FAST_DIR / EXE_NAME
    else:
        subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", str(ROOT / "src" / "ShotDetector.Cli")],
                       check=True, stdout=subprocess.DEVNULL)
    # "all" is the detectors expected to match exactly; detect-hash is compared separately (it is
    # numerical noise on flat frames, in scenedetect too).
    detectors = {"both": ["adaptive", "content"], "all": ["adaptive", "content", "threshold", "hist"]}.get(
        a.detector, [a.detector])
    extra_args = ((["--ffmpeg-resize"] if a.ffmpeg_resize else []) + (["--fast-yuv"] if a.fast_yuv else [])
                  + (["--compat", a.compat] if a.compat else [])
                  # The Windows OpenCV wheel converts with BT.601 whatever the colour tags say (docs/decisions/)
                  + (["--always-bt601"] if sys.platform == "win32" else []))
    shared = [*(["-w", *a.weights.split()] if a.weights else []), *(["-k", a.kernel_size] if a.kernel_size else [])]
    rows = [compare(v, d, a.tolerance, extra_args, a.stats, shared) for v in a.videos for d in detectors]

    if len(rows) > 1:
        print()
        print(report(rows, a.tolerance))
    if a.report:
        Path(a.report).write_text(report(rows, a.tolerance), encoding="utf-8")
    variant = " ".join(x for x in [
        f"--compat {a.compat}" if a.compat else "", "--fast-yuv" if a.fast_yuv else "",
        f"scenedetect args: {a.scenedetect_args}" if a.scenedetect_args else "",
        f"ours args: {a.ours_args}" if a.ours_args else "",
        f"scenedetect on {a.scenedetect_on}" if a.scenedetect_on else ""] if x)
    for kind, title, message in annotations(rows, a.label, a.informational, variant):
        emit(kind, message, title)
    return 1 if any(differs(r) for r in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
