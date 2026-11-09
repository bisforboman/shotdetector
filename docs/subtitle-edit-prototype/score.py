"""Scores each engine against the hand labels: C (hard cut) must be found, N (no shot change) must not be, G (gradual)
and X (outside the windows) count neither way. A detection belongs to the candidate cluster it went into."""
import json, sys
from pathlib import Path

S = Path(sys.argv[1])
cands = json.loads((S / "candidates.json").read_text())
labels = {}
for line in (S / "labels.txt").read_text().splitlines():
    if line and not line.startswith("#"):
        k, v = line.split()[:2]
        labels[k] = v
WINDOWS = {"sintel_trailer_1920x1080_h264.mp4": {"action": "Sintel trailer (animated action, 0:52)"},
           "tears_of_steel_1280x534_h264.mov": {"dialogue": "Tears of Steel 0:00-4:00 (live-action dialogue)",
                                                "action": "Tears of Steel 7:00-9:48 (live-action battle)"},
           "elephants_dream_426x240_h264.mp4": {"animated": "Elephants Dream 0:00-5:30 (animated, dark, flicker)"},
           "duck_and_cover_1951.mp4": {"documentary": "Duck and Cover 1951 (documentary, 9:15, telecined film)"}}
ENGINES = [("ffmpeg@0.4", "FFmpeg scene, 0.4 (SE default)"), ("shotdetector@0.4:inprocess", "ShotDetector adaptive, 3 (slider 0.4)"),
           ("ffmpeg@0.2", "FFmpeg scene, 0.2"), ("shotdetector@0.2:inprocess", "ShotDetector adaptive, 1.5 (slider 0.2)"), ("content@20", "ShotDetector content, 20")]
rows, tot = [], {e: [0, 0, 0, 0] for e, _ in ENGINES}
for clip, ws in WINDOWS.items():
    for w, title in ws.items():
        cs = [c for c in cands[clip] if c["window"] == w and labels[c["id"]] != "X"]
        n_c = sum(labels[c["id"]] == "C" for c in cs)
        n_g = sum(labels[c["id"]] == "G" for c in cs)
        row = [title, n_c, n_g]
        for e, _ in ENGINES:
            found = [c for c in cs if e in c["engines"]]
            hit = sum(labels[c["id"]] == "C" for c in found)
            false = sum(labels[c["id"]] == "N" for c in found)
            row += [n_c - hit, false]
            t = tot[e]
            t[0] += n_c; t[1] += hit; t[2] += false
        rows.append(row)
print("| Clip | Cuts | Gradual | " + " | ".join(f"{name}: missed / false" for _, name in ENGINES) + " |")
print("|---|---:|---:|" + "---:|" * len(ENGINES))
for r in rows:
    print(f"| {r[0]} | {r[1]} | {r[2]} | " + " | ".join(f"{r[3 + 2 * i]} / {r[4 + 2 * i]}" for i in range(len(ENGINES))) + " |")
cuts = rows and sum(r[1] for r in rows)
print(f"| **All** | **{cuts}** | **{sum(r[2] for r in rows)}** | " + " | ".join(
    f"**{t[0] - t[1]} / {t[2]}**" for t in tot.values()) + " |")
print()
for e, name in ENGINES:
    t = tot[e]
    print(f"{name}: recall {t[1] / t[0]:.0%}, precision {t[1] / (t[1] + t[2]):.0%} ({t[1]} of {t[0]} cuts, {t[2]} false)")
# The flashes: N candidates whose note says flash
notes = {l.split()[0]: " ".join(l.split()[2:]) for l in (S / "labels.txt").read_text().splitlines() if l and not l.startswith("#")}
