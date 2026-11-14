# `shotdetect diff`: runs of shots, a cut in one cut only (2026-11-13)

The first slice of `shotdetect diff` ([2026-11-09-shotdetect-diff.md](2026-11-09-shotdetect-diff.md)) matched one new
shot to one old shot. Measuring it on the Sintel trailer with `tools/recut.py`, a 16-frame black interstitial cut in the
old cut was joined to the next shot in the new one (the new grade lifts the black, so the detector no longer cuts
there): the joined shot matched the next old shot, its black frames read as a negative head trim and the black shot as
removed. The backlog row: match a new shot to a run of old ones, and an old shot to a run of new ones.

## Question, choices and answer

**How a row shows a run** (the owner, 2026-11-13; `diff` was not released yet, so nothing to keep compatible).

- **`lastShot`**: `a` and `b` stay one object each, with `lastShot` beside `shot` (equal to it for a single shot), and
  the run's frames and timecodes (`startFrame` the first shot's, `endFrame` the last one's); `headTrim` and `tailTrim`
  are the run's. The table shows `#4-#5`. *Chosen.*
- `a` and `b` as arrays of shot objects.
- Statuses of their own (`joined`, `split`), one row per shot, each pointing to its counterpart.

## How it works

After each new shot is matched to an old one (its anchor) with an offset, as before:

- **A split** (a cut in the new cut only): consecutive new shots matched to the same old shot, each starting in it
  exactly where the one before ends, are one run. Pieces with frames missing between them (the middle of a shot cut
  out) stay two trimmed rows.
- **A join** (a cut in the old cut only): where a run's frames, laid over the old cut from the anchor's offset, reach
  past the anchor's head or tail, it takes in the old shots there, one after another, while no new shot matched them
  and most of 5 of their frames, spread over the part the run covers, are within 12 bits of the new frames over them
  (flat frames match each other: a black lifted by the grade is still black). A shot the run covers only partly gives
  the run's trim.
- **The offset again over the run.** A join's offset is first found against the anchor alone, which can be slow enough
  to fit offsets a few frames apart (on Sintel, 2 frames off); once the run takes in other old shots, the offset is
  found again over all of them, where the frames the anchor doesn't have (the black) place it, and the join is
  checked again from there.
- Moves and removals as before, by run: a run is in order by its first old shot and its head trim, and every old
  shot in a run is matched.

## What it changed, and what is left

On the Sintel trailer, 8 seeds of `tools/recut.py` (its default changes): the 3 seeds where the black was joined to
its old neighbour went from about 92% of the new cut's frames mapped right to all of them, their changes exact; the
others are as before. `recut.py` now scores a run row as one row per old shot, as its truth has them.

Left (backlog):

- **A join across a move**: when the shot after the black moved away, the black is joined in the new cut to a shot
  that wasn't its neighbour, so the run isn't consecutive old shots and isn't taken (seeds 5 and 7: the black reads
  as removed, the joined shot as 16 frames longer).
- **A cut moved, not added or removed** (continuous action the detector chops elsewhere, as Elephants Dream's strobe,
  or a cut found a few frames late in the new cut): the pieces overlap old shots on both sides, which runs of one
  anchor don't cover; they still read as trims.
