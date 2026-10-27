"""Charts for benchmark.yml's run summary: Mermaid xychart-beta blocks, which GitHub renders in Markdown.

Reads the run files tools/bench_record.py writes (schema: benchmarks/bench-data.md), or tools/bench.py's --json
files, and prints Markdown.

  python tools/bench_chart.py run [--rid R] FILE...    this run, per runner: shotdetect's wall time as a ratio of
                                                       scenedetect's, one bar per film; a run file gives content and
                                                       content --stats, a bench.py file its own variant
  python tools/bench_chart.py trend [--last 12] RUN.json...
                                                       per runner: the median of those ratios per run, oldest first

It never fails the run: a missing file, section or value leaves that chart out (a note on stderr)."""
import argparse
import json
import math
import re
import statistics
import sys
from pathlib import Path

TOOL = "shotdetect"  # the default: in-process, chunked from 4 CPUs on. Only its rows enter a chart (matched exactly):
# the other tools (--parallel, --decoder process, --decoder process --fast-yuv on some films...) never move a ratio
# or a median, so a tool added to the benchmark keeps the trend comparable with earlier runs.
Y = "ratio to scenedetect"
CHARTS = [("content", "content"), ("content-stats", "content --stats")]


def load(paths: list[str]) -> list[dict]:
    runs = []
    for p in paths:
        try:
            r = json.loads(Path(p).read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            print(f"{p}: {e}, left out", file=sys.stderr)
            continue
        if isinstance(r, dict) and isinstance(r.get("rows"), list):
            runs.append(r)
    return runs


def rid(run: dict) -> str:
    return str((run.get("runner") or {}).get("rid") or "unknown")


def quote(s: str) -> str:
    return '"' + str(s).replace('"', "'") + '"'


def number(x: float) -> str:
    return f"{x:.3f}".rstrip("0").rstrip(".")


def film(video: str) -> str:
    """sintel_trailer_1920x1080_h264.mp4 -> sintel trailer."""
    name = re.sub(r"\.[^.]+$", "", video or "?")
    return re.sub(r"_\d+x\d+.*$", "", name).replace("_", " ")


def ratios(run: dict, variant: str | None) -> list[tuple[str, float]]:
    """(film, ratio) of the default tool's rows in a variant (None: every row, a bench.py file)."""
    out = []
    for r in run.get("rows") or []:
        if (variant is None or r.get("variant") == variant) and r.get("tool") == TOOL \
                and isinstance(r.get("ratio"), (int, float)):
            out.append((film(r.get("video")), float(r["ratio"])))
    return out


def chart(title: str, labels: list[str], values: list[float], kind: str, y: str,
          ref: float | None = 1.0) -> str:
    """One xychart-beta block; `ref` draws a constant line (1: as fast as scenedetect) and keeps it on the y axis."""
    top = max(values) * 1.1
    if ref is not None:
        top = max(top, ref * 1.2)
    step = 10 ** math.floor(math.log10(top)) / 10 if top > 0 else 0.1  # two significant digits, rounded up
    top = round(math.ceil(top / step - 1e-9) * step, 10) if top > 0 else 1
    lines = ["```mermaid", "xychart-beta", f"    title {quote(title)}",
             f"    x-axis [{', '.join(quote(l) for l in labels)}]",
             f"    y-axis {quote(y)} 0 --> {number(top)}",
             f"    {kind} [{', '.join(number(v) for v in values)}]"]
    if ref is not None:
        lines.append(f"    line [{', '.join(number(ref) for _ in values)}]")
    return "\n".join(lines + ["```", ""])


def run_charts(runs: list[dict], runner: str | None) -> str:
    out = []
    for run in runs:
        who = runner or rid(run)
        if "variants" in run:  # bench_record.py's file
            charts = CHARTS
        else:  # bench.py's: one variant
            name = f"{run.get('detector') or '?'}{' --stats' if run.get('stats') else ''}"
            charts = [(None, name + (f", {run['cpus']} CPUs" if run.get("cpus") and run["cpus"] < 4 else ""))]
        for variant, name in charts:
            points = ratios(run, variant)
            if not points:
                print(f"{who}: no {TOOL} rows in {name}, no chart", file=sys.stderr)
                continue
            out.append(chart(f"{who}, {name}: {TOOL} / scenedetect wall time", [p[0] for p in points],
                             [p[1] for p in points], "bar", Y))
    if out:
        out.insert(0, f"{TOOL}'s wall time as a ratio of scenedetect's per film (below the line at 1 is faster):\n")
    return "\n".join(out)


def series(points: list[tuple[str, int, float]], last: int) -> tuple[list[str], list[float]]:
    """(date, run id, value) -> the last runs' labels (MM-DD, #2 for a day's second run) and values, oldest first."""
    points = sorted(points, key=lambda p: (p[0], p[1] or 0))[-last:]
    labels = []
    for date, _, _ in points:
        label = date[5:10] or "?"
        n = sum(1 for l in labels if l == label or l.startswith(label + " #"))
        labels.append(label if n == 0 else f"{label} #{n + 1}")
    return labels, [p[2] for p in points]


def trend(title: str, labels: list[str], values: list[float], y: str, ref: float | None) -> str:
    # A line through one point draws nothing: a bar until there are two runs.
    runs = f"last {len(values)} run{'s' if len(values) > 1 else ''}"
    return chart(f"{title}, {runs}", labels, values, "line" if len(values) > 1 else "bar", y, ref)


def wasm_cases(run: dict) -> list[dict]:
    wasm = run.get("wasm")
    cases = wasm.get("cases") if isinstance(wasm, dict) else None
    return [c for c in cases or [] if isinstance(c, dict) and isinstance(c.get("ns_per_pixel"), (int, float))]


def trend_charts(runs: list[dict], last: int) -> str:
    out = []
    for arch in sorted({rid(r) for r in runs}):
        points = []
        for run in runs:
            values = [v for _, v in ratios(run, "content")] if rid(run) == arch else []
            if values:
                points.append((str(run.get("date") or ""), run.get("run_id"), statistics.median(values)))
        if not points:
            print(f"{arch}: no content rows in any run, no trend", file=sys.stderr)
            continue
        labels, values = series(points, last)
        out.append(trend(f"{arch}, content: median {TOOL} / scenedetect", labels, values, Y, 1.0))
    if out:
        out.insert(0, f"Trend: the median over the films of {TOOL}'s wall time as a ratio of scenedetect's, per run "
                      "on main (bench-data), oldest first.\n")
    wasm = []
    for arch in sorted({rid(r) for r in runs}):
        films: dict[str, list[tuple[str, int, float]]] = {}
        for run in runs:
            for c in wasm_cases(run) if rid(run) == arch else []:
                if c.get("detector") == "content" and not c.get("stats"):
                    films.setdefault(film(c.get("film")), []).append(
                        (str(run.get("date") or ""), run.get("run_id"), float(c["ns_per_pixel"])))
        for name, points in sorted(films.items()):
            labels, values = series(points, last)
            wasm.append(trend(f"{arch}, wasm, {name}, content: ns per pixel", labels, values, "ns per pixel", None))
    if wasm:
        out.append("WebAssembly (benchmarks/wasm): Detect's time per pixel on browser-wasm, content detector, per run "
                   "on main, oldest first.\n")
        out += wasm
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["run", "trend"])
    ap.add_argument("files", nargs="*")
    ap.add_argument("--last", type=int, default=12)
    ap.add_argument("--rid", help="the runner, for bench.py's files (they don't name it)")
    a = ap.parse_args()
    try:
        runs = load([f for f in a.files if not f.endswith("index.json")])
        text = run_charts(runs, a.rid) if a.mode == "run" else trend_charts(runs, max(1, a.last))
    except Exception as e:  # a chart is never worth a failed run
        print(f"no charts: {e!r}", file=sys.stderr)
        return
    if text:
        print(text)


if __name__ == "__main__":
    main()
