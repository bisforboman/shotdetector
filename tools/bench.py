"""Time scenedetect and shotdetect on the same videos and report wall time, frames per second and peak memory
(resident set, polled with psutil: `pip install psutil`).

Usage: python tools/bench.py <video>... [--detector adaptive|content|threshold|hist|hash] [--runs 1]
                             [--threads 4] [--fast-yuv] [--report bench.md]
Builds first: dotnet build src/ShotDetector.Cli -c Release (and compare.py's --fast-yuv build for --fast-yuv)."""
import argparse
import subprocess
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare import EXE, FAST_DIR, EXE_NAME  # noqa: E402

try:
    import psutil
except ImportError:
    psutil = None


def run(cmd: list[str]) -> tuple[float, float]:
    """Wall seconds and peak RSS in MB of the process tree (0 without psutil)."""
    start = time.perf_counter()
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    peak = 0.0
    if psutil:
        p = psutil.Process(proc.pid)

        def poll():
            nonlocal peak
            while proc.poll() is None:
                try:
                    rss = sum(c.memory_info().rss for c in [p, *p.children(recursive=True)])
                    peak = max(peak, rss / 2**20)
                except psutil.Error:
                    pass
                time.sleep(0.05)

        t = threading.Thread(target=poll, daemon=True)
        t.start()
    _, err = proc.communicate()
    if proc.returncode != 0:
        sys.exit(f"{cmd[0]} failed:\n{err}")
    return time.perf_counter() - start, peak


def frames(video: str) -> int:
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets",
                          "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", video], capture_output=True, text=True)
    return int(out.stdout.strip() or 0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("videos", nargs="+")
    ap.add_argument("--detector", default="content", choices=["adaptive", "content", "threshold", "hist", "hash"])
    ap.add_argument("--runs", type=int, default=1, help="best of N")
    ap.add_argument("--threads", type=int, default=4, help="shotdetect --threads")
    ap.add_argument("--fast-yuv", action="store_true")
    ap.add_argument("--report")
    a = ap.parse_args()
    exe = str(FAST_DIR / EXE_NAME if a.fast_yuv else EXE)
    sd_cmd = {"hist": "detect-hist", "hash": "detect-hash"}.get(a.detector, f"detect-{a.detector}")
    rows = []
    for video in a.videos:
        n = frames(video)
        best = {}
        for name, cmd in (
            ("scenedetect", [sys.executable, "-m", "scenedetect", "-q", "-i", video, sd_cmd, "list-scenes", "-n"]),
            ("shotdetect", [exe, "-i", video, "-d", a.detector, "--threads", str(a.threads)]),
        ):
            results = [run(cmd) for _ in range(a.runs)]
            best[name] = (min(r[0] for r in results), max(r[1] for r in results))
        (sd_t, sd_m), (our_t, our_m) = best["scenedetect"], best["shotdetect"]
        rows.append((Path(video).name, n, sd_t, n / sd_t, sd_m, our_t, n / our_t, our_m, sd_t / our_t))
        print(f"{Path(video).name}: scenedetect {sd_t:.1f}s ({n / sd_t:.0f} fps, {sd_m:.0f} MB), "
              f"shotdetect {our_t:.1f}s ({n / our_t:.0f} fps, {our_m:.0f} MB), x{sd_t / our_t:.2f}", flush=True)
    table = ["| Video | Frames | scenedetect | fps | MB | shotdetect | fps | MB | Speed-up |", "|---|---|---|---|---|---|---|---|---|"]
    table += [f"| {v} | {n} | {st:.1f} s | {sf:.0f} | {sm:.0f} | {ot:.1f} s | {of:.0f} | {om:.0f} | x{x:.2f} |" for v, n, st, sf, sm, ot, of, om, x in rows]
    print("\n".join(table))
    if a.report:
        Path(a.report).write_text("\n".join(table) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
