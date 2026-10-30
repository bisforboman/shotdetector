"""Why the trimmer kept an assembly: the shortest chain of dependencies from the app's entry point to it.

Reads the dependency dump of a trimmed publish (`dotnet publish ... -p:_TrimmerDumpDependencies=true` writes
obj/<config>/<tfm>/<rid>/linked/linker-dependencies.xml) and prints, one per line, the nodes from the entry point to the
first node of the assembly. With --cut, the named nodes (substrings) count as gone, as if stubbed, to find the next chain.

    python tools/trim_path.py linker-dependencies.xml FFmpeg.AutoGen
    python tools/trim_path.py linker-dependencies.xml FFmpeg.AutoGen --cut "VideoReader::FramesInProcess"

Exits 1 when a chain is found (the assembly is reached), 0 when none is.
"""

import argparse
import collections
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dump", help="linker-dependencies.xml")
    ap.add_argument("assembly", help="the assembly's name, e.g. FFmpeg.AutoGen")
    ap.add_argument("--cut", action="append", default=[], help="treat nodes containing this text as removed (repeatable)")
    ap.add_argument("--all", action="store_true", help="keep cutting each chain's last ShotDetector/FrameReader node and print every chain")
    args = ap.parse_args()

    edges = collections.defaultdict(list)
    for _, el in ET.iterparse(args.dump):
        if el.tag == "edge":
            edges[el.get("b")].append(el.get("e"))
        el.clear()

    roots = [n for n in edges if "::<Main>$" in n or "::Main(" in n]
    if not roots:
        print("no entry point (Main) in the dump", file=sys.stderr)
        return 2
    target = args.assembly + "."
    cuts = list(args.cut)
    found = 0
    while True:
        path = shortest(edges, roots, target, cuts)
        if path is None:
            break
        found += 1
        print(f"chain {found}:")
        for node in path:
            print("  " + node)
        if not args.all:
            break
        # The chain's last ShotDetector method (else FrameReader's): where it leaves for code only FFmpeg's paths need.
        ours = [n for n in path if n.startswith("Method:") and " ShotDetector." in n and target not in n] \
            or [n for n in path if n.startswith("Method:") and " FrameReader." in n and target not in n]
        if not ours:
            break
        cuts.append(ours[-1])
        print(f"  (cut: {ours[-1]})")
    if found == 0:
        print(f"{args.assembly}: not reached")
    return 1 if found else 0


def shortest(edges, roots, target, cuts):
    prev = {r: None for r in roots}
    queue = collections.deque(roots)
    while queue:
        node = queue.popleft()
        if target in node:
            path = []
            while node is not None:
                path.append(node)
                node = prev[node]
            return path[::-1]
        for nxt in edges.get(node, ()):
            if nxt not in prev and not any(c in nxt for c in cuts):
                prev[nxt] = node
                queue.append(nxt)
    return None


if __name__ == "__main__":
    sys.exit(main())
