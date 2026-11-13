"""Measure `shotdetect diff` (issue #771): make a new cut of a video with known edits, compare the two, and score the
report against the edits made: precision and recall of the changes by shot, exact (status, shots and trims to the
frame) and by status alone; and frame by frame (the new cut's frames mapped to the right old frame, the old cut's
frames left out and the new cut's added), which doesn't depend on the detector cutting the new cut where it was
joined.

The old cut's shots are shotdetect's own. The new cut takes them in another order with seeded random edits: shots
removed, moved, trimmed at the head or the tail, and one added from an ffmpeg test source; then a new grade, a
half-transparent logo, another size and another encoder. Each piece is cut from a lossless split of the old cut made in
one pass (ffv1, every frame a keyframe), so the frames are exact.

Usage: python tools/recut.py [video] [--seed N] [--edits N] [--work DIR] [--shotdetect PATH] [--check]
  video   default: a synthetic programme of ffmpeg test sources (as ShotDiffTests). A sample (tools/make-samples.ps1,
          e.g. samples/sintel_trailer-480p.mp4) or an open film (python tools/realworld.py bbb_640x360_h264.mp4
          --download, then samples/realworld/bbb_640x360_h264.mp4).
  --check exit 1 unless exact precision and recall are both 1.
Not a parity check: shotdetect diff is ShotDetector's own (docs/decisions/2026-11-09-shotdetect-diff.md)."""
import argparse
import json
import random
import shutil
import subprocess
import sys
import tempfile
from fractions import Fraction
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "src" / "ShotDetector.Cli" / "bin" / "Release" / "net10.0" / ("shotdetect.exe" if sys.platform == "win32" else "shotdetect")

# The synthetic programme: sources with structure both ways (bars that vary only across, flat colour and gradients
# hash as noise) and the same frames each run; all of them move (a still shot trimmed at its head reads as trimmed at
# its tail: every offset fits a still picture alike, and ShotDiff keeps the head).
SYNTHETIC = [
    ("testsrc2=S", 60), ("mandelbrot=S", 50), ("smptebars=S,scroll=h=0.004", 70), ("colorspectrum=S,scroll=v=0.005", 55),
    ("testsrc=S", 65), ("sierpinski=S:seed=1", 50), ("smptehdbars=S,scroll=v=0.004", 60), ("colorchart=r=25,scroll=h=0.003", 45),
    ("mandelbrot=S:start_x=-0.75:start_y=0.1", 60), ("testsrc=S,vflip", 50), ("sierpinski=S:seed=2:type=triangle", 55),
    ("testsrc2=S,hflip", 50),
]
GRADE = "lutyuv=y=val*0.9+20:u=(val-128)*1.2+128:v=(val-128)*1.2+128"
LOGO = "drawbox=x=iw*0.86:y=ih*0.04:w=iw*0.11:h=ih*0.07:color=white@0.7:t=fill"


def run(cmd: list, ok: tuple = (0,)) -> subprocess.CompletedProcess:
    result = subprocess.run([str(c) for c in cmd], capture_output=True, text=True)
    if result.returncode not in ok:
        sys.exit(f"{Path(str(cmd[0])).name} failed:\n{result.stderr[-2000:]}")
    return result


def probe(path: Path) -> tuple[int, int, Fraction]:
    out = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate",
               "-of", "json", path]).stdout
    s = json.loads(out)["streams"][0]
    return s["width"], s["height"], Fraction(s["r_frame_rate"])


def synthetic(path: Path) -> None:
    args, parts = ["ffmpeg", "-v", "error", "-y"], []
    for i, (source, frames) in enumerate(SYNTHETIC):
        args += ["-f", "lavfi", "-i", source.replace("=S", "=s=320x240:r=25", 1)]
        parts.append(f"[{i}]trim=end_frame={frames},setpts=PTS-STARTPTS,scale=320:240,setsar=1,format=yuv420p[v{i}]")
    joined = "".join(f"[v{i}]" for i in range(len(SYNTHETIC)))
    run(args + ["-filter_complex", ";".join(parts) + f";{joined}concat=n={len(SYNTHETIC)}:v=1:a=0", "-r", "25",
                "-c:v", "mpeg4", "-q:v", "3", path])


def shots(video: Path, work: Path) -> list[tuple[int, int]]:
    """shotdetect's shots as 0-based [start, end) frames."""
    out = work / (video.stem + ".shots.json")
    run([EXE, "-i", video, "-q", "--json", out])
    return [(s["startFrame"] - 1, s["endFrame"]) for s in json.loads(out.read_text())]


