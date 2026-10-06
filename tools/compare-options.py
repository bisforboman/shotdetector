"""Compare scenedetect and shotdetect on detection options a single-detector compare can't express: several
detectors in one run, per-detector -t/-m, --drop-short-scenes, --merge-last-scene, detect-content --filter-mode
suppress and -d/--downscale. The scene list CSVs (cut list row included) and stats files must be byte-identical.
Needs samples/ (tools/make-samples.ps1) and a Release build of the CLI.  python tools/compare-options.py"""
import pathlib, subprocess, sys, tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from compare import EXE, ROOT  # noqa: E402

OUT = pathlib.Path(tempfile.mkdtemp(prefix="shotdetector-options-"))
VIDEOS = ["samples/synthetic.mp4", "samples/sintel_trailer-480p.mp4"]
# label, scenedetect global options, scenedetect detect commands, shotdetect options
CASES = [
    ("content+threshold", [], ["detect-content", "detect-threshold"], ["-d", "content", "-d", "threshold"]),
    ("adaptive+hist", [], ["detect-adaptive", "detect-hist"], ["-d", "adaptive", "-d", "hist"]),
    ("content+hash", [], ["detect-content", "detect-hash"], ["-d", "content", "-d", "hash"]),
    ("per-detector -t/-m", [], ["detect-content", "-t", "35", "-m", "10", "detect-threshold", "-t", "8", "-m", "1s"],
     ["-d", "content", "-t", "35", "-m", "10", "-d", "threshold", "-t", "8", "-m", "1s"]),
    ("drop-short-scenes", ["--drop-short-scenes", "-m", "1s"], ["detect-content"], ["--drop-short-scenes", "-m", "1s", "-d", "content"]),
    ("merge-last-scene", ["--merge-last-scene", "-m", "3s"], ["detect-content"], ["--merge-last-scene", "-m", "3s", "-d", "content"]),
    ("drop+merge, 2 detectors", ["--drop-short-scenes", "--merge-last-scene", "-m", "2s"], ["detect-content", "detect-hist"],
     ["--drop-short-scenes", "--merge-last-scene", "-m", "2s", "-d", "content", "-d", "hist"]),
    # A short last shot to merge: the end time cuts the video there (synthetic at 13.2 s, Sintel at 33 s).
    ("merge-last, end 13.2s", ["--merge-last-scene", "-m", "1s"], ["time", "-e", "13.2s", "detect-content"],
     ["--merge-last-scene", "-m", "1s", "-e", "13.2s", "-d", "content"]),
    ("merge-last, end 33s", ["--merge-last-scene", "-m", "1s"], ["time", "-e", "33s", "detect-content"],
     ["--merge-last-scene", "-m", "1s", "-e", "33s", "-d", "content"]),
    ("filter-mode suppress, 2s", [], ["detect-content", "-f", "suppress", "-m", "2s"], ["-d", "content", "--filter-mode", "suppress", "-m", "2s"]),
    ("filter-mode suppress", [], ["detect-content", "-f", "suppress"], ["-d", "content", "--filter-mode", "suppress"]),
    ("downscale 1", ["-d", "1"], ["detect-content"], ["--downscale", "1", "-d", "content"]),
    ("downscale 3", ["-d", "3"], ["detect-adaptive"], ["--downscale", "3", "-d", "adaptive"]),
    ("downscale 5", ["-d", "5"], ["detect-hist"], ["--downscale", "5", "-d", "hist"]),
]
bad = 0
for video in VIDEOS:
    for i, (label, sd_global, sd_detect, ours) in enumerate(CASES):
        d = OUT / f"{pathlib.Path(video).stem}-{i}"
        d.mkdir(parents=True)
        r = subprocess.run([sys.executable, "-m", "scenedetect", "-q", "-i", video, "-o", str(d), "-s", str(d / "ref-stats.csv"),
                            *sd_global, *sd_detect, "list-scenes", "-f", "ref.csv"], cwd=ROOT, capture_output=True, text=True)
        if r.returncode:
            print(f"{video} {label}: scenedetect failed: {r.stderr[-300:]}"); bad += 1; continue
        r = subprocess.run([str(EXE), "-i", video, "-q", *ours, "--csv", str(d / "ours.csv"), "--stats", str(d / "ours-stats.csv")],
                           cwd=ROOT, capture_output=True, text=True)
        if r.returncode:
            print(f"{video} {label}: shotdetect failed: {r.stderr[-300:]}"); bad += 1; continue
        results = []
        for ref, ours_file in (("ref.csv", "ours.csv"), ("ref-stats.csv", "ours-stats.csv")):
            a = (d / ref).read_bytes().replace(b"\r\n", b"\n")
            b = (d / ours_file).read_bytes().replace(b"\r\n", b"\n")
            results.append(a == b)
            if a != b:
                bad += 1
                al, bl = a.decode().splitlines(), b.decode().splitlines()
                first = next((k for k in range(min(len(al), len(bl))) if al[k] != bl[k]), min(len(al), len(bl)))
                print(f"   {ref} differs at line {first + 1}:\n   ref : {al[first] if first < len(al) else '<end>'}\n   ours: {bl[first] if first < len(bl) else '<end>'}")
        print(f"{pathlib.Path(video).name:24} {label:26} csv {'identical' if results[0] else 'DIFFERENT'}, stats {'identical' if results[1] else 'DIFFERENT'}")
print("differences:", bad)
sys.exit(1 if bad else 0)
