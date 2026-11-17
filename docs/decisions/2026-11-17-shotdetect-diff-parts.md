# `shotdetect diff`: rows in parts of new shots (2026-11-17)

Runs of shots ([2026-11-13-shotdetect-diff-runs.md](2026-11-13-shotdetect-diff-runs.md)) left two cases, both backlog
rows (issue #771):

- **A join across a move.** A black interstitial is joined in the new cut to the shot after it, and that shot's old
  neighbour moved away, so the black and the shot weren't neighbours in the old cut. A run takes in only consecutive
  old shots, so the black read as removed and the shot as 16 frames longer (Sintel seeds 5 and 7 of `tools/recut.py`).
- **A cut found elsewhere.** The new cut's detector cuts a few frames off where the old one did (a cut found late,
  continuous action chopped elsewhere): a new shot holds an old shot and the head of the next one. Read as trims though
  every frame is there (Sintel seeds 3 and 4: a moved shot 7 frames longer, the next 7 shorter at its head).

In both, one new shot holds frames of two old shots that no run of consecutive old shots joins, so a row can't be one
or more whole new shots.

## Question, choices and answer

**How a row shows part of a new shot** (put to the owner on the pull request, 2026-11-17; `diff` is not released yet,
so nothing to keep compatible). Open: the pull request is a draft until the owner answers, built on the first choice.

- **A row per old run, its frames in the new cut where they lie**: `b` keeps its shape (`shot`, `lastShot`,
  `startFrame`, `endFrame`, the timecodes), and its frames and timecodes are the row's own, which can start or end
  inside the new shots `shot` and `lastShot` name, so two rows can name the same new shot. A cut found elsewhere then
  changes nothing; the black and the shot each have their row, unchanged or trimmed. *Recommended, built.*
- The same, with a field that says a row is part of a new shot (`partial`, or the new shot's own frames beside the
  row's), so a reader that takes `b` as whole shots sees it without comparing frames.
- `a` as a list of old runs (`[{shot, lastShot, ...}, ...]`), one row per new shot: covers the join across a move, not
  a cut found elsewhere, where the pieces of one new shot have different statuses (a moved shot and the next one in
  place).
- Leave both as they are, the limits in README.

## How it works

After runs of shots are found as before (each new shot matched to an old one, its offset, the consecutive old shots its
frames reach), a run's frames past its old shots' head or tail that aren't their neighbours' are placed:

- **A cut found elsewhere** first: such frames that continue the run next to it in the new cut, inside that run's old
  shot (the frames after its last one, or before its first, most of 5 within 12 bits as for a join), go to that run.
- **A join across a move** then: such frames that are part of an old shot no run has, anywhere in the old cut, are a
  run of their own, at the offset where their pictures correlate best and checked as above; nearest where the run's old
  shots expect it first, the longest overhangs first. They must be at least half that old shot: a few frames past a
  shot's end aren't taken for a whole removed shot (dark frames match a black shot).
- The runs are then in the new cut's order by their first frame, added shots among them by theirs; moves as before.

## What it changed, and what is left

On the Sintel trailer, 8 seeds of `tools/recut.py` (its default changes): seeds 3 and 4 went from exact precision
0.667 and recall 0.8 to 1 and 1, every frame mapped right; seeds 5 and 7 from 0.5 and 0.6 to 0.8 and 0.8, by status 1
and 1. The other four were exact and still are, as are the 8 seeds of the synthetic programme. `recut.py` scores the
new rows as they are: each row's `startFrame` is where its old shots' frames start in the new cut.

Left: in seeds 5 and 7, a moved shot that fades in from black and out to black reads 3 or 4 frames off (trims of −3
and 3 where it has none). Its first and last frames are dark, and a picture flat in one cut but not in the other
correlates 0, which outweighs everything else in the offset's score (backlog).