def plan(old: list[tuple[int, int]], rng: random.Random, edits: int):
    """The new cut as pieces ("old", shot, head, tail) or ("new", frames), and the truth: each change as the report gives
    it, (status, old shot, new start frame, head, tail), shot numbers and frames 1-based; unchanged ones too."""
    # Shots long enough to trim and still be a shot (min-scene-len is 15 frames), away from the ends.
    usable = [i for i in range(1, len(old) - 1) if old[i][1] - old[i][0] >= 40]
    rng.shuffle(usable)
    picked = usable[:4 * edits]
    removed, moved = set(picked[0::4]), picked[1::4]
    heads = {i: rng.randint(5, (old[i][1] - old[i][0]) // 3) for i in picked[2::4]}
    tails = {i: rng.randint(5, (old[i][1] - old[i][0]) // 3) for i in picked[3::4]}
    order = [i for i in range(len(old)) if i not in removed and i not in moved]
    for i in moved:
        # At least three places from where it stood.
        was = sum(k < i for k in order)
        order.insert(rng.choice([k for k in range(1, len(order)) if abs(k - was) > 2]), i)
    pieces = [("old", i, heads.get(i, 0), tails.get(i, 0)) for i in order]
    pieces.insert(rng.randint(1, len(pieces) - 1), ("new", 50))
    truth, start = [], 0
    for p in pieces:
        if p[0] == "new":
            truth.append(("added", None, start + 1, None, None))
            start += p[1]
            continue
        _, i, head, tail = p
        status = "moved" if i in moved else "trimmed" if head or tail else "unchanged"
        truth.append((status, i + 1, start + 1, head, tail))
        start += old[i][1] - old[i][0] - head - tail
    truth += [("removed", i + 1, None, None, None) for i in sorted(removed)]
    return pieces, truth


def render(video: Path, old: list[tuple[int, int]], pieces, out: Path, work: Path, changes: list[str]) -> None:
    width, height, rate = probe(video)
    # Every boundary a piece needs, then one lossless split of the old cut at them.
    cuts = sorted({f for p in pieces if p[0] == "old" for f in (old[p[1]][0] + p[2], old[p[1]][1] - p[3])} - {0})
    run(["ffmpeg", "-v", "error", "-y", "-i", video, "-an", "-sn", "-vf", "setsar=1,format=yuv420p", "-c:v", "ffv1",
         "-g", "1", "-f", "segment", "-segment_frames", ",".join(map(str, cuts)), "-reset_timestamps", "1",
         work / "part%05d.mkv"])
    bounds = [0, *cuts]
    files = []
    for p in pieces:
        if p[0] == "new":
            new = work / "added.mkv"
            run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", f"smptebars=s={width}x{height}:r={rate},rotate=0.5*t",
                 "-frames:v", p[1], "-vf", "setsar=1,format=yuv420p", "-c:v", "ffv1", "-g", "1", new])
            files.append(new)
            continue
        first, end = old[p[1]][0] + p[2], old[p[1]][1] - p[3]
        files += [work / f"part{k:05d}.mkv" for k in range(bounds.index(first), bounds.index(end) if end in bounds else len(bounds))]
    listing = work / "pieces.txt"
    listing.write_text("".join(f"file '{f.as_posix()}'\n" for f in files))
    filters = {"grade": GRADE, "logo": LOGO, "scale": "scale=trunc(iw*0.9/2)*2:trunc(ih*0.9/2)*2"}
    run(["ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listing, "-vf",
         ",".join([f"setpts=N/({rate}*TB)", *(filters[c] for c in changes), "setsar=1"]), "-fps_mode", "passthrough",
         "-c:v", "mpeg4", "-q:v", "5", out])


def mapping(old: list[tuple[int, int]], pieces) -> list:
    """The truth frame by frame: the old cut's frame each frame of the new cut shows (None: an added one)."""
    out = []
    for p in pieces:
        out += list(range(old[p[1]][0] + p[2], old[p[1]][1] - p[3])) if p[0] == "old" else [None] * p[1]
    return out


def frames(report: dict, truth: list) -> dict:
    """The report frame by frame, against the truth: the new cut's frames mapped to the right old frame (or rightly to
    none), and precision and recall of the old cut's frames left out and the new cut's frames added. Unlike the shots'
    scores, these don't depend on the detector cutting the new cut where the edits joined it: in continuous action it
    can cut elsewhere (Elephants Dream's strobe is cut every min-scene-len frames, from wherever it starts)."""
    got = [None] * len(truth)
    for r in report["shots"]:
        if not r["b"] or not r["a"]:
            continue
        a0, a_len = r["a"]["startFrame"] - 1, r["a"]["endFrame"] - r["a"]["startFrame"] + 1
        for k in range(r["b"]["startFrame"] - 1, min(r["b"]["endFrame"], len(truth))):
            v = r["headTrim"] + k - (r["b"]["startFrame"] - 1)
            got[k] = a0 + v if 0 <= v < a_len else None

    def pr(reported: set, wanted: set) -> tuple[float, float]:
        hit = len(reported & wanted)
        return (hit / len(reported) if reported else 1.0, hit / len(wanted) if wanted else 1.0)

    every = set(range(report["a"]["frames"]))
    return {
        "mapped": sum(g == t for g, t in zip(got, truth)) / len(truth),
        "left out": pr(every - set(got), every - set(truth)),
        "added": pr({k for k, g in enumerate(got) if g is None}, {k for k, t in enumerate(truth) if t is None}),
    }


def keys(r: dict, old: list[tuple[int, int]]) -> list:
    """A report row as the truth's keys: (status, old shot, new start frame, head, tail). A run of old shots (a cut the
    detector found in the old cut only) is one key per old shot, as the truth has them: the run's head trim on its
    first, its tail trim on its last, each starting in the new cut where its frames do."""
    if not r["a"] or not r["b"]:
        return [(r["status"], r["a"]["shot"] if r["a"] else None, r["b"]["startFrame"] if r["b"] else None,
                 r["headTrim"], r["tailTrim"])]
    first, last = r["a"]["shot"], r["a"].get("lastShot", r["a"]["shot"])
    out = []
    for n in range(first, last + 1):
        head = r["headTrim"] if n == first else 0
        tail = r["tailTrim"] if n == last else 0
        start = r["b"]["startFrame"] + old[n - 1][0] - old[first - 1][0] + head - r["headTrim"]
        status = r["status"] if r["status"] == "moved" else "trimmed" if head or tail else "unchanged"
        out.append((status, n, start, head, tail))
    return out


def score(report: dict, truth: list, truth_frames: list, old: list[tuple[int, int]]) -> dict:
    reported = [k for r in report["shots"] for k in keys(r, old)]
    out = {}
    for name, cut in (("exact", lambda k: k), ("status", lambda k: k[:3])):
        got = {cut(k) for k in reported if k[0] != "unchanged"}
        want = {cut(k) for k in truth if k[0] != "unchanged"}
        hit = len(got & want)
        out[name] = (hit / len(got) if got else 1.0, hit / len(want) if want else 1.0)
    out["frames"] = frames(report, truth_frames)
    out["missed"] = sorted((k for k in truth if k[0] != "unchanged" and k not in reported), key=str)
    out["wrong"] = sorted((k for k in reported if k[0] != "unchanged" and k not in truth), key=str)
    return out


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("video", nargs="?", type=Path)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--edits", type=int, default=2, help="how many of each kind (removed, moved, head and tail trims)")
    p.add_argument("--work", type=Path, help="folder for the cuts and pieces (default: a temporary one, deleted)")
    p.add_argument("--shotdetect", type=Path, help="a built shotdetect (default: build src/ShotDetector.Cli)")
    p.add_argument("--changes", default="grade,logo,scale",
                   help="what else the new cut changes, comma-separated (default all): grade, logo, scale; none: only "
                        "re-encoded")
    p.add_argument("--check", action="store_true")
    a = p.parse_args()
    global EXE
    if a.shotdetect:
        EXE = a.shotdetect
    else:
        subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", str(ROOT / "src" / "ShotDetector.Cli")],
                       check=True, stdout=subprocess.DEVNULL)
    work = a.work or Path(tempfile.mkdtemp(prefix="recut-"))
    work.mkdir(parents=True, exist_ok=True)
    try:
        video = a.video
        if video is None:
            video = work / "synthetic.mp4"
            synthetic(video)
        old = shots(video, work)
        pieces, truth = plan(old, random.Random(a.seed), a.edits)
        new = work / "recut.mp4"
        render(video, old, pieces, new, work, [c for c in a.changes.split(",") if c and c != "none"])
        run([EXE, "diff", video, new, "--json", work / "diff.json"], ok=(0, 3))
        s = score(json.loads((work / "diff.json").read_text()), truth, mapping(old, pieces), old)
        print(f"{video.name}: {len(old)} shots, seed {a.seed}, {sum(k[0] != 'unchanged' for k in truth)} changes")
        for name in ("exact", "status"):
            print(f"  shots, {name:6}      precision {s[name][0]:.3f}  recall {s[name][1]:.3f}")
        f = s["frames"]
        print(f"  frames mapped right {f['mapped']:.3f}")
        for name in ("left out", "added"):
            print(f"  frames {name:12} precision {f[name][0]:.3f}  recall {f[name][1]:.3f}")
        for k in s["missed"]:
            print(f"  missed: {k}")
        for k in s["wrong"]:
            print(f"  wrong:  {k}")
        if a.check and s["exact"] != (1.0, 1.0):
            sys.exit(1)
    finally:
        if a.work is None:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
