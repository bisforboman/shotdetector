"""Summarises how tools/arch-outputs.sh's folders from two platforms differ (ci.yml's arch-diff; a = the reference, x64):
per clip, detector and decoder, how many stats rows differ and by how much at most, and the cuts only one side has; the
frame checksums (decoded, single-threaded, b's -idct simple against a's default, bgr24, bgr24 from swscale's C code, bgr24 +bitexact), as frames differing;
and whether b's FastYuv files equal a's default ones. Exits 1 unless they do on every 8-bit 4:2:0 and 4:2:2 clip of even
size (yuv420p, yuvj420p, yuv422p, yuvj422p, turned or not; MPEG-1/2, MJPEG and MPEG-4 Part 2 too, which decode with the
C IDCT or, Xvid's, the Xvid one; odd widths reported for now; on clips whose colour tags change midway, b's FastYuv
through the executable against a's FastYuv, which differs from the default path there by design): the rest is FFmpeg's rounding on ARM, reported
(docs/decisions/2026-10-17-arm-results.md, 2026-10-18-fastyuv-422.md, 2026-10-18-mpeg4-xvid-idct.md).
Usage: python3 tools/arch-summary.py <a-dir> <b-dir>"""
import csv
import sys
from pathlib import Path

# tools/make-samples.ps1's clips whose colour matrix or chroma siting changes midway (samples/informational/).
TAGS_CHANGE = {"sintel_matrix_change.mkv", "sintel_10bit_siting_change.mkv"}


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
    worst, differing = {}, 0
    for x, y in zip(ra[1:], rb[1:]):
        differing += x != y
        for name, u, v in zip(ra[0][2:], x[2:], y[2:]):
            try:
                d = abs(float(u) - float(v))
            except ValueError:
                d = 0.0 if u == v else float("inf")
            worst[name] = max(worst.get(name, 0.0), d)
    peaks = ", ".join(f"{n} {d:.3g}" for n, d in worst.items() if d)
    return f"{differing} of {len(ra) - 1} rows; largest: {peaks}"


def frames(a: Path, b: Path) -> str:
    fa = [l for l in a.read_text().splitlines() if not l.startswith("#")]
    fb = [l for l in b.read_text().splitlines() if not l.startswith("#")]
    if fa == fb:
        return "same"
    if len(fa) != len(fb):
        return f"{len(fa)} vs {len(fb)} frames"
    diff = [i for i, (x, y) in enumerate(zip(fa, fb)) if x != y]
    return f"{len(diff)}/{len(fa)}, from {diff[0]}"


def same(a: Path, b: Path) -> bool:
    return a.exists() and b.exists() and a.read_bytes() == b.read_bytes()


def main() -> int:
    a, b = Path(sys.argv[1]), Path(sys.argv[2])
    names = sorted(p.name for p in a.iterdir())
    outputs = [n for n in names if n.endswith(".csv") and ".fastyuv." not in n]
    print("| file | stats rows differing; largest difference per column | cuts only on one side |\n|---|---|---|")
    for name in outputs:
        pa, pb = a / name, b / name
        if same(pa, pb):
            continue
        if not pb.exists():
            print(f"| {name} | missing on the second side | |")
        elif name.endswith(".stats.csv"):
            print(f"| {name} | {stats_delta(pa, pb)} | |")
        else:
            ca, cb = cuts(pa), cuts(pb)
            only = [f"-{c}" for c in ca if c not in cb] + [f"+{c}" for c in cb if c not in ca]
            print(f"| {name} | | {' '.join(only) or 'cuts same, other cells differ'} |")

    failed, checked = [], 0
    kinds = ["file", "decoded", "decoded-1thread", "decoded-idctsimple", "bgr24", "bgr24-c", "bgr24-bitexact"]
    print("\n| clip | codec | " + " | ".join(kinds) + " | b's FastYuv = a's default |")
    print("|---|---|" + "---|" * len(kinds) + "---|")
    for codec in sorted(n for n in names if n.endswith(".codec.md5")):
        clip = codec[: -len(".codec.md5")]
        cells = [frames(a / f"{clip}.decoded.md5", b / f"{clip}.{k}.md5") if k == "decoded-idctsimple"
                 else frames(a / f"{clip}.{k}.md5", b / f"{clip}.{k}.md5") if k != "file"
                 else ("same" if same(a / f"{clip}.file.md5", b / f"{clip}.file.md5") else "differ") for k in kinds]
        fast = [n for n in outputs if n.startswith(clip + ".")]

        # Colour tags that change midway: in-process, FastYuv converts each frame with its own, as the default path
        # does (docs/decisions/2026-10-24-per-frame-tags-fastyuv.md); through the executable it takes the first frame's
        # (docs/decisions/2026-10-19-per-frame-tags.md), so there b's FastYuv is held to a's.
        own = clip in TAGS_CHANGE

        def fastyuv(n: str) -> str:
            return n.replace(".scenes.csv", ".fastyuv.scenes.csv").replace(".stats.csv", ".fastyuv.stats.csv")

        def matches(n: str) -> bool:
            return same(a / (fastyuv(n) if own and ".process." in n else n), b / fastyuv(n))

        hits = sum(matches(n) for n in fast)
        info = dict(line.split("=", 1) for line in (a / codec).read_text().splitlines() if "=" in line)
        codec_name, pix_fmt = info.get("codec_name", ""), info.get("pix_fmt", "")
        width, height = int(info.get("width") or 0), int(info.get("height") or 0)
        # FastYuv converts what swscale's unscaled converter takes (an even decoded height); odd widths reported only
        # until their first ARM table is read (docs/decisions/2026-10-18-fastyuv-422.md). 10-bit 4:2:0: even widths,
        # any height (swscale's scaled path, docs/fastyuv-10bit-422.md).
        required = (pix_fmt in ("yuv420p", "yuvj420p", "yuv422p", "yuvj422p") and width % 2 == 0 and height % 2 == 0
                    or pix_fmt == "yuv420p10le" and width % 2 == 0 and width >= 8 and height > 8)
        checked += required
        if required and (not fast or hits != len(fast)):
            failed.append(clip)
        split = " ".join(f"{d} {sum(matches(n) for n in fast if f'.{d}.' in n)}/{sum(f'.{d}.' in n for n in fast)}"
                         for d in ("inprocess", "process"))
        mark = (" (required" + (", a's FastYuv through the executable" if own else "") + ")") if required else ""
        print(f"| {clip} | {codec_name},{pix_fmt} | {' | '.join(cells)} | {hits}/{len(fast)} ({split}){mark} |")
    if failed:
        print(f"\nARM with FastYuv differs from x64 on 8-bit 4:2:0 or 4:2:2, or 10-bit 4:2:0: {', '.join(failed)}")
    if not checked:
        print("\nNo 8-bit 4:2:0 or 4:2:2 clip to check")
    return 1 if failed or not checked else 0


if __name__ == "__main__":
    sys.exit(main())
