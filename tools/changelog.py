"""Fold the changelog fragments (changes/*.md, one per pull request) into CHANGELOG.md as a new version's section,
then delete them. Each PR writes its own file, so parallel PRs never edit the same lines (changes/README.md).

Usage: python tools/changelog.py X.Y.Z "One line on what the version is." [--date YYYY-MM-DD]
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


def fold(root: Path, version: str, summary: str, date: str) -> list[Path]:
    fragments = sorted(p for p in (root / "changes").glob("*.md") if p.name != "README.md")
    if not fragments:
        sys.exit("no fragments in changes/")
    merged: dict[str, list[str]] = {}
    for f in fragments:
        for heading, text in parse(f):
            merged.setdefault(heading, []).append(text)
    rank = lambda h: next(i for i, o in enumerate(ORDER) if h == o or h.startswith(o + " "))
    body = "".join(f"### {h}\n\n" + "\n".join(merged[h]) + "\n\n" for h in sorted(merged, key=rank))
    section = f"## {version} – {date}\n\n{summary}\n\n{body}"

    log = root / "CHANGELOG.md"
    raw = log.read_bytes().decode("utf-8")
    nl = "\r\n" if "\r\n" in raw else "\n"
    text = raw.replace("\r\n", "\n")
    if f"\n## {version} " in text:
        sys.exit(f"CHANGELOG.md already has {version}")
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
        (root / "changes").mkdir()
        (root / "CHANGELOG.md").write_bytes(b"# Changelog\r\n\r\nIntro.\r\n\r\n## 1.0.0 \xe2\x80\x93 2026-01-01\r\n\r\nOld.\r\n")
        (root / "changes" / "README.md").write_text("not a fragment")
        (root / "changes" / "a.md").write_text("### Fixed\n\n- A fix.\n\n### New\n\n- A thing,\n  two lines.\n")
        (root / "changes" / "b.md").write_text("### Fixed\n\n- B fix.\n\n### Breaking changes (FrameReader only)\n\n- Gone.\n")
        fold(root, "1.1.0", "Summary.", "2026-02-02")
        got = (root / "CHANGELOG.md").read_bytes().decode()
        want = ("# Changelog\r\n\r\nIntro.\r\n\r\n## 1.1.0 – 2026-02-02\r\n\r\nSummary.\r\n\r\n"
                "### Breaking changes (FrameReader only)\r\n\r\n- Gone.\r\n\r\n### New\r\n\r\n- A thing,\r\n  two lines.\r\n\r\n"
                "### Fixed\r\n\r\n- A fix.\r\n- B fix.\r\n\r\n## 1.0.0 – 2026-01-01\r\n\r\nOld.\r\n")
        assert got == want, got
        assert [p.name for p in (root / "changes").iterdir()] == ["README.md"]
    print("ok")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", nargs="?")
    ap.add_argument("summary", nargs="?")
    ap.add_argument("--date", default=datetime.date.today().isoformat())
    ap.add_argument("--test", action="store_true")
    args = ap.parse_args()
    if args.test:
        return test()
    if not args.version or not args.summary:
        ap.error("version and summary are required")
    for f in fold(ROOT, args.version, args.summary, args.date):
        print(f"folded {f.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
