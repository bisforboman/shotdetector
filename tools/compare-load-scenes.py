"""Compare load-scenes: scenedetect writes a scene list, then scenedetect and shotdetect both load it back (each start
column, with a time range, --merge-last-scene, --drop-short-scenes) and write list-scenes, save-edl and save-qp,
which must be byte-identical (the EDL's comment line names the tool, so it is skipped).
Needs samples/ (tools/make-samples.ps1) and a Release build of the CLI.  python tools/compare-load-scenes.py"""
import pathlib, subprocess, sys, tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from compare import EXE, ROOT  # noqa: E402

OUT = pathlib.Path(tempfile.mkdtemp(prefix="shotdetector-load-"))
cases = [
    # label, list-scenes options for the input list, scenedetect global, load-scenes column, our extra options
    ("frames", [], [], [], "Start Frame", []),
    ("timecode column", [], [], [], "Start Timecode", []),
    ("seconds column", [], [], [], "Start Time (seconds)", []),
    ("no cut row", ["-s"], [], [], "Start Frame", []),
    ("range", [], [], ["time", "-s", "5s", "-e", "30s"], "Start Frame", ["-s", "5s", "-e", "30s"]),
    ("merge-last", [], ["--merge-last-scene", "-m", "3s"], ["time", "-e", "33s"], "Start Frame", ["--merge-last-scene", "-m", "3s", "-e", "33s"]),
    ("drop-short", [], ["--drop-short-scenes", "-m", "2s"], [], "Start Frame", ["--drop-short-scenes", "-m", "2s"]),
]
bad = 0
for video in ["samples/sintel_trailer-480p.mp4", "samples/sintel_retimed_30000_1001.mp4"]:
    stem = pathlib.Path(video).stem
    for label, list_opts, sd_global, sd_time, column, ours in cases:
        d = OUT / f"{stem}-{label.replace(' ', '_')}"
        d.mkdir(parents=True)
        # The input list, from scenedetect's own detection.
        subprocess.run([sys.executable, "-m", "scenedetect", "-q", "-o", str(d), "-i", video, "detect-content", "list-scenes", "-f", "input.csv", *list_opts],
                       cwd=ROOT, check=True, capture_output=True)
        r = subprocess.run([sys.executable, "-m", "scenedetect", "-q", "-o", str(d), "-i", video, *sd_global, *sd_time,
                            "load-scenes", "-i", str(d / "input.csv"), "-c", column,
                            "list-scenes", "-f", "ref.csv", "save-edl", "-f", "ref.edl", "save-qp", "-f", "ref.qp"], cwd=ROOT, capture_output=True, text=True)
        if r.returncode:
            print(label, "scenedetect failed", r.stderr[-300:]); bad += 1; continue
        r = subprocess.run([str(EXE), "-i", video, "-q", "--load-scenes", str(d / "input.csv"), "--load-scenes-column", column, *ours,
                            "--csv", str(d / "ours.csv"), "--save-edl", str(d / "ours.edl"), "--save-qp", str(d / "ours.qp")],
                           cwd=ROOT, capture_output=True, text=True)
        if r.returncode:
            print(label, "shotdetect failed", r.stderr[-300:]); bad += 1; continue
        res = []
        for ext in ("csv", "edl", "qp"):
            a = (d / f"ref.{ext}").read_bytes().replace(b"\r\n", b"\n")
            b = (d / f"ours.{ext}").read_bytes()
            if ext == "edl":
                a, b = a.split(b"\n", 1)[1], b.split(b"\n", 1)[1]
            res.append(a == b)
            if a != b:
                bad += 1
                al, bl = a.decode().splitlines(), b.decode().splitlines()
                k = next((i for i in range(min(len(al), len(bl))) if al[i] != bl[i]), min(len(al), len(bl)))
                print(f"   {ext} line {k + 1}:\n   ref : {al[k] if k < len(al) else '<end>'}\n   ours: {bl[k] if k < len(bl) else '<end>'}")
        print(f"{stem:28} {label:16} csv/edl/qp: {' '.join('identical' if x else 'DIFFERENT' for x in res)}")
print("differences:", bad)
sys.exit(1 if bad else 0)
