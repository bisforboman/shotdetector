"""Git merge driver for docs/backlog.md: merges the tables row by row.

Two branches that each change a different row (one marks its item Done, the other adds a row at the end of the same
section) touch adjacent lines, which git's line merge reports as a conflict. This runs git's merge first and, in each
conflicting hunk made only of table rows, merges the rows by their first cell: a row one side changed takes that
side's version, rows either side added are kept (ours first). A row both sides changed differently, or anything that
isn't a table row, stays a conflict.

Set up once per clone (worktrees share it; .gitattributes names the driver):
    git config merge.backlog.driver "python \"$(git rev-parse --show-toplevel)/tools/backlog-merge.py\" %O %A %B %L"
Check: python tools/backlog-merge.py --self-test
"""
import os
import subprocess
import sys
import tempfile


def key(line):
    """A table row's first cell, or None for anything else (headers' separator lines included)."""
    s = line.strip()
    if not s.startswith(b'|') or set(s) <= set(b'|-: '):
        return None
    return s.split(b'|')[1].strip()


def merge_rows(base, ours, theirs):
    """The merged rows, or None when the hunk can't be merged row by row."""
    sides = []
    for rows in (base, ours, theirs):
        keys = [key(r) for r in rows]
        if None in keys or len(set(keys)) != len(keys):
            return None
        sides.append(dict(zip(keys, rows)))
    b, o, t = sides
    out = []
    for k in list(o) + [k for k in t if k not in o]:
        bv, ov, tv = b.get(k), o.get(k), t.get(k)
        if ov == tv or tv == bv:
            v = ov
        elif ov == bv:
            v = tv
        else:
            return None  # both sides changed (or one deleted) the same row
        if v is not None:
            out.append(v)
    return out


def merge(base, ours, theirs, marker=7):
    """Returns (merged bytes, conflicts left)."""
    files = []
    try:
        for data in (ours, base, theirs):
            f = tempfile.NamedTemporaryFile(delete=False)
            f.write(data)
            f.close()
            files.append(f.name)
        r = subprocess.run(['git', 'merge-file', '-p', '--diff3', f'--marker-size={marker}',
                            '-L', 'ours', '-L', 'base', '-L', 'theirs', *files], capture_output=True)
        if r.returncode < 0 or r.returncode > 127:
            raise SystemExit(r.stderr.decode())
        text = r.stdout
    finally:
        for name in files:
            os.unlink(name)

    lines = text.splitlines(keepends=True)
    out, left, i = [], 0, 0
    start, mid, sep, end = (c * marker for c in (b'<', b'|', b'=', b'>'))
    while i < len(lines):
        if not lines[i].startswith(start + b' '):
            out.append(lines[i])
            i += 1
            continue
        parts, j, cur = {'o': [], 'b': [], 't': []}, i + 1, 'o'
        while not lines[j].startswith(end + b' '):
            if lines[j].startswith(mid + b' '):
                cur = 'b'
            elif lines[j].rstrip(b'\r\n') == sep:
                cur = 't'
            else:
                parts[cur].append(lines[j])
            j += 1
        rows = merge_rows(parts['b'], parts['o'], parts['t'])
        if rows is None:
            # Leave it as git would without --diff3.
            nl = b'\r\n' if lines[i].endswith(b'\r\n') else b'\n'
            out += [start + b' ours' + nl, *parts['o'], sep + nl, *parts['t'], end + b' theirs' + nl]
            left += 1
        else:
            out += rows
        i = j + 1
    return b''.join(out), left


def self_test():
    head = b'| Item | Why | Status |\r\n|---|---|---|\r\n'
    base = head + b'| A | a | Open |\r\n| B | b | Open |\r\n| C | c | Open |\r\n'
    # Adjacent status changes, and an addition next to a change: merged.
    got, left = merge(base, base.replace(b'| A | a | Open', b'| A | a | Done'),
                      base.replace(b'| B | b | Open', b'| B | b | Done'))
    assert left == 0 and got == head + b'| A | a | Done |\r\n| B | b | Done |\r\n| C | c | Open |\r\n', got
    got, left = merge(base, base.replace(b'| C | c | Open', b'| C | c | Done'), base + b'| D | d | Open |\r\n')
    assert left == 0 and got == head + b'| A | a | Open |\r\n| B | b | Open |\r\n| C | c | Done |\r\n| D | d | Open |\r\n', got
    # Both add a row at the same place: both kept, ours first.
    got, left = merge(base, base + b'| D | d | Open |\r\n', base + b'| E | e | Open |\r\n')
    assert left == 0 and got.endswith(b'| D | d | Open |\r\n| E | e | Open |\r\n'), got
    # The same row changed differently: still a conflict.
    got, left = merge(base, base.replace(b'| B | b | Open', b'| B | b | Done'),
                      base.replace(b'| B | b | Open', b'| B | b | Maybe'))
    assert left == 1 and b'<<<<<<< ours' in got, got
    # A row renamed on one side and changed on the other: a conflict, not two rows.
    got, left = merge(base, base.replace(b'| B | b | Open', b'| B2 | b | Open'),
                      base.replace(b'| B | b | Open', b'| B | b | Done'))
    assert left == 1, got
    print('ok')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
        sys.exit(0)
    base_path, ours_path, theirs_path = sys.argv[1:4]
    marker = int(sys.argv[4]) if len(sys.argv) > 4 else 7
    read = lambda p: open(p, 'rb').read()
    merged, left = merge(read(base_path), read(ours_path), read(theirs_path), marker)
    open(ours_path, 'wb').write(merged)
    sys.exit(1 if left else 0)
