"""Compare ShotDetector with scenedetect on real public videos (Blender open movies, CC-BY): whole films with
hundreds of cuts and fades, and short clips in every common codec. The files are pinned by SHA-256 and cached
in samples/realworld/ (gitignored).

Usage: python tools/realworld.py [name ...] [--detector all] [--fast-yuv] [--report realworld.md]
       python tools/realworld.py --list
       python tools/realworld.py [name ...] --download   (only fetch; for tools/bench.py)
Any other option goes to tools/compare.py."""
import argparse
import hashlib
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / "samples" / "realworld"

# name: (url, sha256). Add a line (download once, `sha256sum` it) and a matrix entry in .github/workflows/realworld.yml.
VIDEOS = {
    # Big Buck Bunny, the full film: 640x360 h264, 24 fps, 10 minutes, animation with hard cuts.
    "bbb_640x360_h264.mp4": ("https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4",
                             "46f62396c755e1ed0ab856a1521378d54196e125ef1a1643a199af087a15046b"),
    # Elephants Dream, the full film: 426x240 low-bitrate h264, 24 fps, 11 minutes, dark scenes and fades.
    "elephants_dream_426x240_h264.mp4": ("https://archive.org/download/ElephantsDream/ed_1024_512kb.mp4",
                                         "fd6dc0e9da3a2f63487957325983f197f23c6ff1cd2b29bb71e64db567e0994c"),
    # Tears of Steel, the full film: 1280x534 h264 in a mov, 24 fps, 12 minutes, live action with fades.
    "tears_of_steel_1280x534_h264.mov": ("https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov",
                                         "efa9062d9cdb7a338e40ad530dfdf234806743f29ae6a1a136b97ece4e588e8f"),
    # The same 10 seconds of Big Buck Bunny at 720p in four codecs (test-videos.co.uk).
    "bbb_10s_h264.mp4": ("https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
                         "18b99ec25f32f6bd2223aa54e4b5632533328bf5cc81c283eba7604c42649f75"),
    "bbb_10s_h265.mp4": ("https://test-videos.co.uk/vids/bigbuckbunny/mp4/h265/720/Big_Buck_Bunny_720_10s_1MB.mp4",
                         "3d07dcc53f7e8a64cdc24d42daad4396d19ae375b3bb014f2e86913a77b1150c"),
    "bbb_10s_vp9.webm": ("https://test-videos.co.uk/vids/bigbuckbunny/webm/vp9/720/Big_Buck_Bunny_720_10s_1MB.webm",
                         "970f0daf21d82382940085e745825cf6ee34d89415492415fc1504fef78685e5"),
    "bbb_10s_av1.mp4": ("https://test-videos.co.uk/vids/bigbuckbunny/mp4/av1/720/Big_Buck_Bunny_720_10s_1MB.mp4",
                        "56ce7a65bc942fefcccbea30844d539e1aec32e8aee73a7345a01b8b9bac072b"),
}


def fetch(name: str) -> Path:
    url, sha = VIDEOS[name]
    path = CACHE / name
    if not path.exists():
        CACHE.mkdir(parents=True, exist_ok=True)
        print(f"downloading {url}", flush=True)
        req = urllib.request.Request(url, headers={"User-Agent": "ShotDetector real-world check"})
        with urllib.request.urlopen(req) as r, open(path, "wb") as f:
            while chunk := r.read(1 << 20):
                f.write(chunk)
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != sha:
        path.unlink()
        sys.exit(f"{name}: SHA-256 {actual}, expected {sha} (deleted; the source changed?)")
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="*", help="videos to run (default: all)")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--download", action="store_true", help="only download and verify; print the paths")
    args, rest = ap.parse_known_args()
    if args.list:
        print("\n".join(VIDEOS))
        return
    unknown = [n for n in args.names if n not in VIDEOS]
    if unknown:
        sys.exit(f"unknown: {', '.join(unknown)}; known: {', '.join(VIDEOS)}")
    paths = [fetch(n) for n in (args.names or VIDEOS)]
    if args.download:
        print("\n".join(map(str, paths)))
        return
    if "--detector" not in rest:
        rest += ["--detector", "all"]
    cmd = [sys.executable, str(ROOT / "tools" / "compare.py"), *map(str, paths), "--stats", *rest]
    sys.exit(subprocess.run(cmd).returncode)


if __name__ == "__main__":
    main()
