"""Pooled candidates for hand labelling, shown blind (no engine names on the sheets).

For each clip window: every cut that any of FFmpeg scene (0.4, 0.2), ShotDetector adaptive (slider 0.4, 0.2) or
ShotDetector content (-t 20) reported, merged when within 3 frames. Each candidate becomes a row of five frames:
n-2, n-1 | n, n+1, n+2 (n = the earliest reported frame), with a red bar before n. Writes sheets/<clip>-NN.png and
candidates.json (id -> frame, time, which engines reported it: kept off the sheets).
"""
import json, subprocess, sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

S = Path(sys.argv[1])
res = json.loads((S / "out" / "results.json").read_text())
CLIPS = {
    "sintel_trailer_1920x1080_h264.mp4": ("samples/realworld/sintel_trailer_1920x1080_h264.mp4", [("action", 0, 1e9)]),
    "tears_of_steel_1280x534_h264.mov": ("samples/realworld/tears_of_steel_1280x534_h264.mov",
                                         [("dialogue", 0, 240), ("action", 420, 660)]),
    "elephants_dream_426x240_h264.mp4": ("samples/realworld/elephants_dream_426x240_h264.mp4", [("animated", 0, 330)]),
    "duck_and_cover_1951.mp4": ("samples/se/duck_and_cover_1951.mp4", [("documentary", 0, 1e9)]),
}
ENGINES = ["ffmpeg@0.4", "ffmpeg@0.2", "shotdetector@0.4:inprocess", "shotdetector@0.2:inprocess", "content@20"]
W = 192
ROWS = 12
CLI = "src/ShotDetector.Cli/bin/Release/net10.0/shotdetect"
import os
LIBS = os.environ["SHOTDETECTOR_FFMPEG_LIBS"]


def fps_of(path):
    r = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate",
                        "-of", "csv=p=0", path], capture_output=True, text=True).stdout.strip()
    a, b = r.split("/")
    return int(a) / int(b)


(S / "sheets").mkdir(exist_ok=True)
allc = {}
for name, (path, windows) in CLIPS.items():
    fps = fps_of(path)
    sc = S / "out" / f"{Path(name).stem}.content20.shotchanges"
    if not sc.exists():
        subprocess.run([CLI, "-i", path, "--ffmpeg-dir", LIBS, "-d", "content", "-t", "20", "--save-shotchanges", str(sc)],
                       check=True, capture_output=True)
    lists = {e: res[name][e]["cuts"] for e in ENGINES if e in res[name]}
    lists["content@20"] = [float(x) for x in sc.read_text().split() if float(x) > 0.2]
    pts = sorted((round(t * fps), e) for e, ts in lists.items() for t in ts)
    clusters = []
    for n, e in pts:
        if clusters and n - clusters[-1]["frames"][-1] <= 3:
            clusters[-1]["frames"].append(n)
            clusters[-1]["engines"].add(e)
        else:
            clusters.append({"frames": [n], "engines": {e}})
    cands = []
    for wname, a, b in windows:
        for c in clusters:
            n = c["frames"][0]
            if a <= n / fps < b:
                cands.append({"clip": name, "window": wname, "frame": n, "time": n / fps,
                              "engines": sorted(c["engines"]), "frames": sorted(set(c["frames"]))})
    need = {n + d for c in cands for n in [c["frame"]] for d in range(-2, 3)}
    probe = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
                            "-of", "csv=p=0", path], capture_output=True, text=True).stdout.strip().split(",")
    h = round(W * int(probe[1]) / int(probe[0]) / 2) * 2
    p = subprocess.Popen(["ffmpeg", "-v", "error", "-i", path, "-vf", f"scale={W}:{h}", "-f", "rawvideo", "-pix_fmt", "rgb24",
                          "-"], stdout=subprocess.PIPE)
    frames, i, size = {}, 0, W * h * 3
    last = max(need) if need else -1
    while i <= last:
        buf = p.stdout.read(size)
        if len(buf) < size:
            break
        if i in need:
            frames[i] = np.frombuffer(buf, np.uint8).reshape(h, W, 3)
        i += 1
    p.kill()
    stem = Path(name).stem
    for k, c in enumerate(cands):
        c["id"] = f"{stem[:4]}-{k + 1:03d}"
    for s in range(0, len(cands), ROWS):
        chunk = cands[s:s + ROWS]
        img = Image.new("RGB", (70 + 5 * (W + 4), len(chunk) * (h + 6)), "white")
        d = ImageDraw.Draw(img)
        for r, c in enumerate(chunk):
            y = r * (h + 6)
            d.text((2, y + h // 2 - 6), c["id"], fill="black")
            for j, dn in enumerate(range(-2, 3)):
                f = frames.get(c["frame"] + dn)
                x = 70 + j * (W + 4) + (6 if dn >= 0 else 0)
                if f is not None:
                    img.paste(Image.fromarray(f), (x, y))
            d.rectangle((70 + 2 * (W + 4), y, 70 + 2 * (W + 4) + 3, y + h), fill="red")
        img.save(S / "sheets" / f"{stem}-{s // ROWS + 1:02d}.png")
    allc[name] = cands
    print(name, len(cands), "candidates", flush=True)
(S / "candidates.json").write_text(json.dumps(allc, indent=1))
