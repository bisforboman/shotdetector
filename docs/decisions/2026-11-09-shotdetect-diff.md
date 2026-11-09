# `shotdetect diff`: what changed between two cuts (2026-11-09)

Issue #771: given two exports of the same programme, an old cut and a new one, report which shots are unchanged,
moved, trimmed, added or removed. The issue scoped a first slice (a report only, `shotdetect diff a.mp4 b.mp4
--json`, measured on scripted re-cuts) and left questions for the owner. EDL and subtitle outputs stay open on the
issue.

## Questions, choices and answers

1. **Where it lives** (the issue's first question; the owner, 2026-11-09).
   - **The first slice in this repository**, labelled as ShotDetector's own feature: scenedetect has nothing like it,
     so the exactness promise doesn't cover it, and README says so as it does for the YouTube chapters. *Chosen.*
   - A repository of its own, as the video RAG sample and the Jellyfin plugin are.
   - Research first (the subtitle tools, Noga's wish for a check against a reference file).
   - Skip it.
2. **The JSON's shape** (the owner, 2026-11-09).
   - **One list in the new cut's order**: each cut (path, frame rate, frames, shots), the changes counted, then a row
     per shot with removed shots where they stood; each row its status, the shot in the old cut `a` and the new `b`
     (null where it isn't), `headTrim` and `tailTrim` (null for added and removed shots). Reads like the new cut, top
     to bottom. *Chosen.*
   - Two lists, one per cut, each shot with its counterpart.
   - The edit operations (remove, insert, move, trim) that turn the old cut into the new, closest to a change EDL.
3. **Library API** (the owner, 2026-11-09).
   - **CLI only for now**: the fingerprint and alignment are internal (`ShotDiff`, tested through InternalsVisibleTo);
     a public API comes with the EDL and subtitle slices, once its shape has settled. *Chosen.*
   - A public `ShotDiff.Compare` and its result types now, held by the API baseline from the next release.
4. **Frame numbering** (the owner, 2026-11-09).
   - **As the shot list's JSON**: `startFrame` 1-based, `endFrame` the last frame, with start and end timecodes, so
     every CLI output numbers frames alike. *Chosen.*
   - 0-based frame indexes, end exclusive.

Not asked, decided in the slice: exit code 3 when anything changed, 0 when nothing did (1 an error, 2 usage), as `qc`
and `--loudness` do, so a pipeline can stop on a changed master.

## How it works

- **One pass per cut.** The shots are the chosen detector's (`-d`, adaptive by default). A hash detector that never
  cuts (threshold 2) runs beside it and hands each frame's 32×32 grayscale shrink (HashDetector's first step:
  OpenCV's gray conversion and INTER_AREA) to `ShotDiff` (`DetectorSettings.OnShrunk`, internal), also from the
  chunks of a parallel run.
- **A hash per frame.** HashDetector's recipe (scaled by its maximum, the DCT's low 8×8 coefficients against their
  median) on the middle 24×24 of the shrink: the edges, where channel logos, burnt-in timecodes and subtitles sit,
  are left out. The whole frame's hash moved by up to 12 of 64 bits under a small bright logo on test sources, and a
  logo on the Sintel trailer's dark shots broke matches; the middle's doesn't see it. A frame whose middle is flat
  (standard deviation under 2 levels: black, one colour) has a hash that is noise; flat frames match only each other.
- **A fingerprint per shot**: the hashes of 5 frames at fixed places (the middles of five equal parts). A new shot is
  matched to the old shot in which most of them (3) have a frame within 12 bits; among old shots that fit about as
  well, the one the old order expects (after the previous match), so repeated pictures and black shots pair up in
  order.
- **Trims.** The offset between a matched pair's frames is where the new shot's pictures (the shrink's middle at
  12×12) laid over the old one's correlate best, over at least half the shorter shot: correlation ignores a grade's
  gain and offset, and a logo or re-encoding changes every offset alike. A 64-bit hash barely changes over a few frames
  of slow motion (a Mandelbrot zoom read 8 frames trimmed where 10 were), the pictures do. Within 0.001 of the best,
  the offset that keeps the head, else the tail: a still picture fits every offset alike, and edits usually trim one
  end.
- **Moves.** The matched shots in the longest run that keeps the old order (by old shot, then by offset into it, so the
  pieces of a split shot count in order) stay; the others moved. A moved shot keeps its trims.

## Measuring it

`tools/recut.py` makes a new cut of any video with seeded edits (shots removed, moved, trimmed at the head or the tail,
one added) from a lossless one-pass split of the old cut, then a new grade, a logo, another size and encoder quality
(`--changes` picks them), runs `shotdetect diff` and scores precision and recall of the changes by shot, exact
(status, shots and trims to the frame) and by status, and frame by frame: the new cut's frames mapped to the right old
frame, the old cut's frames left out and the new cut's added. The shots' scores assume the detector cuts the new cut
where the pieces were joined and nowhere else; the frames' don't. Its default is a synthetic programme of ffmpeg test sources; a sample
(`tools/make-samples.ps1`) or an open film (`tools/realworld.py --download`) works too. CI checks the same exactly on
a 16-second pair (ShotDiffTests: every change, nothing else, every trim to the frame); the script's numbers on real
footage go in PRs, not here.

What the first measurements showed, and what is left:

- **A cut the detector sees in only one cut.** A black interstitial between two shots is cut in the old cut but joined
  to the next shot in the new one (the grade lifts the black); the joined shot matches the old shot with most of its
  frames and the rest reads as a negative trim, and the black shot as removed. Matching a new shot to a run of old ones
  is a backlog row.
- **Continuous action the detector chops.** Elephants Dream has a strobe the adaptive detector cuts every
  min-scene-len frames, from wherever it starts: in the new cut the pieces start elsewhere, so they read as trimmed by
  a few frames (rightly, for those pieces) and some as removed, though every frame maps well. The frames' scores show
  it; matching runs of shots (the first point) would help there too.
- **Pictures with structure one way only, flat colour, gradients and noise textures** (plain bars, `rgbtestsrc`,
  `gradients`, `cellauto`) hash as noise; such shots read as removed and added. Test sources with structure both ways
  are used for the tests.
