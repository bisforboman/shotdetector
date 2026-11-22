"""§6: shotdetect adaptive on the prototype's clips, scored against the labels here (missed / false, as score.py).
Each config is "t[:on[:c]]": the threshold, "on" for --ignore-repeated-frames ("-" for off), and --min-content-val.
Reports that fall in no labelled candidate are listed as unlabelled.
Usage: repeats.py <out dir> 3 3:on 1.5 1.5:on 3:-:25 ... (from the repository root)
The option wasn't kept: "on" needs shotdetect built from commit 32c91ae (pull request #780)."""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

OUT = Path(sys.argv[1]); OUT.mkdir(exist_ok=True)
CONFIGS = sys.argv[2:]
CLI = "src/ShotDetector.Cli/bin/Release/net10.0/shotdetect"
LIBS = os.environ["SHOTDETECTOR_FFMPEG_LIBS"]
D = Path("docs/subtitle-edit-prototype")
cands = json.loads((D / "candidates.json").read_text())
labels = {l.split()[0]: l.split()[1] for l in (D / "labels.txt").read_text().splitlines() if l.strip() and not l.startswith("#")}
CLIPS = {
    "sintel_trailer_1920x1080_h264.mp4": ("samples/realworld/sintel_trailer_1920x1080_h264.mp4", 24, [("action", 0, 1e9)]),
    "tears_of_steel_1280x534_h264.mov": ("samples/realworld/tears_of_steel_1280x534_h264.mov", 24, [("dialogue", 0, 240), ("action", 420, 660)]),
    "elephants_dream_426x240_h264.mp4": ("samples/realworld/elephants_dream_426x240_h264.mp4", 24, [("animated", 0, 330)]),
    "duck_and_cover_1951.mp4": ("samples/se/duck_and_cover_1951.mp4", 2997 / 100, [("documentary", 0, 1e9)]),
}
CLEAN = {"sintel_trailer_1920x1080_h264.mp4", "tears_of_steel_1280x534_h264.mov"}


def parse(cfg):
    p = (cfg.split(":") + ["-", "-"])[:3]
    return p[0], p[1] == "on", (None if p[2] == "-" else p[2])


def run(clip, cfg):
    t, on, cv = parse(cfg)
    f = OUT / f"{Path(clip).stem}.{t}.{'on' if on else 'off'}{'.c' + cv if cv else ''}.shotchanges"
    if not f.exists():
        args = [CLI, "-i", CLIPS[clip][0], "--ffmpeg-dir", LIBS, "-q", "-d", "adaptive", "-t", t, "--save-shotchanges", str(f)]
        if cv:
            args += ["-c", cv]
        if on:
            args.append("--ignore-repeated-frames")
        subprocess.run(args, check=True, capture_output=True)
    return [float(x) for x in f.read_text().split() if float(x) > 0.2]  # as SE: nothing at 0.2 s or before


with ThreadPoolExecutor(4) as ex:
    jobs = {(clip, cfg): ex.submit(run, clip, cfg) for clip in CLIPS for cfg in CONFIGS}
    res = {k: v.result() for k, v in jobs.items()}

for cfg in CONFIGS:
    tot = {"clean": [0, 0, 0], "repeat": [0, 0, 0]}  # cuts, missed, false
    unl, per = [], []
    for clip, (_, fps, windows) in CLIPS.items():
        cs = [c for c in cands[clip] if labels[c["id"]] != "X"]
        found = set()
        for s in res[clip, cfg]:
            n = round(s * fps)
            hit = [c for c in cands[clip] if c["frames"][0] - 3 <= n <= c["frames"][-1] + 3]
            if hit:
                found.update(c["id"] for c in hit)
            elif any(a <= n / fps < b for _, a, b in windows):
                unl.append(f"{clip[:4]}@{n}")
        k = "clean" if clip in CLEAN else "repeat"
        n_c = sum(labels[c["id"]] == "C" for c in cs)
        hit_c = sum(labels[c["id"]] == "C" and c["id"] in found for c in cs)
        false = sum(labels[c["id"]] == "N" and c["id"] in found for c in cs)
        tot[k][0] += n_c; tot[k][1] += n_c - hit_c; tot[k][2] += false
        per.append(f"{clip[:8]} {n_c - hit_c}/{false}")
    a = [tot["clean"][i] + tot["repeat"][i] for i in range(3)]
    print(f"{cfg}: all {a[1]}/{a[2]} of {a[0]}; clean {tot['clean'][1]}/{tot['clean'][2]}; repeat {tot['repeat'][1]}/{tot['repeat'][2]}; "
          f"[{'; '.join(per)}]; unlabelled {len(unl)} {unl[:20]}")
