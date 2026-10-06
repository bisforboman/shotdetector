"""Run scenedetect's save-edl/save-fcp/save-otio/save-qp and shotdetect's --save-edl/--save-fcp/--save-otio/--save-qp
on the same inputs and compare the files byte for byte (the EDL's first line names the tool, so it is skipped).
Needs samples/ (tools/make-samples.ps1) and a Release build of the CLI.  python tools/compare-timeline.py"""
import pathlib, shutil, subprocess, sys, tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from compare import EXE, ROOT  # noqa: E402

OUT = pathlib.Path(tempfile.mkdtemp(prefix="shotdetector-timeline-"))
spaced = ROOT / "samples" / "my clip.mp4"
if not spaced.exists():
    shutil.copyfile(ROOT / "samples/sintel_trailer-480p.mp4", spaced)

cases = [
    # name, video, scenedetect global+detector args, scenedetect export args, our args
    ("content", "samples/sintel_trailer-480p.mp4", ["detect-content"],
     ["save-edl", "save-fcp", "save-otio", "save-qp"], ["-d", "content"], {}),
    ("fcp7", "samples/sintel_trailer-480p.mp4", ["detect-content"], ["save-fcp", "--format", "fcp7"], ["-d", "content"], {"fcp-format": "fcp7"}),
    ("threshold", "samples/sintel_trailer-480p.mp4", ["detect-threshold"],
     ["save-edl", "save-fcp", "save-otio", "save-qp"], ["-d", "threshold"], {}),
    ("threshold-fcp7", "samples/sintel_trailer-480p.mp4", ["detect-threshold"], ["save-fcp", "--format", "fcp7"], ["-d", "threshold"], {"fcp-format": "fcp7"}),
    ("range", "samples/sintel_trailer-480p.mp4", ["time", "-s", "5s", "-e", "30s", "detect-content"],
     ["save-edl", "-s", "01:00:00:00", "-t", "Take $VIDEO_NAME", "-r", "B01", "save-qp", "save-otio", "--no-audio", "-n", "Cut list"],
     ["-d", "content", "-s", "5s", "-e", "30s"], {"edl-start-timecode": "01:00:00:00", "edl-title": "Take $VIDEO_NAME", "edl-reel": "B01",
                                                  "otio-no-audio": None, "otio-name": "Cut list"}),
    ("range-noshift", "samples/sintel_trailer-480p.mp4", ["time", "-s", "5s", "-e", "30s", "detect-content"], ["save-qp", "-d"],
     ["-d", "content", "-s", "5s", "-e", "30s"], {"qp-disable-shift": None}),
    ("ntsc", "samples/sintel_retimed_30000_1001.mp4", ["detect-adaptive"],
     ["save-edl", "save-fcp", "save-otio", "save-qp"], ["-d", "adaptive"], {}),
    ("ntsc-fcp7", "samples/sintel_retimed_30000_1001.mp4", ["detect-adaptive"], ["save-fcp", "--format", "fcp7"], ["-d", "adaptive"], {"fcp-format": "fcp7"}),
    ("spaced", "samples/my clip.mp4", ["detect-content"], ["save-fcp", "save-otio"], ["-d", "content"], {}),
    ("spaced-fcp7", "samples/my clip.mp4", ["detect-content"], ["save-fcp", "--format", "fcp7"], ["-d", "content"], {"fcp-format": "fcp7"}),
]
EXT = {"save-edl": "edl", "save-fcp": "xml", "save-otio": "otio", "save-qp": "qp"}
bad = 0
for name, video, sd_args, sd_exports, our_args, extra in cases:
    d = OUT / name
    shutil.rmtree(d, ignore_errors=True)
    (d / "ref").mkdir(parents=True); (d / "ours").mkdir()
    r = subprocess.run([sys.executable, "-m", "scenedetect", "-q", "-o", str(d / "ref"), "-i", video, *sd_args, *sd_exports],
                       cwd=ROOT, capture_output=True, text=True)
    if r.returncode:
        print(name, "scenedetect failed:", r.stderr[-400:]); bad += 1; continue
    stem = pathlib.Path(video).stem
    kinds = [e for e in sd_exports if e in EXT]
    args = [str(EXE), "-i", video, "-q", *our_args]
    for k in kinds:
        args += [f"--{k}", str(d / "ours" / f"{stem}.{EXT[k]}")]
    for k, v in extra.items():
        args += [f"--{k}"] + ([] if v is None else [v])
    r = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
    if r.returncode:
        print(name, "shotdetect failed:", r.stderr[-400:]); bad += 1; continue
    for k in kinds:
        ref = (d / "ref" / f"{stem}.{EXT[k]}").read_bytes().replace(b"\r\n", b"\n")
        ours = (d / "ours" / f"{stem}.{EXT[k]}").read_bytes()
        if k == "save-edl":
            ref, ours = ref.split(b"\n", 1)[1], ours.split(b"\n", 1)[1]
        ok = ref == ours
        bad += not ok
        print(f"{name:15} {k:9} {'identical' if ok else 'DIFFERENT'}")
        if not ok:
            for i, (a, b) in enumerate(zip(ref.decode().splitlines(), ours.decode().splitlines())):
                if a != b:
                    print(f"   line {i + 1}:\n   ref : {a}\n   ours: {b}")
                    break
            else:
                print(f"   lengths {len(ref)} vs {len(ours)}")
print("differences:", bad)
sys.exit(1 if bad else 0)
