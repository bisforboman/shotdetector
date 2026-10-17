"""Summarises how tools/arch-outputs.sh's folders from two platforms differ (ci.yml's arch-diff): per clip, detector and
decoder, whether the row counts match, the largest difference in each stats column, and the cuts only one side has; then
which of the frame checksums (decoded yuv420p, bgr24, bgr24 with +bitexact) agree. Exits 1 when anything differs.
Usage: python3 tools/arch-summary.py <a-dir> <b-dir>"""
import csv
import sys
from pathlib import Path


def rows(path: Path) -> list[list[str]]:
    with path.open(newline="") as f:
        return list(csv.reader(f))


def cuts(path: Path) -> list[str]:
    head = rows(path)[0]
    return head[1:] if head and head[0].startswith("Timecode List") else []


def stats_delta(a: Path, b: Path) -> str:
    ra, rb = rows(a), rows(b)
    if len(ra) != len(rb):
        return f"rows {len(ra)} vs {len(rb)}"
    names = ra[0]
    worst = {}
    for x, y in zip(ra[1:], rb[1:]):
        for name, u, v in zip(names[2:], x[2:], y[2:]):
            try:
                d = abs(float(u) - float(v))
            except ValueError:
                d = 0.0 if u == v else float("inf")
            worst[name] = max(worst.get(name, 0.0), d)
    return ", ".join(f"{n} {d:.3g}" for n, d in worst.items() if d) or "same"


def main() -> int:
    a, b = Path(sys.argv[1]), Path(sys.argv[2])
    names = sorted(p.name for p in a.iterdir())
    differ = False
    print("| file | stats: largest difference per column | cuts only on one side |\n|---|---|---|")
    for name in names:
        pa, pb = a / name, b / name
        if not pb.exists():
            print(f"| {name} | missing on the second side | |")
            differ = True
            continue
        if pa.read_bytes() == pb.read_bytes() or name.endswith(".md5"):
            continue
        differ = True
        if name.endswith(".stats.csv"):
            print(f"| {name} | {stats_delta(pa, pb)} | |")
        elif name.endswith(".scenes.csv"):
            ca, cb = cuts(pa), cuts(pb)
            only = [f"-{c}" for c in ca if c not in cb] + [f"+{c}" for c in cb if c not in ca]
            print(f"| {name} | | {' '.join(only) or 'cuts same, other cells differ'} |")
    print("\n| clip | yuv420p | bgr24 | bgr24 +bitexact |\n|---|---|---|---|")
    for md5 in sorted(n for n in names if n.endswith(".yuv420p.md5")):
        clip = md5[: -len(".yuv420p.md5")]
        cells = [("same" if (a / f"{clip}.{k}.md5").read_bytes() == (b / f"{clip}.{k}.md5").read_bytes() else "differ")
                 for k in ("yuv420p", "bgr24", "bgr24-bitexact")]
        print(f"| {clip} | {' | '.join(cells)} |")
    return 1 if differ else 0


if __name__ == "__main__":
    sys.exit(main())
