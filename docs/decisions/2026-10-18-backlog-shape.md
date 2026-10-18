# The backlog's shape: Next, Waiting, Maybe (2026-10-18)

### Question

How should the backlog be reshaped? Today it's 833 lines: ~180 Done items stay in their original sections (2.0,
FrameReader split and 3.0 are 100% Done), new ideas pile into 'After 1.0', and the empty '## Done' heading was never
used.

### Choices

1. Next / Maybe / archive (recommended): docs/backlog.md becomes ~70 lines: 'Next' (Open items in priority order),
   'Waiting' (owner/external), 'Maybe'; every Done item moves verbatim to docs/backlog-done.md under its old section
   heading; CLAUDE.md and the backlog skill updated, a decision record.
2. Done to the bottom of the same file.
3. Leave as is.

### Answer

**1**. docs/backlog.md has Next, Waiting and Maybe; docs/backlog-done.md has every finished row (Done, Released,
Dropped, Won't do) byte for byte under the section it was in, and rows finished from now on go at its end.

### Question

Which Maybe items should be dropped (marked Dropped with today's date)? (Several could be picked.)

### Choices

1. Hardware decoding on Linux and macOS (VAAPI, VideoToolbox).
2. The cap on the probe's packet-timestamp lists.
3. split-video `--mkvmerge` (the part left of 'scenedetect features still missing').
4. The shared exception base for 3.0 (the part left of 'Exceptions (issue #239)').

### Answer

**All four**, each now Dropped (2026-10-18) and moved to docs/backlog-done.md with the rest of its row.

Correction: choice 4 came from a stale row. The shared base had already landed in 3.0 (`MediaException`, 2026-10-14, #290), so that row's part is marked Done there instead of Dropped; the other three are Dropped.
