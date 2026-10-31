# Where the YouTube chapters export lives, and where cut-list formats go (2026-10-31)

Issue #648 came from shotdetector-web's page (bisforboman/shotdetector-web#84, its chapters part): a YouTube chapters
export from the shot list. Shots have no titles, and YouTube's chapters need one per line, so someone has to supply
them.

## Where the export lives

### Choices

1. **ShotDetector, with titles on the page**: the library owns the YouTube export and the rule that merges shots into
   chapters, with placeholder titles ("Chapter N"); the page wraps it through its TimelineFiles and lets the user edit
   the titles.
2. The page only: the export and the rule in shotdetector-web, nothing upstream.
3. Both, the page first: build it on the page, move it upstream later.

### Answer

**1** (the owner, 2026-10-31, through the coordinating session): `Timeline.YouTubeChapters`, with the rule public on
its own as `Timeline.ChapterStarts`, so the Jellyfin plugin (#634), which also picks chapter points from shots, takes
the same points from the same shots ([2026-10-31-youtube-chapters.md](2026-10-31-youtube-chapters.md) has the rule).

## The standing rule

A file format built from a cut list lives on ShotDetector's `Timeline` (beside Edl, Fcpx, Otio, Qp, ShotChanges and
YouTubeChapters); the page owns only the UI around it (the owner, 2026-10-31).
