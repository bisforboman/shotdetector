"""Time scenedetect and shotdetect on the same videos: wall time, CPU time and peak memory of the whole process tree.

shotdetect runs in its default mode (in-process decoding when FFmpeg's libraries are found, e.g. bundled from
native/, and from 4 CPUs on a long file in chunks on several cores, docs/parallel-detection.md), with --parallel (also
above 4K), with --sequential (one pass) and with --decoder process (the ffmpeg executable); with fewer than 4
CPUs available (taskset) the --parallel and --sequential rows are left out, as nothing chunks there. CPU time is the
user + system time of the finished child processes (Unix: getrusage(RUSAGE_CHILDREN), which counts ffmpeg too once
shotdetect has waited for it); memory is the peak RSS of the process tree, polled with psutil (`pip install psutil`). With --stats both tools also
write per-frame metrics (scenedetect -s, shotdetect --stats; content computes edges for every frame then), and
--decoder process doesn't run. --process-fast-yuv FILE (repeatable, a file name) adds a row on that video:
--decoder process with FastYuv's converter (`--fast-yuv` of the LGPL build, compare.py's -p:WithFastYuv=true), which
only changes something where shotdetect resizes (not at 426x240 and below).

Usage: python tools/bench.py <video>... [--detector adaptive|content|threshold|hist|hash] [--runs 1]
                             [--threads N] [--fast-yuv] [--process-fast-yuv FILE]... [--stats] [--report bench.md]
                             [--json bench.json]
--json writes the table's numbers as JSON (benchmark.yml keeps them on the bench-data branch).
Builds first: dotnet build src/ShotDetector.Cli -c Release (and compare.py's --fast-yuv build for --fast-yuv and
--process-fast-yuv: dotnet build src/ShotDetector.Cli -c Release -p:WithFastYuv=true -o artifacts/cli-fastyuv)."""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
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
    ap.add_argument("--fast-yuv", action="store_true", help="every shotdetect row with FastYuv's converter (its build)")
    ap.add_argument("--process-fast-yuv", action="append", default=[], metavar="FILE",
                    help="on this video (file name) also a --decoder process row with FastYuv's converter")
    ap.add_argument("--stats", action="store_true", help="both tools write a stats CSV")
    ap.add_argument("--report")
    ap.add_argument("--json", help="also write the table's numbers to this file as JSON")
    a = ap.parse_args()
    exe = str(FAST_DIR / EXE_NAME if a.fast_yuv else EXE)
    # The FastYuv build uses its converter only when asked (`--fast-yuv`); without the option it runs the plain path.
    fast = ["--fast-yuv"] if a.fast_yuv else []
    sd_cmd = {"hist": "detect-hist", "hash": "detect-hash"}.get(a.detector, f"detect-{a.detector}")
    threads = ["--threads", str(a.threads)] if a.threads else []
    stats = Path(tempfile.mkdtemp()) / "stats.csv"
    sd_stats, sh_stats = (["-s", str(stats)], ["--stats", str(stats)]) if a.stats else ([], [])
    suffix = " --stats" if a.stats else ""
    tools = [
        (f"scenedetect 0.7.2{suffix}",
         [sys.executable, "-m", "scenedetect", "-q", "-i", "{v}", *sd_stats, sd_cmd, "list-scenes", "-n"]),
        (f"shotdetect{suffix}", [exe, "-i", "{v}", "-d", a.detector, *sh_stats, *threads, *fast]),
        (f"shotdetect --parallel{suffix}", [exe, "-i", "{v}", "-d", a.detector, "--parallel", *sh_stats, *threads, *fast]),
        (f"shotdetect --sequential{suffix}", [exe, "-i", "{v}", "-d", a.detector, "--sequential", *sh_stats, *threads, *fast]),
    ]
    # The CPUs this process may use (taskset's mask, as .NET's ProcessorCount): below 4 nothing chunks.
    cpus = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else os.cpu_count() or 1
    if cpus < 4:
        tools = [t for t in tools if not t[0].startswith(("shotdetect --parallel", "shotdetect --sequential"))]
        print(f"{cpus} CPUs: no --parallel or --sequential rows (chunks need 4 CPUs)", flush=True)
    if not a.stats:
        tools.append(("shotdetect --decoder process", [exe, "-i", "{v}", "-d", a.detector, "--decoder", "process", *threads, *fast]))
    # Its own tool name, so it never counts as one of the rows above (bench_chart.py charts "shotdetect" alone).
    process_fast = ("shotdetect --decoder process --fast-yuv",
                    [str(FAST_DIR / EXE_NAME), "-i", "{v}", "-d", a.detector, "--decoder", "process", *threads, "--fast-yuv"])
    if a.process_fast_yuv and a.stats:
        sys.exit("--process-fast-yuv: --decoder process doesn't run with --stats")
    if a.process_fast_yuv and a.fast_yuv:
        sys.exit("--process-fast-yuv: the --decoder process row already uses FastYuv with --fast-yuv")
    unknown = set(a.process_fast_yuv) - {Path(v).name for v in a.videos}
    if unknown:
        sys.exit(f"--process-fast-yuv: not among the videos: {', '.join(sorted(unknown))}")
    table = ["| Video | Frames | Tool | Wall | fps | CPU | Peak MB | vs scenedetect |", "|---|---|---|---|---|---|---|---|"]
    rows = []
    for video in a.videos:
        n = frames(video)
        base = None
        for tool, cmd in tools + ([process_fast] if Path(video).name in a.process_fast_yuv else []):
            name = tool
            results = []
            for _ in range(a.runs):
                stats.unlink(missing_ok=True)  # a fresh file each run, written in full
                results.append(run([c.replace("{v}", video) for c in cmd]))
            wall, cpu, _, err = min(results, key=lambda r: r[0])
            peak = max(r[2] for r in results)
            if name.startswith("shotdetect") and "decoded in-process" in err:
                name += " (in-process)"
            if m := re.search(r" in (\d+) chunks", err):
                name += f", {m.group(1)} chunks"
            base = base or wall
            cpu_s = f"{cpu:.1f} s" if resource else "-"
            table.append(f"| {Path(video).name} | {n} | {name} | {wall:.1f} s | {n / wall:.0f} | {cpu_s} | {peak:.0f} | "
                         f"{'1.00x' if wall == base else f'{wall / base:.2f}x'} |")
            print(table[-1], flush=True)
            rows.append({"video": Path(video).name, "frames": n, "tool": tool.removesuffix(suffix),
                         "in_process": "(in-process)" in name, "chunks": int(m.group(1)) if m else None,
                         "wall_s": round(wall, 3), "fps": round(n / wall, 1), "cpu_s": round(cpu, 3) if resource else None,
                         "peak_mb": round(peak, 1), "scenedetect_wall_s": round(base, 3), "ratio": round(wall / base, 4)})
    print("\n".join(table))
    if a.report:
        Path(a.report).write_text("\n".join(table) + "\n", encoding="utf-8")
    if a.json:
        try:
            from importlib.metadata import version
            sd_version = version("scenedetect")
        except Exception:
            sd_version = None
        Path(a.json).write_text(json.dumps({
            "detector": a.detector, "stats": a.stats, "runs": a.runs, "threads": a.threads, "fast_yuv": a.fast_yuv,
            "process_fast_yuv": a.process_fast_yuv,
            "cpus": cpus, "scenedetect": sd_version, "rows": rows}, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
