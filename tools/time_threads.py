"""Throwaway (debug/decoder-threads): sequential shotdetect at the default decoder threads against --threads <CPUs>,
interleaved; min wall and CPU (children's rusage) of N rounds, peak RSS, and whether the outputs match."""
import hashlib, os, resource, subprocess, sys, tempfile, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from compare import EXE  # noqa: E402

films, rounds, summary = sys.argv[1:-2], int(sys.argv[-2]), sys.argv[-1]
n = os.cpu_count()
modes = [["-d", "content"], ["-d", "adaptive"], ["-d", "content", "--stats", "stats.csv"], ["-d", "content", "--decoder", "process"]]


def run(film, mode, threads):
    with tempfile.TemporaryDirectory() as d:
        cmd = [str(EXE), "-i", film, *mode, "--sequential", "-o", d, *threads]
        before = resource.getrusage(resource.RUSAGE_CHILDREN)
        t = time.perf_counter()
        p = subprocess.run(cmd, capture_output=True)
        wall = time.perf_counter() - t
        after = resource.getrusage(resource.RUSAGE_CHILDREN)
        if p.returncode:
            sys.exit(f"{cmd}: {p.stderr.decode()[-2000:]}")
        h = hashlib.sha256(p.stdout)
        for f in sorted(Path(d).rglob("*")):
            if f.is_file():
                h.update(f.read_bytes())
        cpu = after.ru_utime + after.ru_stime - before.ru_utime - before.ru_stime
        return wall, cpu, after.ru_maxrss // 1024, h.hexdigest()[:12]


with open(summary, "a") as s:
    s.write(f"| film | mode | default wall | {n} threads wall | speed-up | default CPU | {n} threads CPU | output |\n|---|---|---|---|---|---|---|---|\n")
    for film in films:
        for mode in modes:
            res = {"new": [], "old": []}
            for _ in range(rounds):
                for k in res:
                    res[k].append(run(film, mode, [] if k == "new" else ["--threads", str(n)]))
            w = {k: min(r[0] for r in v) for k, v in res.items()}
            c = {k: min(r[1] for r in v) for k, v in res.items()}
            hashes = {k: {r[3] for r in v} for k, v in res.items()}
            same = "identical" if len(hashes["new"]) == 1 and hashes["new"] == hashes["old"] else f"DIFFER {hashes}"
            line = f"| {Path(film).name} | {' '.join(mode)} | {w['new']:.2f} s | {w['old']:.2f} s | {w['old'] / w['new']:.2f}x | {c['new']:.1f} s | {c['old']:.1f} s | {same} |"
            print(line, flush=True)
            s.write(line + "\n")
