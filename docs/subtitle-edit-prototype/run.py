"""Runs Subtitle Edit's ffmpeg scene command and shotdetect (adaptive) on each clip; writes cut lists and timings.

SE's engine: the exact default ShowChangesFFmpegArguments with the slider value, every pts_time of showinfo's log,
times of 0.2 s or less dropped (ShotChangesViewModel.OutputHandler). ShotDetector: adaptive, threshold = slider * 7.5,
the same 0.2 s filter applied afterwards (SE would apply it).
"""
import json, re, resource, subprocess, sys, time
from pathlib import Path

OUT = Path(sys.argv[1])
CLIPS = [Path(p) for p in sys.argv[2:]]
CLI = "src/ShotDetector.Cli/bin/Release/net10.0/shotdetect"  # from the repository root
LIBS = __import__("os").environ["SHOTDETECTOR_FFMPEG_LIBS"]
SENS = [0.4, 0.3, 0.2, 0.1]
TIME = re.compile(r"pts_time:\d+[.,]*\d*")


def run(cmd):
    r0 = resource.getrusage(resource.RUSAGE_CHILDREN)
    t0 = time.perf_counter()
    p = subprocess.run(cmd, capture_output=True, text=True)
    wall = time.perf_counter() - t0
    r1 = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu = (r1.ru_utime - r0.ru_utime) + (r1.ru_stime - r0.ru_stime)
    if p.returncode:
        raise SystemExit(f"{cmd}: {p.stderr[-2000:]}")
    return p, wall, cpu, r1.ru_maxrss


results = {}
for clip in CLIPS:
    res = results[clip.name] = {}
    for s in SENS:
        args = f'-i "{clip}" -vf "select=gt(scene\\,{s}),showinfo" -threads 0 -f null -'
        p, wall, cpu, _ = run(["bash", "-c", "ffmpeg -hide_banner " + args])
        cuts = []
        for line in p.stderr.splitlines():
            m = TIME.search(line)
            if m:
                v = float(m.group(0)[9:].replace(",", "."))
                if v > 0.2:
                    cuts.append(v)
        res[f"ffmpeg@{s}"] = {"cuts": cuts, "wall": wall, "cpu": cpu}
        print(clip.name, f"ffmpeg@{s}", len(cuts), f"{wall:.1f}s wall {cpu:.1f}s cpu", flush=True)
    for s in SENS:
        for dec in (["inprocess", "process"] if s == 0.4 else ["inprocess"]):
            f = OUT / f"{clip.stem}.{s}.{dec}.shotchanges"
            _, wall, cpu, _ = run([CLI, "-i", str(clip), "--ffmpeg-dir", LIBS, "--decoder", dec, "-d", "adaptive", "-t", str(round(s * 7.5, 4)),
                                   "--save-shotchanges", str(f)])
            cuts = [float(x) for x in f.read_text().split() if float(x) > 0.2]
            res[f"shotdetector@{s}:{dec}"] = {"cuts": cuts, "wall": wall, "cpu": cpu}
            print(clip.name, f"shotdetector@{s}:{dec}", len(cuts), f"{wall:.1f}s wall {cpu:.1f}s cpu", flush=True)
(OUT / "results.json").write_text(json.dumps(results, indent=1))
