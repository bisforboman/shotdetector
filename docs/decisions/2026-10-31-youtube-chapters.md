# YouTube chapters from the shot list (2026-10-31)

Issue #648: a YouTube chapters export on `Timeline`, beside Edl, Fcpx, Otio, Qp and ShotChanges, for the CLI and
shotdetector-web's page alike.

YouTube's rules, from its help page "Video Chapters" (support.google.com/youtube/answer/9884579): the first timestamp
in the description is 00:00, there are at least three timestamps in ascending order, and every chapter is at least
10 seconds long (the last one too, to the end of the video). A film's shots are mostly shorter than that, so shots
have to merge into chapters.

## How shots merge into chapters

### Choices

1. **Greedy, with a minimum length**: walk the cuts in order; a cut starts a new chapter only when the current
   chapter has run at least the minimum (10 s by default, an option to raise it, e.g. a minute for a film). A last
   chapter shorter than the minimum joins the one before. Fewer than three chapters is an error and writes nothing.
2. Strongest cuts first: keep the cuts with the highest detector scores that still meet the spacing, up to a chapter
   count. Follows the big visual changes, but depends on scores, which not every detector makes comparable.
3. A fixed count spread evenly: the cut nearest each 1/N of the length, then fix the 10 s rule.

### Answer

**1** (the owner, 2026-10-31): predictable, and the page gives the same answer from the same shots.

## Timestamps and titles

YouTube's timestamps are whole seconds (m:ss, or h:mm:ss from an hour), and each line needs a title.

### Choices

1. **Round each cut's time up, titles "Chapter N"**: clicking a chapter lands in its first shot, never on the
   previous shot's last frames; the minimum length is checked on the written times. The user renames the chapters.
2. Round down, "Chapter N": a chapter can start up to a second before its cut.
3. Round up, titled by the shots each covers ("Shots 1-4").

### Answer

**1** (the owner, 2026-10-31).
