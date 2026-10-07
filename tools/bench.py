"""Time scenedetect and shotdetect on the same videos: wall time, CPU time and peak memory of the whole process tree.

shotdetect runs in its default mode (in-process decoding when FFmpeg's libraries are found, e.g. bundled from
native/) and with --decoder process (the ffmpeg executable). CPU time is the user + system time of the finished
child processes (Unix: getrusage(RUSAGE_CHILDREN), which counts ffmpeg too once shotdetect has waited for it);
memory is the peak RSS of the process tree, polled with psutil (`pip install psutil`).

Usage: python tools/bench.py <video>... [--detector adaptive|content|threshold|hist|hash] [--runs 1]
                             [--threads N] [--fast-yuv] [--report bench.md]
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
try:
    import resource
except ImportError:  # Windows: no CPU time
    resource = None


def cpu_children() -> float:
    if resource is None:
        return 0.0
    r = resource.getrusage(resource.RUSAGE_CHILDREN)
    return r.ru_utime + r.ru_stime


def run(cmd: list[str]) -> tuple[float, float, float, str]:
    """Wall seconds, CPU seconds, peak RSS in MB of the process tree (0 without psutil), and stderr."""
    cpu0 = cpu_children()
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
    wall = time.perf_counter() - start
    if proc.returncode != 0:
        sys.exit(f"{cmd[0]} failed:\n{err}")
    return wall, cpu_children() - cpu0, peak, err


def frames(video: str) -> int:
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets",
                          "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", video], capture_output=True, text=True)
    return int(out.stdout.strip() or 0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("videos", nargs="+")
    ap.add_argument("--detector", default="content", choices=["adaptive", "content", "threshold", "hist", "hash"])
    ap.add_argument("--runs", type=int, default=1, help="best of N (by wall time)")
    ap.add_argument("--threads", type=int, help="shotdetect --threads (default: its own)")
    ap.add_argument("--fast-yuv", action="store_true")
    ap.add_argument("--report")
    a = ap.parse_args()
    exe = str(FAST_DIR / EXE_NAME if a.fast_yuv else EXE)
    sd_cmd = {"hist": "detect-hist", "hash": "detect-hash"}.get(a.detector, f"detect-{a.detector}")
    threads = ["--threads", str(a.threads)] if a.threads else []
    tools = (
        ("scenedetect 0.7.1", [sys.executable, "-m", "scenedetect", "-q", "-i", "{v}", sd_cmd, "list-scenes", "-n"]),
        ("shotdetect", [exe, "-i", "{v}", "-d", a.detector, *threads]),
        ("shotdetect --decoder process", [exe, "-i", "{v}", "-d", a.detector, "--decoder", "process", *threads]),
    )
    table = ["| Video | Frames | Tool | Wall | fps | CPU | Peak MB | vs scenedetect |", "|---|---|---|---|---|---|---|---|"]
    for video in a.videos:
        n = frames(video)
        base = None
        for name, cmd in tools:
            results = [run([c.replace("{v}", video) for c in cmd]) for _ in range(a.runs)]
            wall, cpu, _, err = min(results, key=lambda r: r[0])
            peak = max(r[2] for r in results)
            if name == "shotdetect" and "decoded in-process" in err:
                name += " (in-process)"
            base = base or wall
            cpu_s = f"{cpu:.1f} s" if resource else "-"
            table.append(f"| {Path(video).name} | {n} | {name} | {wall:.1f} s | {n / wall:.0f} | {cpu_s} | {peak:.0f} | "
                         f"{'1.00x' if wall == base else f'{wall / base:.2f}x'} |")
            print(table[-1], flush=True)
    print("\n".join(table))
    if a.report:
        Path(a.report).write_text("\n".join(table) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
