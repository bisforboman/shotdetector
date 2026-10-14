"""Fold a product's changelog fragments (changes/<product>/*.md, one per pull request) into its CHANGELOG as a new
version's section, then delete them. Each PR writes its own file, so parallel PRs never edit the same lines
(changes/README.md). Two products, released separately: shotdetector (CHANGELOG.md: ShotDetector, FastYuv, the CLI,
the Native packages) and framereader (src/FrameReader/CHANGELOG.md).

Usage: python tools/changelog.py --package shotdetector|framereader X.Y.Z "One line on what the version is." [--date YYYY-MM-DD]
       python tools/changelog.py --test"""
import argparse
import datetime
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
# The release notes' order (release.yml publishes the section as they are); a heading may go on after the word,
# e.g. "Breaking changes (FrameReader only)".
ORDER = ["Breaking changes", "New", "Faster", "Fixed"]
# Each product's fragments folder and CHANGELOG, from the repository root.
PRODUCTS = {
    "shotdetector": ("changes/shotdetector", "CHANGELOG.md"),
    "framereader": ("changes/framereader", "src/FrameReader/CHANGELOG.md"),
}


def parse(path: Path) -> list[tuple[str, str]]:
    """The fragment's (heading, text) pairs."""
    sections, heading, lines = [], None, []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("### "):
            if heading:
                sections.append((heading, "\n".join(lines).strip("\n")))
            heading, lines = line[4:].strip(), []
        elif heading:
            lines.append(line)
        elif line.strip():
            sys.exit(f"{path}: text before the first '### <section>' heading")
    if heading:
        sections.append((heading, "\n".join(lines).strip("\n")))
    if not sections:
        sys.exit(f"{path}: no '### <section>' heading")
    for h, _ in sections:
        if not any(h == o or h.startswith(o + " ") for o in ORDER):
            sys.exit(f"{path}: section '{h}' isn't one of {', '.join(ORDER)}")
    return sections


def fold(root: Path, product: str, version: str, summary: str, date: str) -> list[Path]:
    folder, changelog = PRODUCTS[product]
    fragments = sorted(p for p in (root / folder).glob("*.md") if p.name != "README.md")
    if not fragments:
        sys.exit(f"no fragments in {folder}/")
    merged: dict[str, list[str]] = {}
    for f in fragments:
        for heading, text in parse(f):
            merged.setdefault(heading, []).append(text)
    rank = lambda h: next(i for i, o in enumerate(ORDER) if h == o or h.startswith(o + " "))
    body = "".join(f"### {h}\n\n" + "\n".join(merged[h]) + "\n\n" for h in sorted(merged, key=rank))
    section = f"## {version} – {date}\n\n{summary}\n\n{body}"

    log = root / changelog
    raw = log.read_bytes().decode("utf-8")
    nl = "\r\n" if "\r\n" in raw else "\n"
    text = raw.replace("\r\n", "\n")
    if f"\n## {version} " in text:
        sys.exit(f"{changelog} already has {version}")
    at = text.find("\n## ") + 1
    if at == 0:
        text, at = text.rstrip("\n") + "\n\n", len(text.rstrip("\n")) + 2
    log.write_bytes((text[:at] + section + text[at:]).replace("\n", nl).encode("utf-8"))
    for f in fragments:
        f.unlink()
    return fragments


def test():
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        (root / "changes" / "shotdetector").mkdir(parents=True)
        (root / "changes" / "framereader").mkdir(parents=True)
        (root / "src" / "FrameReader").mkdir(parents=True)
        (root / "CHANGELOG.md").write_bytes(b"# Changelog\r\n\r\nIntro.\r\n\r\n## 1.0.0 \xe2\x80\x93 2026-01-01\r\n\r\nOld.\r\n")
        (root / "src" / "FrameReader" / "CHANGELOG.md").write_bytes(b"# FrameReader\n\n## 2.0.0 \xe2\x80\x93 2026-01-01\n\nOld.\n")
        (root / "changes" / "README.md").write_text("not a fragment")
        (root / "changes" / "shotdetector" / "a.md").write_text("### Fixed\n\n- A fix.\n\n### New\n\n- A thing,\n  two lines.\n")
        (root / "changes" / "shotdetector" / "b.md").write_text("### Fixed\n\n- B fix.\n\n### Breaking changes (FrameReader only)\n\n- Gone.\n")
        (root / "changes" / "framereader" / "c.md").write_text("### New\n\n- C.\n")
        fold(root, "shotdetector", "1.1.0", "Summary.", "2026-02-02")
        fold(root, "framereader", "2.1.0", "FR.", "2026-02-03")
        got = (root / "CHANGELOG.md").read_bytes().decode()
        want = ("# Changelog\r\n\r\nIntro.\r\n\r\n## 1.1.0 – 2026-02-02\r\n\r\nSummary.\r\n\r\n"
                "### Breaking changes (FrameReader only)\r\n\r\n- Gone.\r\n\r\n### New\r\n\r\n- A thing,\r\n  two lines.\r\n\r\n"
                "### Fixed\r\n\r\n- A fix.\r\n- B fix.\r\n\r\n## 1.0.0 – 2026-01-01\r\n\r\nOld.\r\n")
        assert got == want, got
        fr = (root / "src" / "FrameReader" / "CHANGELOG.md").read_bytes().decode()
        assert fr == "# FrameReader\n\n## 2.1.0 – 2026-02-03\n\nFR.\n\n### New\n\n- C.\n\n## 2.0.0 – 2026-01-01\n\nOld.\n", fr
        assert not any((root / "changes" / "shotdetector").iterdir()) and not any((root / "changes" / "framereader").iterdir())
    print("ok")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--package", choices=sorted(PRODUCTS))
    ap.add_argument("version", nargs="?")
    ap.add_argument("summary", nargs="?")
    ap.add_argument("--date", default=datetime.date.today().isoformat())
    ap.add_argument("--test", action="store_true")
    args = ap.parse_args()
    if args.test:
        return test()
    if not args.package or not args.version or not args.summary:
        ap.error("--package, version and summary are required")
    for f in fold(ROOT, args.package, args.version, args.summary, args.date):
        print(f"folded {f.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
